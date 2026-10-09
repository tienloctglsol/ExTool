using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(ExTool.App))]
[assembly: CommandClass(typeof(ExTool.MakeBlockCommand))]
[assembly: CommandClass(typeof(ExTool.PipeBlockCommand))]

namespace ExTool
{
    public class App : IExtensionApplication
    {
        // Theo ExtendApplication của TakaCAD: XAML của LoadingLib nạp assembly theo tên,
        // CLR không tự tìm trong thư mục plugin nên thiếu handler này cửa sổ loading không hiện
        static App()
        {
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
        }

        private static Assembly? CurrentDomain_AssemblyResolve(object? sender, ResolveEventArgs args)
        {
            string requestedName = new AssemblyName(args.Name).Name ?? "";
            // Tránh trả về assembly khác instance nếu đã load rồi
            Assembly? existing = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => !a.IsDynamic && string.Equals(a.GetName().Name, requestedName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            string fullPath = Path.Combine(folder, requestedName + ".dll");
            return File.Exists(fullPath) ? Assembly.LoadFrom(fullPath) : null;
        }

        public void Initialize()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage("\nĐã tải ExTool.");
            PipeMarkerSelection.Start();
        }

        public void Terminate()
        {
            PipeMarkerSelection.Stop();
        }
    }
}
