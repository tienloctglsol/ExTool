using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace ExTool.Backend.Core
{
	// Lấy từ TakaCAD (TakaUIService/SystemHelper.cs), nhánh AutoCAD
	public static class SystemHelper
	{
		// Trả focus về cửa sổ CAD, gọi sau khi đóng form WPF để prompt chọn đối tượng nhận chuột/phím ngay
		public static void FocusMainWindow()
		{
			CadApp.MainWindow.Focus();
		}
	}
}
