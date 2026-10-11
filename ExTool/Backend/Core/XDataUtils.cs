using Autodesk.AutoCAD.DatabaseServices;

namespace ExTool.Backend.Core
{
	internal static class XDataUtils
	{
		public static void EnsureRegApp(Transaction tr, Database db, string appName)
		{
			var regAppTable = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
			if (regAppTable.Has(appName)) return;

			regAppTable.UpgradeOpen();
			var record = new RegAppTableRecord { Name = appName };
			regAppTable.Add(record);
			tr.AddNewlyCreatedDBObject(record, true);
		}
	}
}
