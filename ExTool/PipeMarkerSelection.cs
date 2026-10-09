using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace ExTool
{
	// Chọn rectangle đánh dấu của GOM (đỏ/vàng) thì tự chọn kèm các line liên kết lưu trong XData,
	// áp dụng cho cả chọn khi không có lệnh (pick first) lẫn chọn trong lệnh (MOVE, COPY...)
	internal static class PipeMarkerSelection
	{
		private static readonly RXClass PolylineClass = RXObject.GetClass(typeof(Polyline));
		private static bool _busy;

		// Bật trong lúc lệnh của ExTool cần lấy đúng rectangle (VD: XOAGOM)
		public static bool Suspended;

		public static void Start()
		{
			DocumentCollection docs = Application.DocumentManager;
			foreach (Document doc in docs)
				Attach(doc);
			docs.DocumentCreated += OnDocumentCreated;
			docs.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
		}

		public static void Stop()
		{
			DocumentCollection docs = Application.DocumentManager;
			docs.DocumentCreated -= OnDocumentCreated;
			docs.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
			foreach (Document doc in docs)
				doc.Editor.SelectionAdded -= OnSelectionAdded;
		}

		private static void Attach(Document doc)
		{
			doc.Editor.SelectionAdded -= OnSelectionAdded;
			doc.Editor.SelectionAdded += OnSelectionAdded;
		}

		private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e) => Attach(e.Document);

		private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e) =>
			e.Document.Editor.SelectionAdded -= OnSelectionAdded;

		private static void OnSelectionAdded(object sender, SelectionAddedEventArgs e)
		{
			if (_busy || Suspended) return;
			SelectionSet? added = e.AddedObjects;
			if (added == null || added.Count == 0) return;

			_busy = true;
			try
			{
				// Chỉ mở polyline, các loại khác bỏ qua ngay để không chậm khi chọn cả mặt bằng
				var links = new List<ObjectId>();
				ObjectId[] ids = added.GetObjectIds();
				using (Transaction tr = ids[0].Database.TransactionManager.StartOpenCloseTransaction())
				{
					foreach (ObjectId id in ids)
					{
						if (id.IsErased || id.ObjectClass != PolylineClass) continue;
						if (tr.GetObject(id, OpenMode.ForRead) is Entity ent)
							links.AddRange(PipeBlockCommand.GetMarkerLinks(ent));
					}
					tr.Commit();
				}
				if (links.Count == 0) return;

				var selected = new HashSet<ObjectId>(e.Selection.GetObjectIds());
				foreach (ObjectId link in links)
				{
					if (selected.Add(link))
						e.Add(new SelectedObject(link, SelectionMethod.NonGraphical, IntPtr.Zero));
				}
			}
			catch (System.Exception)
			{
				// Lỗi ở đây không được làm hỏng thao tác chọn của user
			}
			finally
			{
				_busy = false;
			}
		}
	}
}
