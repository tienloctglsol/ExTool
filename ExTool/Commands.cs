using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

namespace ExTool
{
    public class Commands
    {
        [CommandMethod("EX_HELLO")]
        public void Hello()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
#if A21
            ed.WriteMessage("\nExTool - AutoCAD 2021 (net48)");
#elif A25
            ed.WriteMessage("\nExTool - AutoCAD 2025 (net8)");
#endif
        }
    }
}
