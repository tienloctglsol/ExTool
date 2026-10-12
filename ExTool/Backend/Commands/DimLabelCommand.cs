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
using ExTool.Frontend.Views;

namespace ExTool.Backend.Commands
{
	// Quét vùng -> lọc block ống (XData TBL/GOM) -> mỗi block vẽ 1 dim aligned theo cạnh dài bên trái, text quay ra ngoài ống, màu theo layer block
	public class DimLabelCommand
	{
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
					List<PipeSegment> segs = PipeGeometry.CollectSegments(tr, blockRef);

					(LineSegment3d Edge, Vector3d Out)? left = GetLeftEdge(segs);
					if (left == null)
					{
						skipped++;
						continue;
					}
					(LineSegment3d edge, Vector3d outward) = left.Value;
					var dim = new AlignedDimension(edge.StartPoint, edge.EndPoint, edge.MidPoint + outward, "", dimStyleId);
					dim.SetDatabaseDefaults(db);
					dim.DimensionStyle = dimStyleId;
					dim.LayerId = layerId;
					dim.Dimscale = settings.Scale;
					dim.Dimtad = TextOutside;
					dim.Dimrnd = settings.Round;
					// Đã làm tròn theo số nguyên nên chỉ hiện phần nguyên
					dim.Dimdec = 0;
					// Text, đường dim, 2 đường gióng cùng màu layer block ống
					Color color = GetLayerColor(tr, blockRef.LayerId);
					dim.Dimclrt = color;
					dim.Dimclrd = color;
					dim.Dimclre = color;
					// Mép text gần ống cách cạnh ống đúng TextOffset: đường dim lùi vào 1 khoảng DIMGAP (tối thiểu cách ống 1 đơn vị)
					double lineOffset = Math.Max(1, settings.TextOffset - Math.Abs(dim.Dimgap) * settings.Scale);
					dim.DimLinePoint = edge.MidPoint + outward * lineOffset;
					space.AppendEntity(dim);
					tr.AddNewlyCreatedDBObject(dim, true);
					count++;
				}
				tr.Commit();
			}
			return count;
		}

		// Color index của layer block ống; layer dùng true color thì giữ nguyên màu đó
		private static Color GetLayerColor(Transaction tr, ObjectId layerId)
		{
			Color color = ((LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead)).Color;
			return color.IsByAci ? Color.FromColorIndex(ColorMethod.ByAci, color.ColorIndex) : color;
		}

		// Cạnh bên trái trong 2 cạnh biên, Out chỉ ra ngoài block
		private static (LineSegment3d Edge, Vector3d Out)? GetLeftEdge(List<PipeSegment> segs)
		{
			PipeEdges? edges = PipeGeometry.FindEdges(segs);
			if (edges == null) return null;
			if (!edges.IsSingle)
				return (edges.LeftEdges[0].Seg, edges.Left);

			// Chỉ có 1 cạnh: đặt dim về phía xa tâm các đoạn còn lại
			LineSegment3d longest = edges.Longest.Seg;
			Point3d center = Point3d.Origin + segs.Aggregate(new Vector3d(), (sum, s) => sum + s.Seg.MidPoint.GetAsVector()) / segs.Count;
			return (longest, (longest.MidPoint - center).DotProduct(edges.Left) < 0 ? -edges.Left : edges.Left);
		}
	}
}
