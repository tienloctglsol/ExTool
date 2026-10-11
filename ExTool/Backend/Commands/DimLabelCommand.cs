using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using ExTool.Backend.Core;
using ExTool.Backend.Models;
using ExTool.Frontend.Views;

namespace ExTool.Backend.Commands
{
	// Quét vùng -> lọc block ống (XData TBL/GOM) -> mỗi block vẽ 2 dim aligned 2 bên theo 2 cạnh dài nhất, text quay ra ngoài ống
	public class DimLabelCommand
	{
		private const double ParallelTolerance = 1e-3;
		// Cạnh song song ngắn hơn tỉ lệ này so với cạnh dài nhất thì không tính là cạnh ống
		private const double MinEdgeRatio = 0.5;
		private const int MaxNestedDepth = 4;
		// DIMTAD = 2: text nằm phía đường dim xa điểm gốc dim (cạnh ống), tức là phía ngoài ống
		private const int TextOutside = 2;

		private static DimLabelSettings? _settings;

		[CommandMethod("DAO", CommandFlags.Modal | CommandFlags.Redraw)]
		public void DimLabel()
		{
			Database db = CADUtil.Database();
			Editor ed = CADUtil.Editor();

			if (!ShowSettings(db)) return;
			// Đóng form WPF xong trả focus về CAD để quét chọn ngay
			SystemHelper.FocusMainWindow();

			List<ObjectId>? blocks = SelectPipeBlocks(ed, db);
			if (blocks == null) return;

			int dimCount = AddDims(db, _settings!, blocks, out int skipped);
			ed.WriteMessage($"\nĐã vẽ {dimCount} dim cho {blocks.Count - skipped} block ống.");
			if (skipped > 0)
				ed.WriteMessage($"\nBỏ qua {skipped} block không có cạnh thẳng.");
		}

		private static bool ShowSettings(Database db)
		{
			var styles = new List<string>();
			var layers = new List<string>();
			string currentStyle, currentLayer;
			using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
			{
				var dimStyleTable = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
				foreach (ObjectId id in dimStyleTable)
				{
					var record = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
					if (!record.IsDependent) styles.Add(record.Name);
				}
				var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
				foreach (ObjectId id in layerTable)
				{
					var record = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
					if (!record.IsDependent) layers.Add(record.Name);
				}
				currentStyle = ((DimStyleTableRecord)tr.GetObject(db.Dimstyle, OpenMode.ForRead)).Name;
				currentLayer = ((LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead)).Name;
				tr.Commit();
			}
			styles.Sort(StringComparer.OrdinalIgnoreCase);
			layers.Sort(StringComparer.OrdinalIgnoreCase);

			// Lần đầu lấy theo dim style, layer, DIMSCALE hiện hành
			var settings = _settings ?? new DimLabelSettings
			{
				DimStyle = currentStyle,
				Layer = currentLayer,
				Scale = db.Dimscale > 0 ? db.Dimscale : 100
			};
			var window = new DimLabelWindow(settings, styles, layers);
			if (Application.ShowModalWindow(window) != true) return false;
			_settings = settings;
			return true;
		}

		// Quét vùng rồi chỉ giữ block có XData của TBL/GOM
		private static List<ObjectId>? SelectPipeBlocks(Editor ed, Database db)
		{
			var opts = new PromptSelectionOptions { MessageForAdding = "\nQuét chọn vùng có ống nhựa: " };
			var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
			PromptSelectionResult sel = ed.GetSelection(opts, filter);
			if (sel.Status != PromptStatus.OK) return null;

			var blocks = new List<ObjectId>();
			using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
			{
				foreach (ObjectId id in sel.Value.GetObjectIds())
				{
					if (BlockTag.IsPipe((Entity)tr.GetObject(id, OpenMode.ForRead)))
						blocks.Add(id);
				}
				tr.Commit();
			}

			int others = sel.Value.Count - blocks.Count;
			if (blocks.Count == 0)
			{
				ed.WriteMessage("\nKhông có block ống nào (tạo bằng TBL/GOM) trong vùng chọn.");
				return null;
			}
			ed.WriteMessage(others > 0
				? $"\nTìm thấy {blocks.Count} block ống, bỏ qua {others} block khác."
				: $"\nTìm thấy {blocks.Count} block ống.");
			return blocks;
		}

		private static int AddDims(Database db, DimLabelSettings settings, List<ObjectId> blockIds, out int skipped)
		{
			int count = 0;
			skipped = 0;
			using (Transaction tr = db.TransactionManager.StartTransaction())
			{
				var dimStyleTable = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
				var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
				// Dim style/layer bị xóa sau khi đóng cửa sổ thì dùng cái hiện hành
				ObjectId dimStyleId = dimStyleTable.Has(settings.DimStyle) ? dimStyleTable[settings.DimStyle] : db.Dimstyle;
				ObjectId layerId = layerTable.Has(settings.Layer) ? layerTable[settings.Layer] : db.Clayer;
				var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

				foreach (ObjectId id in blockIds)
				{
					if (id.IsErased) continue;
					var blockRef = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
					var segs = new List<LineSegment3d>();
					CollectSegments(tr, blockRef.BlockTableRecord, blockRef.BlockTransform, segs, 0);

					List<(LineSegment3d Edge, Vector3d Out)> edges = GetLongestEdges(segs);
					if (edges.Count == 0)
					{
						skipped++;
						continue;
					}
					foreach ((LineSegment3d edge, Vector3d outward) in edges)
					{
						var dim = new AlignedDimension(edge.StartPoint, edge.EndPoint, edge.MidPoint + outward, "", dimStyleId);
						dim.SetDatabaseDefaults(db);
						dim.DimensionStyle = dimStyleId;
						dim.LayerId = layerId;
						dim.Dimscale = settings.Scale;
						dim.Dimtad = TextOutside;
						// Mép text gần ống cách cạnh ống đúng TextOffset: đường dim lùi vào 1 khoảng DIMGAP (tối thiểu cách ống 1 đơn vị)
						double lineOffset = Math.Max(1, settings.TextOffset - Math.Abs(dim.Dimgap) * settings.Scale);
						dim.DimLinePoint = edge.MidPoint + outward * lineOffset;
						space.AppendEntity(dim);
						tr.AddNewlyCreatedDBObject(dim, true);
						count++;
					}
				}
				tr.Commit();
			}
			return count;
		}

		// Line và đoạn thẳng của polyline trong block (kể cả block lồng), đổi sang WCS
		private static void CollectSegments(Transaction tr, ObjectId btrId, Matrix3d transform, List<LineSegment3d> segs, int depth)
		{
			var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
			foreach (ObjectId id in btr)
			{
				if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent) || !ent.Visible) continue;
				if (ent is Line line)
					AddSegment(segs, line.StartPoint, line.EndPoint, transform);
				else if (ent is Polyline pline)
				{
					int segCount = pline.Closed ? pline.NumberOfVertices : pline.NumberOfVertices - 1;
					for (int i = 0; i < segCount; i++)
					{
						if (pline.GetSegmentType(i) != SegmentType.Line) continue;
						LineSegment3d seg = pline.GetLineSegmentAt(i);
						AddSegment(segs, seg.StartPoint, seg.EndPoint, transform);
					}
				}
				else if (ent is BlockReference nested && depth < MaxNestedDepth)
					CollectSegments(tr, nested.BlockTableRecord, transform * nested.BlockTransform, segs, depth + 1);
			}
		}

		private static void AddSegment(List<LineSegment3d> segs, Point3d start, Point3d end, Matrix3d transform)
		{
			Point3d s = start.TransformBy(transform), e = end.TransformBy(transform);
			if (s.DistanceTo(e) > Tolerance.Global.EqualPoint) segs.Add(new LineSegment3d(s, e));
		}

		// 2 cạnh ngoài cùng song song với cạnh dài nhất (bỏ đường tim ở giữa), hướng Out chỉ ra ngoài block
		private static List<(LineSegment3d Edge, Vector3d Out)> GetLongestEdges(List<LineSegment3d> segs)
		{
			var result = new List<(LineSegment3d, Vector3d)>();
			if (segs.Count == 0) return result;

			LineSegment3d longest = segs.OrderByDescending(s => s.Length).First();
			Vector3d u = longest.Direction.GetNormal();
			var n = new Vector3d(-u.Y, u.X, 0).GetNormal();
			Point3d origin = longest.StartPoint;

			var parallels = segs
				.Where(s => s.Length >= longest.Length * MinEdgeRatio && s.Direction.GetNormal().CrossProduct(u).Length <= ParallelTolerance)
				.Select(s => (Seg: s, Offset: (s.MidPoint - origin).DotProduct(n)))
				.ToList();
			var low = parallels.OrderBy(p => p.Offset).ThenByDescending(p => p.Seg.Length).First();
			var high = parallels.OrderByDescending(p => p.Offset).ThenByDescending(p => p.Seg.Length).First();

			if (high.Offset - low.Offset > Tolerance.Global.EqualPoint)
			{
				result.Add((low.Seg, -n));
				result.Add((high.Seg, n));
				return result;
			}

			// Chỉ có 1 cạnh: đặt dim về phía xa tâm các đoạn còn lại
			Point3d center = Point3d.Origin + segs.Aggregate(new Vector3d(), (sum, s) => sum + s.MidPoint.GetAsVector()) / segs.Count;
			result.Add((longest, (longest.MidPoint - center).DotProduct(n) < 0 ? -n : n));
			return result;
		}
	}
}
