using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace ExTool
{
	public class MakeBlockCommand
	{
		// Gom đối tượng thành anonymous block, base point = góc dưới trái (WCS) của bao các đối tượng.
		// Lặp chọn -> Enter -> tạo block cho tới khi Esc; block vừa tạo được highlight tới lúc kết thúc lệnh.
		[CommandMethod("TBL", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
		public void MakeBlock()
		{
			Document doc = Application.DocumentManager.MdiActiveDocument;
			Database db = doc.Database;
			Editor ed = doc.Editor;

			var created = new List<ObjectId>();
			try
			{
				PromptSelectionResult implied = ed.SelectImplied();
				if (implied.Status == PromptStatus.OK)
				{
					ed.SetImpliedSelection(Array.Empty<ObjectId>());
					AddHighlighted(db, created, CreateBlock(db, ed, implied.Value.GetObjectIds()));
				}

				var opts = new PromptSelectionOptions { MessageForAdding = "\nChọn đối tượng <Esc để thoát>: " };
				while (true)
				{
					PromptSelectionResult res = ed.GetSelection(opts);
					if (res.Status == PromptStatus.Cancel) return;
					if (res.Status != PromptStatus.OK) continue;
					AddHighlighted(db, created, CreateBlock(db, ed, res.Value.GetObjectIds()));
				}
			}
			finally
			{
				SetHighlight(db, created, false);
			}
		}

		private static void AddHighlighted(Database db, List<ObjectId> created, ObjectId blockRefId)
		{
			if (blockRefId.IsNull) return;
			created.Add(blockRefId);
			SetHighlight(db, new[] { blockRefId }, true);
		}

		// Block cũ có thể đã bị gom vào block mới (bị erase) nên bỏ qua id đã erase
		private static void SetHighlight(Database db, IEnumerable<ObjectId> ids, bool on)
		{
			using (Transaction tr = db.TransactionManager.StartTransaction())
			{
				foreach (ObjectId id in ids)
				{
					if (id.IsErased) continue;
					var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
					if (on) ent.Highlight();
					else ent.Unhighlight();
				}
				tr.Commit();
			}
		}

		private static ObjectId CreateBlock(Database db, Editor ed, ObjectId[] selected)
		{
			using (Transaction tr = db.TransactionManager.StartTransaction())
			{
				var ids = new ObjectIdCollection();
				var extents = new Extents3d();
				bool hasExtents = false;
				int skipped = 0;

				foreach (ObjectId id in selected)
				{
					var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
					var layer = (LayerTableRecord)tr.GetObject(ent.LayerId, OpenMode.ForRead);
					if (layer.IsLocked)
					{
						skipped++;
						continue;
					}

					ids.Add(id);
					// Xline, ray, text rỗng... không có extents, vẫn đưa vào block nhưng không tính góc
					Extents3d? entExtents = ent.Bounds;
					if (entExtents == null) continue;
					if (hasExtents) extents.AddExtents(entExtents.Value);
					else extents = entExtents.Value;
					hasExtents = true;
				}

				if (skipped > 0)
					ed.WriteMessage($"\nBỏ qua {skipped} đối tượng nằm trên layer khóa.");
				if (!hasExtents)
				{
					ed.WriteMessage("\nKhông xác định được phạm vi của các đối tượng đã chọn.");
					return ObjectId.Null;
				}

				// "*U" -> AutoCAD tự đánh số *U<n>
				Point3d basePoint = extents.MinPoint;
				ObjectId blockRefId = BlockBuilder.Create(tr, db, ids, "*U", basePoint, ObjectId.Null);
				tr.Commit();
				ed.WriteMessage($"\nĐã tạo block vô danh từ {ids.Count} đối tượng, điểm chèn ({basePoint.X:0.###}, {basePoint.Y:0.###}).");
				return blockRefId;
			}
		}
	}
}
