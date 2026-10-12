using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ExTool.Backend.Models;

namespace ExTool.Frontend.Views
{
	// Sơ đồ ống vẽ trực tiếp bằng DrawingContext: mỗi màu 1 StreamGeometry tọa độ thật, dùng chung 1 MatrixTransform.
	// Zoom/pan chỉ đổi Matrix, không dựng lại hình; transform đặt trên geometry nên nét không dày lên khi zoom.
	public class PipeOverview : FrameworkElement
	{
		private const double ViewMargin = 16;
		private const double ZoomStep = 1.2;
		// Bấm cách nét ống trong khoảng này (pixel) thì tính là bấm trúng ống
		private const double HitPixels = 6;

		private static readonly Pen CenterPen = FrozenPen(Color.FromArgb(0x90, 0x8A, 0x8F, 0x98), 0.8);
		// Ống bị bộ lọc ẩn: vẽ mờ để vẫn thấy vị trí
		private static readonly Pen HiddenPen = FrozenPen(Color.FromArgb(0x50, 0x8A, 0x8F, 0x98), 1);
		private static readonly Pen SelectPen = FrozenPen(Color.FromArgb(0x99, 0x2D, 0x7F, 0xF9), 9);

		private readonly MatrixTransform _view = new MatrixTransform();
		private List<PipeColorRow> _rows = new List<PipeColorRow>();
		private Rect _bounds = Rect.Empty;
		private double _zoom = 1, _scale = 1;
		private Vector _pan;
		private Point? _panStart;

		private Geometry? _centers, _selection;
		private readonly List<(Pen Pen, Geometry Geometry)> _edges = new List<(Pen, Geometry)>();
		// Mảng phẳng các đoạn để dò ống khi bấm
		private Point[] _segStart = Array.Empty<Point>(), _segEnd = Array.Empty<Point>();
		private PipeColorRow[] _segRow = Array.Empty<PipeColorRow>();

		// Bấm trúng ống (row) hoặc chỗ trống (null); bool = đang giữ Ctrl
		public event Action<PipeColorRow?, bool>? PipeClicked;

		public PipeOverview()
		{
			ClipToBounds = true;
			Focusable = false;
		}

		private static Pen FrozenPen(Color color, double thickness)
		{
			var pen = new Pen(new SolidColorBrush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
			pen.Freeze();
			return pen;
		}

		public void SetRows(List<PipeColorRow> rows)
		{
			_rows = rows;
			int count = rows.Sum(r => r.Lines.Count());
			_segStart = new Point[count];
			_segEnd = new Point[count];
			_segRow = new PipeColorRow[count];
			_bounds = Rect.Empty;
			int k = 0;
			foreach (PipeColorRow row in rows)
			{
				foreach (PipeLine line in row.Lines)
				{
					_segStart[k] = new Point(line.Start.X, line.Start.Y);
					_segEnd[k] = new Point(line.End.X, line.End.Y);
					_segRow[k] = row;
					_bounds.Union(_segStart[k]);
					_bounds.Union(_segEnd[k]);
					k++;
				}
			}
			_centers = Build(rows.SelectMany(r => r.Lines.Where(l => !l.IsEdge)));
			RefreshColors();
			UpdateView();
		}

		// Gom nét biên theo màu đang hiển thị (màu mới nếu đã chọn); gọi lại sau khi đổi màu hoặc đổi bộ lọc
		public void RefreshColors()
		{
			_edges.Clear();
			List<PipeColorRow> hidden = _rows.Where(r => !r.IsVisible).ToList();
			if (hidden.Count > 0)
				_edges.Add((HiddenPen, Build(hidden.SelectMany(r => r.Lines.Where(l => l.IsEdge)))));
			foreach (var group in _rows.Where(r => r.IsVisible).GroupBy(r => ((SolidColorBrush)r.DisplayBrush).Color))
			{
				var pen = new Pen(new SolidColorBrush(group.Key), 1.5);
				pen.Freeze();
				_edges.Add((pen, Build(group.SelectMany(r => r.Lines.Where(l => l.IsEdge)))));
			}
			InvalidateVisual();
		}

		public void SetSelection(IEnumerable<PipeColorRow> rows)
		{
			_selection = Build(rows.SelectMany(r => r.Lines));
			InvalidateVisual();
		}

		private Geometry Build(IEnumerable<PipeLine> lines)
		{
			var geometry = new StreamGeometry();
			using (StreamGeometryContext ctx = geometry.Open())
			{
				foreach (PipeLine line in lines)
				{
					ctx.BeginFigure(new Point(line.Start.X, line.Start.Y), false, false);
					ctx.LineTo(new Point(line.End.X, line.End.Y), true, false);
				}
			}
			geometry.Transform = _view;
			return geometry;
		}

		protected override void OnRender(DrawingContext dc)
		{
			// Nền trong suốt để nhận chuột cả chỗ trống
			dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
			if (_selection != null) dc.DrawGeometry(null, SelectPen, _selection);
			if (_centers != null) dc.DrawGeometry(null, CenterPen, _centers);
			foreach ((Pen pen, Geometry geometry) in _edges) dc.DrawGeometry(null, pen, geometry);
		}

		protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
		{
			base.OnRenderSizeChanged(sizeInfo);
			UpdateView();
		}

		// Fit toàn bộ ống vào khung, nhân zoom rồi dời pan (pixel)
		private void UpdateView()
		{
			double w = ActualWidth, h = ActualHeight;
			if (w <= 0 || h <= 0 || _bounds.IsEmpty) return;
			double dx = Math.Max(_bounds.Width, 1e-6), dy = Math.Max(_bounds.Height, 1e-6);
			_scale = Math.Max(1e-9, Math.Min((w - 2 * ViewMargin) / dx, (h - 2 * ViewMargin) / dy)) * _zoom;

			var m = Matrix.Identity;
			m.Translate(-(_bounds.Left + _bounds.Right) / 2, -(_bounds.Top + _bounds.Bottom) / 2);
			m.Scale(_scale, -_scale);
			m.Translate(w / 2 + _pan.X, h / 2 + _pan.Y);
			_view.Matrix = m;
		}

		// Giữ điểm dưới con trỏ đứng yên khi zoom
		protected override void OnMouseWheel(MouseWheelEventArgs e)
		{
			double factor = e.Delta > 0 ? ZoomStep : 1 / ZoomStep;
			Vector cursor = e.GetPosition(this) - new Point(ActualWidth / 2, ActualHeight / 2);
			_pan = cursor - (cursor - _pan) * factor;
			_zoom *= factor;
			UpdateView();
			e.Handled = true;
		}

		protected override void OnMouseDown(MouseButtonEventArgs e)
		{
			if (e.ChangedButton == MouseButton.Middle)
			{
				if (e.ClickCount == 2)
				{
					_zoom = 1;
					_pan = new Vector();
					UpdateView();
					return;
				}
				_panStart = e.GetPosition(this);
				CaptureMouse();
				e.Handled = true;
			}
			else if (e.ChangedButton == MouseButton.Left)
			{
				PipeClicked?.Invoke(HitTest(e.GetPosition(this)), (Keyboard.Modifiers & ModifierKeys.Control) != 0);
				e.Handled = true;
			}
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			if (_panStart == null) return;
			Point p = e.GetPosition(this);
			_pan += p - _panStart.Value;
			_panStart = p;
			UpdateView();
		}

		protected override void OnMouseUp(MouseButtonEventArgs e)
		{
			if (e.ChangedButton != MouseButton.Middle || _panStart == null) return;
			_panStart = null;
			ReleaseMouseCapture();
		}

		// Đoạn gần điểm bấm nhất trong phạm vi HitPixels (bỏ ống bị lọc ẩn), tính trong tọa độ thật
		private PipeColorRow? HitTest(Point screen)
		{
			Matrix inverse = _view.Matrix;
			if (!inverse.HasInverse) return null;
			inverse.Invert();
			Point p = inverse.Transform(screen);
			double best = HitPixels / _scale;
			best *= best;
			PipeColorRow? hit = null;
			for (int i = 0; i < _segStart.Length; i++)
			{
				if (!_segRow[i].IsVisible) continue;
				double d = DistanceSquared(p, _segStart[i], _segEnd[i]);
				if (d > best) continue;
				best = d;
				hit = _segRow[i];
			}
			return hit;
		}

		private static double DistanceSquared(Point p, Point a, Point b)
		{
			Vector ab = b - a, ap = p - a;
			double len2 = ab.LengthSquared;
			double t = len2 <= 0 ? 0 : Math.Max(0, Math.Min(1, (ap * ab) / len2));
			return (ap - ab * t).LengthSquared;
		}
	}
}
