using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace ExTool.Backend.Models
{
	// Đoạn thẳng của ống (WCS) để vẽ minh họa; IsEdge = line biên, còn lại là tim
	public struct PipeLine
	{
		public Point2d Start, End;
		public bool IsEdge;
	}

	// 1 định nghĩa block ống (các block reference copy chung định nghĩa đổi màu cùng nhau) + dim đi kèm
	public class PipeColorItem
	{
		public ObjectId BlockDefId;
		public int RefCount;
		// Line/polyline biên cấp 1 trong định nghĩa block
		public List<ObjectId> EdgeIds = new List<ObjectId>();
		public List<ObjectId> DimIds = new List<ObjectId>();
		public List<PipeLine> Lines = new List<PipeLine>();
		// Khoảng cách 2 line biên, làm tròn về số nguyên
		public int Diameter;
		// Layer line biên (line trên layer 0 thì lấy layer block reference)
		public string Layer = "";
		// Màu đang hiển thị (đã quy ByLayer/ByBlock ra màu thật)
		public Color EdgeColor = null!;
		public Color? DimColor;
		public string DimText = "";
		// null = giữ nguyên
		public Color? NewColor;
	}
}
