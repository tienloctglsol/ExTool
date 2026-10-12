using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ExTool.Backend.Models;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorDialog = Autodesk.AutoCAD.Windows.ColorDialog;

namespace ExTool.Frontend.Views
{
	// Dữ liệu vẽ 1 ống nằm ngang trong danh sách (PipePreviewTemplate)
	public class PipePreview
	{
		public Brush EdgeBrush { get; set; } = Brushes.White;
		public Brush DimBrush { get; set; } = Brushes.White;
		public string DimText { get; set; } = "";
		public Visibility DimVisibility { get; set; }
	}

	// 1 dòng = nhóm ống cùng đường kính, layer, màu hiện tại; màu mới áp cho cả nhóm
	public class PipeColorRow : INotifyPropertyChanged
	{
		private static readonly string[] AciNames = { "", "Đỏ", "Vàng", "Xanh lá", "Xanh lơ", "Xanh dương", "Tím", "Trắng" };

		public List<PipeColorItem> Items { get; }
		public int Index { get; set; }
		public int Diameter { get; }
		public string Layer { get; }
		public PipePreview Current { get; }
		public PipePreview New { get; private set; }
		public string CurrentName { get; }
		public string NewName => NewColor == null ? "Giữ nguyên" : ColorName(NewColor);
		public double NewOpacity => NewColor == null ? 0.3 : 1;
		public Visibility KeepVisibility => NewColor == null ? Visibility.Visible : Visibility.Collapsed;
		public string PipeCount { get; }
		public string DimCount { get; }
		// Cùng key = cùng màu hiện tại
		public string ColorKey { get; }
		internal AcColor EdgeColor => Items[0].EdgeColor;
		internal AcColor? NewColor => Items[0].NewColor;
		internal readonly List<PipeLine> Lines;
		// Qua được bộ lọc
		internal bool IsVisible = true;
		internal Brush DisplayBrush = Brushes.White;

		public event PropertyChangedEventHandler? PropertyChanged;

		public PipeColorRow(List<PipeColorItem> items)
		{
			Items = items;
			PipeColorItem first = items[0];
			Diameter = first.Diameter;
			Layer = first.Layer;
			Lines = items.SelectMany(i => i.Lines).ToList();

			List<PipeColorItem> withDims = items.Where(i => i.DimIds.Count > 0).ToList();
			// Nhóm có nhiều số đo dim khác nhau thì chỉ hiện số đầu kèm "…"
			List<string> texts = withDims.Select(i => i.DimText).Distinct().ToList();
			Current = new PipePreview
			{
				EdgeBrush = ToBrush(first.EdgeColor),
				DimBrush = withDims.Count == 0 || withDims[0].DimColor == null ? Brushes.Transparent : ToBrush(withDims[0].DimColor!),
				DimText = texts.Count == 0 ? "" : texts.Count == 1 ? texts[0] : texts[0] + ", …",
				DimVisibility = withDims.Count > 0 ? Visibility.Visible : Visibility.Collapsed
			};
			New = Current;
			CurrentName = ColorName(first.EdgeColor);
			ColorKey = KeyOf(first.EdgeColor);
			int dimCount = items.Sum(i => i.DimIds.Count);
			PipeCount = $"{items.Sum(i => i.RefCount)} ống";
			DimCount = dimCount > 0 ? $"{dimCount} dim" : "Không có dim";
		}

		internal void SetNewColor(AcColor? color)
		{
			foreach (PipeColorItem item in Items) item.NewColor = color;
		}

		// Cập nhật hình màu mới sau khi đổi NewColor
		internal void Refresh(bool changeDims)
		{
			AcColor? color = NewColor;
			if (color == null) New = Current;
			else
			{
				Brush brush = ToBrush(color);
				New = new PipePreview
				{
					EdgeBrush = brush,
					DimBrush = changeDims ? brush : Current.DimBrush,
					DimText = Current.DimText,
					DimVisibility = Current.DimVisibility
				};
			}
			DisplayBrush = New.EdgeBrush;
			foreach (string name in new[] { nameof(New), nameof(NewName), nameof(NewOpacity), nameof(KeepVisibility) })
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		}

		internal static string KeyOf(AcColor color) =>
			color.IsByAci ? $"A{color.ColorIndex}" : $"R{color.Red},{color.Green},{color.Blue}";

		internal static Brush ToBrush(AcColor color)
		{
			System.Drawing.Color value = color.ColorValue;
			var brush = new SolidColorBrush(Color.FromRgb(value.R, value.G, value.B));
			brush.Freeze();
			return brush;
		}

		private static string ColorName(AcColor color)
		{
			if (color.IsByAci)
			{
				short index = color.ColorIndex;
				return index >= 1 && index < AciNames.Length ? $"{index} - {AciNames[index]}" : $"Màu {index}";
			}
			return color.HasColorName ? color.ColorName : $"RGB {color.Red},{color.Green},{color.Blue}";
		}
	}

	// Cửa sổ lệnh MAU: trái là sơ đồ vị trí thật các ống, phải là danh sách nhóm ống + bộ lọc; OK thì ghi NewColor vào item
	public partial class PipeColorWindow : Window
	{
		private readonly List<PipeColorRow> _rows;
		private readonly ICollectionView _view;
		private bool _selectionPending;

		public bool ChangeDims => cbDims.IsChecked == true;

		public PipeColorWindow(List<PipeColorItem> items, bool changeDims)
		{
			InitializeComponent();
			cbDims.IsChecked = changeDims;

			// Gom theo layer + đường kính + màu hiện tại; xếp cùng thứ tự cột
			_rows = items
				.GroupBy(i => (i.Layer, i.Diameter, PipeColorRow.KeyOf(i.EdgeColor)))
				.Select(g => new PipeColorRow(g.ToList()))
				.OrderBy(r => r.Layer, StringComparer.OrdinalIgnoreCase)
				.ThenBy(r => r.Diameter)
				.ThenBy(r => r.EdgeColor.IsByAci ? r.EdgeColor.ColorIndex : short.MaxValue)
				.ThenBy(r => r.ColorKey)
				.ToList();
			for (int i = 0; i < _rows.Count; i++) _rows[i].Index = i + 1;
			foreach (PipeColorRow row in _rows) row.Refresh(changeDims);
			cbDims.Checked += (s, e) => RefreshRows(_rows);
			cbDims.Unchecked += (s, e) => RefreshRows(_rows);

			lvPipes.ItemsSource = _rows;
			_view = CollectionViewSource.GetDefaultView(_rows);
			_view.Filter = o => ((PipeColorRow)o).IsVisible;

			fLayer.SetOptions(_rows.Select(r => r.Layer).Distinct().OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
				.Select(l => new FilterOption { Text = l }));
			fDiameter.SetOptions(_rows.Select(r => r.Diameter).Distinct().OrderBy(d => d)
				.Select(d => new FilterOption { Text = d.ToString() }));
			// Bộ lọc màu kèm ô màu
			fColor.SetOptions(_rows.GroupBy(r => r.CurrentName)
				.Select(g => new FilterOption { Text = g.Key, Swatch = g.First().Current.EdgeBrush }));
			foreach (MultiSelectFilter filter in new[] { fLayer, fDiameter, fColor })
				filter.Changed += (s, e) => ApplyFilter();
			UpdateCount();

			ovPipes.SetRows(_rows);
			ovPipes.PipeClicked += Overview_PipeClicked;
		}

		private void ApplyFilter()
		{
			foreach (PipeColorRow row in _rows)
				row.IsVisible = fLayer.Matches(row.Layer) && fDiameter.Matches(row.Diameter.ToString()) && fColor.Matches(row.CurrentName);
			// Dòng bị ẩn tự rời khỏi vùng chọn
			_view.Refresh();
			ovPipes.RefreshColors();
			UpdateCount();
		}

		private void ClearFilter_Click(object sender, RoutedEventArgs e)
		{
			fLayer.Reset();
			fDiameter.Reset();
			fColor.Reset();
			ApplyFilter();
		}

		private void UpdateCount()
		{
			List<PipeColorRow> visible = _rows.Where(r => r.IsVisible).ToList();
			tbCount.Text = $"{visible.Count}/{_rows.Count} dòng, {visible.Sum(r => r.Items.Sum(i => i.RefCount))} ống";
		}

		// Sơ đồ chỉ gom lại nét theo màu 1 lần cho cả nhóm dòng vừa đổi
		private void RefreshRows(List<PipeColorRow> rows)
		{
			foreach (PipeColorRow row in rows) row.Refresh(ChangeDims);
			ovPipes.RefreshColors();
		}

		private List<PipeColorRow> SelectedRows() => lvPipes.SelectedItems.Cast<PipeColorRow>().ToList();

		private void Overview_PipeClicked(PipeColorRow? row, bool ctrl)
		{
			if (row == null)
			{
				if (!ctrl) lvPipes.SelectedItems.Clear();
				return;
			}
			if (!ctrl) lvPipes.SelectRows(new[] { row });
			else if (lvPipes.SelectedItems.Contains(row)) lvPipes.SelectedItems.Remove(row);
			else lvPipes.SelectedItems.Add(row);
			lvPipes.ScrollIntoView(row);
		}

		// Dồn các lần đổi chọn liên tiếp (Shift/Ctrl+A) thành 1 lần vẽ lại vùng chọn trên sơ đồ
		private void Pipes_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (_selectionPending) return;
			_selectionPending = true;
			Dispatcher.BeginInvoke(new Action(() =>
			{
				_selectionPending = false;
				ovPipes.SetSelection(SelectedRows());
			}), DispatcherPriority.Background);
		}

		// Dòng được bấm nằm trong vùng chọn thì đổi màu cả vùng chọn, không thì chỉ dòng đó
		private void NewColor_Click(object sender, RoutedEventArgs e)
		{
			var row = (PipeColorRow)((FrameworkElement)sender).DataContext;
			if (!lvPipes.SelectedItems.Contains(row)) lvPipes.SelectRows(new[] { row });
			PickColor(SelectedRows(), row);
		}

		private void PickColor_Click(object sender, RoutedEventArgs e)
		{
			List<PipeColorRow> rows = SelectedRows();
			if (rows.Count == 0)
			{
				MessageBox.Show(this, "Chưa chọn dòng nào.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}
			PickColor(rows, rows[0]);
		}

		// Hộp chọn màu của AutoCAD, mở sẵn màu mới (hoặc màu hiện tại) của dòng mẫu
		private void PickColor(List<PipeColorRow> rows, PipeColorRow sample)
		{
			var dialog = new AcColorDialog { Color = sample.NewColor ?? sample.EdgeColor, IncludeByBlockByLayer = false };
			if (dialog.ShowModal() != true) return;
			foreach (PipeColorRow row in rows) row.SetNewColor(dialog.Color);
			RefreshRows(rows);
		}

		private void Keep_Click(object sender, RoutedEventArgs e)
		{
			List<PipeColorRow> rows = SelectedRows();
			foreach (PipeColorRow row in rows) row.SetNewColor(null);
			RefreshRows(rows);
		}

		private void SameColor_Click(object sender, RoutedEventArgs e)
		{
			List<PipeColorRow> selected = SelectedRows();
			if (selected.Count == 0)
			{
				MessageBox.Show(this, "Chọn trước 1 dòng làm mẫu.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}
			var keys = new HashSet<string>(selected.Select(r => r.ColorKey));
			lvPipes.SelectRows(_rows.Where(r => r.IsVisible && keys.Contains(r.ColorKey)).ToList());
		}

		private void Ok_Click(object sender, RoutedEventArgs e)
		{
			DialogResult = true;
		}
	}
}
