using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(ExTool.App))]
[assembly: CommandClass(typeof(ExTool.Commands))]

namespace ExTool
{
    public class App : IExtensionApplication
    {
        public void Initialize()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage("\nExTool loaded.");
        }

        public void Terminate()
        {
        }
    }
}
