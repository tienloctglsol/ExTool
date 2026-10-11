using Autodesk.AutoCAD.DatabaseServices;

namespace ExTool.Backend.Core
{
	// XData đánh dấu block reference do ExTool tạo: app + loại block (TBL/GOM)
	internal static class BlockTag
	{
		public const string App = "EXTOOL_BLOCK";
		public const string ManualKind = "TBL";
		public const string PipeKind = "GOM";

		public static void Set(Transaction tr, Database db, Entity ent, string kind)
		{
			XDataUtils.EnsureRegApp(tr, db, App);
			ent.XData = new ResultBuffer(
				new TypedValue((int)DxfCode.ExtendedDataRegAppName, App),
				new TypedValue((int)DxfCode.ExtendedDataAsciiString, kind));
		}

		// null nếu không phải block của ExTool
		public static string? Get(Entity ent)
		{
			using (ResultBuffer? rb = ent.GetXDataForApplication(App))
			{
				if (rb == null) return null;
				foreach (TypedValue value in rb)
				{
					if (value.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
						return value.Value as string;
				}
			}
			return null;
		}

		// Block ống nhựa = block tạo bằng TBL hoặc GOM
		public static bool IsPipe(Entity ent)
		{
			string? kind = Get(ent);
			return kind == ManualKind || kind == PipeKind;
		}
	}
}
