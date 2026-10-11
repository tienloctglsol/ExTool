using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace ExTool.Backend.Core
{
	// Lấy từ TakaCAD (_CADUtils/CADUtil.cs): truy cập document, editor, database đang mở
	public static class CADUtil
	{
		public static Document CurDoc()
		{
			return CadApp.DocumentManager.MdiActiveDocument;
		}

		public static Editor Editor()
		{
			return CurDoc().Editor;
		}

		public static Database Database()
		{
			return CurDoc().Database;
		}

		public static Transaction StartTransaction()
		{
			return Database().TransactionManager.StartTransaction();
		}
	}
}
