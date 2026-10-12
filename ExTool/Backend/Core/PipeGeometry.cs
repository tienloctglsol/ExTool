using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace ExTool.Backend.Core
{
	// Đoạn thẳng trong block ống (WCS); EntityId là đối tượng cấp 1 trong định nghĩa block, IsNested nếu đoạn nằm trong block lồng
	internal sealed class PipeSegment
	{
		public LineSegment3d Seg = null!;
		public ObjectId EntityId;
		public bool IsNested;
	}

	// 2 cạnh biên ống: các đoạn ngoài cùng song song với đoạn dài nhất (bỏ đường tim). Left chỉ sang trái theo hướng đọc dim.
	internal sealed class PipeEdges
	{
		public Vector3d Left;
		public PipeSegment Longest = null!;
		public List<PipeSegment> LeftEdges = new List<PipeSegment>(), RightEdges = new List<PipeSegment>();
		public double Width;
		// Chỉ có 1 cạnh: LeftEdges và RightEdges là cùng 1 nhóm
		public bool IsSingle => Width <= Tolerance.Global.EqualPoint;
		public IEnumerable<PipeSegment> All => IsSingle ? LeftEdges : LeftEdges.Concat(RightEdges);
	}

	internal static class PipeGeometry
	{
		private const double ParallelTolerance = 1e-3;
		// Cạnh song song ngắn hơn tỉ lệ này so với cạnh dài nhất thì không tính là cạnh ống
		private const double MinEdgeRatio = 0.5;
		private const int MaxNestedDepth = 4;

		// Line và đoạn thẳng của polyline trong block (kể cả block lồng), đổi sang WCS
		public static List<PipeSegment> CollectSegments(Transaction tr, BlockReference blockRef)
		{
			var segs = new List<PipeSegment>();
			Collect(tr, blockRef.BlockTableRecord, blockRef.BlockTransform, segs, ObjectId.Null, 0);
			return segs;
		}

		private static void Collect(Transaction tr, ObjectId btrId, Matrix3d transform, List<PipeSegment> segs, ObjectId topId, int depth)
		{
			var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
			foreach (ObjectId id in btr)
			{
				if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent) || !ent.Visible) continue;
				ObjectId owner = depth == 0 ? id : topId;
				if (ent is Line line)
					Add(segs, line.StartPoint, line.EndPoint, transform, owner, depth > 0);
				else if (ent is Polyline pline)
				{
					int segCount = pline.Closed ? pline.NumberOfVertices : pline.NumberOfVertices - 1;
					for (int i = 0; i < segCount; i++)
					{
						if (pline.GetSegmentType(i) != SegmentType.Line) continue;
						LineSegment3d seg = pline.GetLineSegmentAt(i);
						Add(segs, seg.StartPoint, seg.EndPoint, transform, owner, depth > 0);
					}
				}
				else if (ent is BlockReference nested && depth < MaxNestedDepth)
					Collect(tr, nested.BlockTableRecord, transform * nested.BlockTransform, segs, owner, depth + 1);
			}
		}

		private static void Add(List<PipeSegment> segs, Point3d start, Point3d end, Matrix3d transform, ObjectId owner, bool nested)
		{
			Point3d s = start.TransformBy(transform), e = end.TransformBy(transform);
			if (s.DistanceTo(e) > Tolerance.Global.EqualPoint)
				segs.Add(new PipeSegment { Seg = new LineSegment3d(s, e), EntityId = owner, IsNested = nested });
		}

		// Trái theo hướng đọc dim: ống đứng -> bên trái, ống ngang -> phía trên, ống xiên -> phía trên-trái
		public static PipeEdges? FindEdges(List<PipeSegment> segs)
		{
			if (segs.Count == 0) return null;

			PipeSegment longest = segs.OrderByDescending(s => s.Seg.Length).First();
			Vector3d u = longest.Seg.Direction.GetNormal();
			if (u.X < -ParallelTolerance || (Math.Abs(u.X) <= ParallelTolerance && u.Y < 0)) u = -u;
			var left = new Vector3d(-u.Y, u.X, 0).GetNormal();
			Point3d origin = longest.Seg.StartPoint;

			var parallels = segs
				.Where(s => s.Seg.Length >= longest.Seg.Length * MinEdgeRatio && s.Seg.Direction.GetNormal().CrossProduct(u).Length <= ParallelTolerance)
				.Select(s => (Seg: s, Offset: (s.Seg.MidPoint - origin).DotProduct(left)))
				.ToList();
			double leftMost = parallels.Max(p => p.Offset), rightMost = parallels.Min(p => p.Offset);
			// Các đoạn nối tiếp nhau trên cùng 1 cạnh lệch offset do sai số
			double tol = Math.Max(Tolerance.Global.EqualPoint, longest.Seg.Length * 1e-6);

			return new PipeEdges
			{
				Left = left,
				Longest = longest,
				Width = leftMost - rightMost,
				LeftEdges = parallels.Where(p => p.Offset >= leftMost - tol).OrderByDescending(p => p.Seg.Seg.Length).Select(p => p.Seg).ToList(),
				RightEdges = parallels.Where(p => p.Offset <= rightMost + tol).OrderByDescending(p => p.Seg.Seg.Length).Select(p => p.Seg).ToList()
			};
		}
	}
}
