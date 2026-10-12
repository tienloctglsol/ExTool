using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using ExTool.Backend.Core;
using ExTool.Backend.Models;
using ExTool.Frontend.Loading;
using ExTool.Frontend.Views;

namespace ExTool.Backend.Commands
{
	// Quét vùng -> lọc block ống (TBL/GOM) + dim đi kèm (2 chân dim nằm trên line biên của ống) -> cửa sổ chọn màu mới từng ống
	// -> đổi màu line biên trong định nghĩa block và màu dim (DIMCLRT/D/E)
	public class PipeColorCommand
	{
		// Sai lệch cho phép khi xét chân dim nằm trên line biên, theo bề rộng ống
		private const double MatchRatio = 0.01;

		private static bool _changeDims = true;

		// Line biên của ống (WCS) để dò chân dim
		private sealed class Edge
		{
			public PipeColorItem Item = null!;
			public double X1, Y1, X2, Y2, Tol;
		}

		// Lưới ô vuông chứa line biên: mỗi chân dim chỉ xét line biên trong ô của nó thay vì mọi ống
		private sealed class EdgeGrid
		{
			private readonly double _cell;
			private readonly Dictionary<(long, long), List<Edge>> _cells = new Dictionary<(long, long), List<Edge>>();

			public EdgeGrid(List<Edge> edges)
			{
				double minX = edges.Min(e => Math.Min(e.X1, e.X2)), maxX = edges.Max(e => Math.Max(e.X1, e.X2));
				double minY = edges.Min(e => Math.Min(e.Y1, e.Y2)), maxY = edges.Max(e => Math.Max(e.Y1, e.Y2));
				double avgLength = edges.Average(e => Math.Sqrt((e.X2 - e.X1) * (e.X2 - e.X1) + (e.Y2 - e.Y1) * (e.Y2 - e.Y1)));
				// Ô cỡ chiều dài trung bình line biên, chặn dưới để ống quá dài/xiên không phủ quá nhiều ô
				_cell = Math.Max(Math.Max(avgLength, Math.Max(maxX - minX, maxY - minY) / 2000), 1e-6);

				foreach (Edge e in edges)
				{
					long i0 = Cell(Math.Min(e.X1, e.X2) - e.Tol), i1 = Cell(Math.Max(e.X1, e.X2) + e.Tol);
					long j0 = Cell(Math.Min(e.Y1, e.Y2) - e.Tol), j1 = Cell(Math.Max(e.Y1, e.Y2) + e.Tol);
					for (long i = i0; i <= i1; i++)
					{
						for (long j = j0; j <= j1; j++)
						{
							if (!_cells.TryGetValue((i, j), out List<Edge>? list))
								_cells.Add((i, j), list = new List<Edge>());
							list.Add(e);
						}
					}
				}
			}

			private long Cell(double v) => (long)Math.Floor(v / _cell);

			// Các ống có line biên cách điểm p không quá Tol
			public List<PipeColorItem> ItemsAt(Point3d p)
			{
				var items = new List<PipeColorItem>();
				if (!_cells.TryGetValue((Cell(p.X), Cell(p.Y)), out List<Edge>? list)) return items;
				foreach (Edge e in list)
				{
					if (!items.Contains(e.Item) && Distance(p.X, p.Y, e) <= e.Tol)
						items.Add(e.Item);
				}
				return items;
			}

			private static double Distance(double px, double py, Edge e)
			{
				double dx = e.X2 - e.X1, dy = e.Y2 - e.Y1;
				double len2 = dx * dx + dy * dy;
				double t = len2 <= 0 ? 0 : Math.Max(0, Math.Min(1, ((px - e.X1) * dx + (py - e.Y1) * dy) / len2));
				double ex = e.X1 + dx * t - px, ey = e.Y1 + dy * t - py;
				return Math.Sqrt(ex * ex + ey * ey);
			}
		}

		[CommandMethod("MAU", CommandFlags.Modal | CommandFlags.Redraw)]
		public void PipeColor()
		{
			Database db = CADUtil.Database();
			Editor ed = CADUtil.Editor();

			var opts = new PromptSelectionOptions { MessageForAdding = "\nQuét chọn vùng có ống và dim: " };
			var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT,DIMENSION") });
			PromptSelectionResult sel = ed.GetSelection(opts, filter);
			if (sel.Status != PromptStatus.OK) return;

			ObjectId[] selected = sel.Value.GetObjectIds();
			List<PipeColorItem> items;
			int locked, otherDims;
			PipeColorWindow? window = null;
			try
			{
				ExLoadingUtils.SetMessageLoading("Đang đọc ống và dim...");
				ExLoadingUtils.SetShowCancelButton(true);
				ExLoadingUtils.Start(selected.Length + 1000);
				using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
				{
					items = CollectItems(tr, db, selected, out locked, out otherDims);
					tr.Commit();
				}
				if (items.Count > 0)
				{
					ExLoadingUtils.SetMessageLoading("Đang dựng giao diện...");
					window = new PipeColorWindow(items, _changeDims);
				}
			}
			catch (OperationCanceledException ex)
			{
				ed.WriteMessage($"\n{ex.Message}");
				return;
			}
			finally
			{
				ExLoadingUtils.Close();
			}

			if (locked > 0)
				ed.WriteMessage($"\nBỏ qua {locked} đối tượng nằm trên layer khóa.");
			if (window == null)
			{
				ed.WriteMessage("\nKhông có block ống nào (tạo bằng TBL/GOM) trong vùng chọn.");
				return;
			}
			ed.WriteMessage($"\nTìm thấy {items.Count} ống, {items.Sum(i => i.DimIds.Count)} dim đi kèm.");
			if (otherDims > 0)
				ed.WriteMessage($"\nBỏ qua {otherDims} dim không thuộc ống nào.");

			bool ok = Application.ShowModalWindow(window) == true;
			SystemHelper.FocusMainWindow();
			if (!ok) return;
			_changeDims = window.ChangeDims;

			List<PipeColorItem> changed = items.Where(i => i.NewColor != null).ToList();
			if (changed.Count == 0)
			{
				ed.WriteMessage("\nKhông có ống nào được chọn màu mới.");
				return;
			}

			int changedDims;
			try
			{
				ExLoadingUtils.SetMessageLoading("Đang đổi màu...");
				ExLoadingUtils.SetShowCancelButton(false);
				ExLoadingUtils.Start(changed.Count + 1000);
				changedDims = ChangeColors(db, changed, _changeDims);
				// Sửa định nghĩa block thì block reference phải regen mới vẽ lại
				ed.Regen();
			}
			finally
			{
				ExLoadingUtils.Close();
			}
			ed.WriteMessage($"\nĐã đổi màu {changed.Count} ống, {changedDims} dim.");
		}

		private static int ChangeColors(Database db, List<PipeColorItem> changed, bool changeDims)
		{
			int changedDims = 0;
			using (Transaction tr = db.TransactionManager.StartTransaction())
			{
				foreach (PipeColorItem item in changed)
				{
					Color color = item.NewColor!;
					// Line biên có thể nằm trên layer khóa trong block, vẫn mở để đổi màu
					foreach (ObjectId id in item.EdgeIds.Where(id => !id.IsErased))
						((Entity)tr.GetObject(id, OpenMode.ForWrite, false, true)).Color = color;
					if (!changeDims) continue;

					foreach (ObjectId id in item.DimIds.Where(id => !id.IsErased))
					{
						// Cùng cách tô màu với lệnh DAO: text, đường dim, đường gióng
						var dim = (Dimension)tr.GetObject(id, OpenMode.ForWrite);
						dim.Dimclrt = color;
						dim.Dimclrd = color;
						dim.Dimclre = color;
						changedDims++;
					}
				}
				tr.Commit();
			}
			return changedDims;
		}

		private static List<PipeColorItem> CollectItems(Transaction tr, Database db, ObjectId[] selected, out int locked, out int otherDims)
		{
			locked = 0;
			otherDims = 0;
			var lockedLayers = new HashSet<ObjectId>();
			var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
			foreach (ObjectId id in layerTable)
			{
				if (((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).IsLocked)
					lockedLayers.Add(id);
			}

			var byDef = new Dictionary<ObjectId, PipeColorItem>();
			var edges = new List<Edge>();
			var dims = new List<Dimension>();
			// Báo tiến độ khoảng 100 lần, gọi cửa sổ loading cho từng đối tượng rất chậm
			int step = Math.Max(1, selected.Length / 100);
			for (int n = 0; n < selected.Length; n++)
			{
				if ((n + 1) % step == 0 || n + 1 == selected.Length)
					ExLoadingUtils.ReportProgress("Đang đọc đối tượng", n + 1, selected.Length, 0, 70);

				var ent = (Entity)tr.GetObject(selected[n], OpenMode.ForRead);
				if (lockedLayers.Contains(ent.LayerId))
				{
					locked++;
					continue;
				}
				if (ent is Dimension dim)
				{
					dims.Add(dim);
					continue;
				}
				if (!(ent is BlockReference blockRef) || !BlockTag.IsPipe(blockRef)) continue;

				List<PipeSegment> segs = PipeGeometry.CollectSegments(tr, blockRef);
				PipeEdges? pipeEdges = PipeGeometry.FindEdges(segs);
				// Line biên nằm trong block lồng thì không đổi màu riêng được
				List<ObjectId> edgeIds = pipeEdges == null ? new List<ObjectId>() : pipeEdges.All.Where(s => !s.IsNested).Select(s => s.EntityId).Distinct().ToList();
				if (pipeEdges == null || edgeIds.Count == 0) continue;

				if (!byDef.TryGetValue(blockRef.BlockTableRecord, out PipeColorItem? item))
				{
					var edge = (Entity)tr.GetObject(edgeIds[0], OpenMode.ForRead);
					ObjectId layerId = edge.LayerId == db.LayerZero ? blockRef.LayerId : edge.LayerId;
					item = new PipeColorItem
					{
						BlockDefId = blockRef.BlockTableRecord,
						EdgeIds = edgeIds,
						EdgeColor = ResolveColor(tr, db, edge.Color, edge.LayerId, blockRef),
						Diameter = (int)Math.Round(pipeEdges.Width, MidpointRounding.AwayFromZero),
						Layer = ((LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead)).Name
					};
					byDef.Add(item.BlockDefId, item);
				}
				item.RefCount++;

				var edgeSet = new HashSet<PipeSegment>(pipeEdges.All);
				double tol = Math.Max(Tolerance.Global.EqualPoint, pipeEdges.Width * MatchRatio);
				foreach (PipeSegment seg in segs)
				{
					Point3d s = seg.Seg.StartPoint, e = seg.Seg.EndPoint;
					bool isEdge = edgeSet.Contains(seg);
					item.Lines.Add(new PipeLine { Start = new Point2d(s.X, s.Y), End = new Point2d(e.X, e.Y), IsEdge = isEdge });
					if (isEdge) edges.Add(new Edge { Item = item, X1 = s.X, Y1 = s.Y, X2 = e.X, Y2 = e.Y, Tol = tol });
				}
			}

			if (dims.Count > 0 && edges.Count > 0)
			{
				ExLoadingUtils.SetMessageLoading("Đang gom dim theo ống...");
				var grid = new EdgeGrid(edges);
				step = Math.Max(1, dims.Count / 100);
				for (int n = 0; n < dims.Count; n++)
				{
					if ((n + 1) % step == 0 || n + 1 == dims.Count)
						ExLoadingUtils.ReportProgress("Đang gom dim", n + 1, dims.Count, 70, 95);

					Dimension dim = dims[n];
					PipeColorItem? item = FindOwner(dim, grid);
					if (item == null)
					{
						otherDims++;
						continue;
					}
					item.DimIds.Add(dim.ObjectId);
					if (item.DimColor != null) continue;
					Color color = dim.Dimclrt.IsByBlock ? dim.Color : dim.Dimclrt;
					item.DimColor = ResolveColor(tr, db, color, dim.LayerId, null);
					item.DimText = GetDimText(dim);
				}
			}
			else otherDims = dims.Count;
			return byDef.Values.ToList();
		}

		// Dim thuộc ống khi cả 2 chân dim nằm trên line biên của ống đó (dim dài theo cạnh hoặc dim bề rộng ống)
		private static PipeColorItem? FindOwner(Dimension dim, EdgeGrid grid)
		{
			Point3d p1, p2;
			if (dim is AlignedDimension aligned)
			{
				p1 = aligned.XLine1Point;
				p2 = aligned.XLine2Point;
			}
			else if (dim is RotatedDimension rotated)
			{
				p1 = rotated.XLine1Point;
				p2 = rotated.XLine2Point;
			}
			else return null;

			List<PipeColorItem> first = grid.ItemsAt(p1);
			if (first.Count == 0) return null;
			List<PipeColorItem> second = grid.ItemsAt(p2);
			return first.FirstOrDefault(second.Contains);
		}

		private static string GetDimText(Dimension dim)
		{
			try
			{
				return dim.FormatMeasurement(dim.Measurement, dim.DimensionText);
			}
			catch (Autodesk.AutoCAD.Runtime.Exception)
			{
				return Math.Round(dim.Measurement).ToString("0");
			}
		}

		// Màu thật đang hiển thị: ByBlock lấy màu block reference, ByLayer lấy màu layer
		// (đối tượng trên layer 0 trong block theo layer của block reference)
		private static Color ResolveColor(Transaction tr, Database db, Color color, ObjectId layerId, BlockReference? owner)
		{
			if (color.IsByBlock)
				return owner == null ? Color.FromColorIndex(ColorMethod.ByAci, 7) : ResolveColor(tr, db, owner.Color, owner.LayerId, null);
			if (!color.IsByLayer) return color;
			if (owner != null && layerId == db.LayerZero) layerId = owner.LayerId;
			return ((LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead)).Color;
		}
	}
}
