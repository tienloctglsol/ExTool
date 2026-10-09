using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using ExTool.Loading;

namespace ExTool
{
	// Ống = 2 line biên song song cùng layer (theo các đối tượng mẫu), cách nhau không quá đường kính tối đa,
	// giữa có ít nhất 1 line tim. Mỗi ống thành 1 block gồm 2 biên + các đoạn tim (đoạn vượt đầu ống thì cắt).
	// Line biên không tạo được ống: cặp song song bao rectangle vàng, line lẻ bao rectangle đỏ (xóa bằng XOAGOM).
	public class PipeBlockCommand
	{
		private const double AngleTolerance = 1e-3;
		private const double CenterTolerance = 0.01;
		private const double MinLengthRatio = 1;

		internal const string MarkerApp = "EXTOOL_GOM";
		private const string MarkerLayer = "EX_GOM_MARK";
		private const short PairColor = 2;
		private const short SingleColor = 1;
		private const string PairType = "PAIR";
		private const string SingleType = "SINGLE";
		private const string ClearRed = "Do";
		private const string ClearYellow = "Vang";
		private const string ClearAll = "Tatca";
		// Theo đường kính tối đa: khoảng hở quanh line và độ dày nét rectangle
		private const double MarkerMarginRatio = 0.2;
		private const double MarkerWidthRatio = 0.05;

		private static double _maxDiameter = 400;
		private static string _clearMode = ClearAll;

		// Line hoặc polyline thẳng
		private sealed class Seg
		{
			public ObjectId Id, LayerId;
			public Point2d Start, End;
			public double Angle;
			public bool IsBoundary;
			// Theo hệ trục chung của nhóm cùng hướng, chỉ dùng để lọc sơ bộ
			public double Offset, T0, T1;
		}

		// Hệ trục riêng của ống: gốc tại đầu line biên A, U dọc ống, N vuông góc hướng về biên B
		private sealed class Pipe
		{
			public Seg A = null!, B = null!;
			public Point2d O;
			public Vector2d U, N;
			// [T0, T1] bao cả 2 biên, [Lo, Hi] là đoạn 2 biên chồng nhau
			public double T0, T1, Lo, Hi, S0, S1;
			public double Width => S1 - S0;

			public double T(Point2d p) => (p - O).DotProduct(U);
			public double S(Point2d p) => (p - O).DotProduct(N);
		}

		[CommandMethod("GOM", CommandFlags.Modal | CommandFlags.Redraw)]
		public void PipeBlock()
		{
			Document doc = Application.DocumentManager.MdiActiveDocument;
			Database db = doc.Database;
			Editor ed = doc.Editor;

			HashSet<ObjectId>? boundaryLayers = PickSampleLayers(ed, db);
			if (boundaryLayers == null) return;
			if (!AskMaxDiameter(ed)) return;

			ObjectId[]? selected = SelectTargets(ed);
			if (selected == null) return;

			int pipeCount, splitCount = 0, pairCount, singleCount;
			try
			{
				ExLoadingUtils.SetMessageLoading("Đang đọc đối tượng...");
				ExLoadingUtils.SetShowCancelButton(true);
				ExLoadingUtils.Start(selected.Length * 2 + 1000);

				// Hủy giữa chừng thì transaction không commit, bản vẽ giữ nguyên
				using (Transaction tr = db.TransactionManager.StartTransaction())
				{
					var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
					List<Seg> segs = CollectSegs(tr, db, selected, boundaryLayers);
					List<Pipe> pipes = FindPipes(segs, _maxDiameter);

					var used = new HashSet<ObjectId>();
					foreach (Pipe pipe in pipes)
					{
						used.Add(pipe.A.Id);
						used.Add(pipe.B.Id);
					}

					for (int i = 0; i < pipes.Count; i++)
					{
						Pipe pipe = pipes[i];
						var ids = new ObjectIdCollection { pipe.A.Id, pipe.B.Id };
						splitCount += CollectCenterLines(tr, db, pipe, segs, used, ids);
						Point3d basePoint = GetLowerLeft(tr, ids);
						string name = BlockBuilder.GetUniqueName(blockTable, "Pipe");
						BlockBuilder.Create(tr, db, ids, name, basePoint, pipe.A.LayerId);
						ExLoadingUtils.ReportProgress("Đang tạo block ống", i + 1, pipes.Count, 40, 90);
					}

					// Line biên còn lại: ghép cặp song song trước, phần còn lại là line lẻ
					ExLoadingUtils.SetMessageLoading("Đang đánh dấu line không tạo được ống...");
					List<Seg> unused = segs.Where(s => s.IsBoundary && !used.Contains(s.Id)).ToList();
					List<Pipe> pairs = SelectDisjoint(FindPairs(unused, _maxDiameter));
					var paired = new HashSet<Seg>(pairs.SelectMany(p => new[] { p.A, p.B }));
					List<Seg> singles = unused.Where(s => !paired.Contains(s)).ToList();
					ExLoadingUtils.ThrowIfCancelled();
					AddMarkers(tr, db, pairs, singles, _maxDiameter);

					tr.Commit();
					pipeCount = pipes.Count;
					pairCount = pairs.Count;
					singleCount = singles.Count;
				}
			}
			catch (OperationCanceledException ex)
			{
				ed.WriteMessage($"\n{ex.Message}");
				return;
			}
			finally
			{
				ExLoadingUtils.Close(true, 300);
			}

			ed.WriteMessage(pipeCount > 0 ? $"\nĐã tạo {pipeCount} block ống." : "\nKhông tìm thấy ống nào.");
			if (splitCount > 0)
				ed.WriteMessage($"\nĐã cắt {splitCount} line/polyline tim tại đầu ống.");
			if (pairCount > 0 || singleCount > 0)
				ed.WriteMessage($"\nKhông tạo được ống: {pairCount} cặp line (rectangle vàng), {singleCount} line lẻ (rectangle đỏ). Gõ XOAGOM để xóa các rectangle này.");
		}

		// Xóa rectangle đánh dấu do GOM tạo theo loại lưu trong XData: đỏ (line lẻ), vàng (cặp line) hoặc tất cả
		[CommandMethod("XOAGOM", CommandFlags.Modal)]
		public void ClearMarkers()
		{
			Document doc = Application.DocumentManager.MdiActiveDocument;
			Database db = doc.Database;
			Editor ed = doc.Editor;

			var opts = new PromptKeywordOptions("\nXóa rectangle đánh dấu loại nào?") { AllowNone = false };
			opts.Keywords.Add(ClearRed);
			opts.Keywords.Add(ClearYellow);
			opts.Keywords.Add(ClearAll);
			opts.Keywords.Default = _clearMode;
			PromptResult mode = ed.GetKeywords(opts);
			if (mode.Status != PromptStatus.OK) return;
			_clearMode = mode.StringResult;

			string? targetType = _clearMode == ClearRed ? SingleType : _clearMode == ClearYellow ? PairType : null;
			string label = _clearMode == ClearRed ? " màu đỏ" : _clearMode == ClearYellow ? " màu vàng" : "";

			// Tắt tự chọn line liên kết để SelectAll chỉ trả về rectangle
			int count = 0;
			var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.ExtendedDataRegAppName, MarkerApp) });
			PromptSelectionResult res;
			PipeMarkerSelection.Suspended = true;
			try
			{
				res = ed.SelectAll(filter);
			}
			finally
			{
				PipeMarkerSelection.Suspended = false;
			}

			if (res.Status == PromptStatus.OK)
			{
				using (Transaction tr = db.TransactionManager.StartTransaction())
				{
					foreach (ObjectId id in res.Value.GetObjectIds())
					{
						var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
						string? markerType = GetMarkerType(ent);
						if (markerType == null || (targetType != null && markerType != targetType)) continue;
						tr.GetObject(id, OpenMode.ForWrite, false, true).Erase();
						count++;
					}
					tr.Commit();
				}
			}

			ed.WriteMessage(count > 0 ? $"\nĐã xóa {count} rectangle đánh dấu{label}." : $"\nKhông có rectangle đánh dấu{label} nào.");
		}

		// Mỗi layer của các đối tượng mẫu là 1 layer biên
		private static HashSet<ObjectId>? PickSampleLayers(Editor ed, Database db)
		{
			var opts = new PromptSelectionOptions { MessageForAdding = "\nChọn các line biên ống mẫu: " };
			var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE") });
			PromptSelectionResult res = ed.GetSelection(opts, filter);
			if (res.Status != PromptStatus.OK) return null;

			var layers = new HashSet<ObjectId>();
			var names = new List<string>();
			using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
			{
				foreach (ObjectId id in res.Value.GetObjectIds())
				{
					// Bấm vào rectangle đánh dấu thì line liên kết đã được chọn kèm, bỏ chính rectangle
					var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
					if (GetMarkerType(ent) != null) continue;
					if (layers.Add(ent.LayerId)) names.Add(ent.Layer);
				}
				tr.Commit();
			}
			if (layers.Count == 0)
			{
				ed.WriteMessage("\nChưa chọn line biên mẫu nào.");
				return null;
			}
			ed.WriteMessage($"\nLayer biên = {string.Join(", ", names)}");
			return layers;
		}

		private static bool AskMaxDiameter(Editor ed)
		{
			var opts = new PromptDistanceOptions("\nĐường kính ống tối đa: ") { AllowNegative = false, AllowZero = false };
			if (_maxDiameter > 0)
			{
				opts.DefaultValue = _maxDiameter;
				opts.UseDefaultValue = true;
			}
			PromptDoubleResult res = ed.GetDistance(opts);
			if (res.Status != PromptStatus.OK) return false;
			_maxDiameter = res.Value;
			return true;
		}

		// Chỉ lấy line và polyline để giảm số đối tượng phải xét
		private static ObjectId[]? SelectTargets(Editor ed)
		{
			var opts = new PromptSelectionOptions { MessageForAdding = "\nChọn các đối tượng cần quét ống: " };
			var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE") });
			PromptSelectionResult res = ed.GetSelection(opts, filter);
			return res.Status == PromptStatus.OK ? res.Value.GetObjectIds() : null;
		}

		// Bỏ đối tượng trên layer khóa
		private static List<Seg> CollectSegs(Transaction tr, Database db, ObjectId[] selected, HashSet<ObjectId> boundaryLayers)
		{
			var segs = new List<Seg>();
			var lockedLayers = new HashSet<ObjectId>();
			var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
			foreach (ObjectId id in layerTable)
			{
				if (((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).IsLocked)
					lockedLayers.Add(id);
			}

			foreach (ObjectId id in selected)
			{
				if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent) || lockedLayers.Contains(ent.LayerId))
					continue;

				Seg? seg = ToSeg(ent, boundaryLayers);
				if (seg != null) segs.Add(seg);
			}
			return segs;
		}

		private static Seg? ToSeg(Entity ent, HashSet<ObjectId>? boundaryLayers)
		{
			Point3d start, end;
			if (ent is Line line)
			{
				start = line.StartPoint;
				end = line.EndPoint;
			}
			else if (ent is Polyline pline && IsStraight(pline))
			{
				start = pline.StartPoint;
				end = pline.EndPoint;
			}
			else return null;

			var s = new Point2d(start.X, start.Y);
			var e = new Point2d(end.X, end.Y);
			if (s.GetDistanceTo(e) <= Tolerance.Global.EqualPoint) return null;

			// Hướng quy về [0, PI), sát PI thì đưa về âm để gom chung nhóm với hướng sát 0
			double angle = (e - s).Angle % Math.PI;
			if (angle > Math.PI - AngleTolerance) angle -= Math.PI;
			return new Seg
			{
				Id = ent.ObjectId,
				LayerId = ent.LayerId,
				Start = s,
				End = e,
				Angle = angle,
				IsBoundary = boundaryLayers != null && boundaryLayers.Contains(ent.LayerId)
			};
		}

		// Polyline hở, không cung, mọi đỉnh nằm trên đoạn đầu-cuối
		private static bool IsStraight(Polyline pline)
		{
			if (pline.Closed || pline.NumberOfVertices < 2) return false;
			Point2d s = pline.GetPoint2dAt(0), e = pline.GetPoint2dAt(pline.NumberOfVertices - 1);
			double length = s.GetDistanceTo(e);
			if (length <= Tolerance.Global.EqualPoint) return false;

			Vector2d dir = (e - s) / length;
			for (int i = 0; i < pline.NumberOfVertices; i++)
			{
				if (Math.Abs(pline.GetBulgeAt(i)) > 1e-9) return false;
				Vector2d v = pline.GetPoint2dAt(i) - s;
				if (Math.Abs(dir.X * v.Y - dir.Y * v.X) > length * 1e-6) return false;
			}
			return true;
		}

		private static List<Pipe> FindPipes(List<Seg> segs, double maxDiameter)
		{
			var candidates = new List<Pipe>();
			List<List<Seg>> groups = GroupByDirection(segs);
			// Báo tiến độ khoảng 100 lần, tránh gọi cửa sổ loading cho từng nhóm khi có hàng nghìn hướng
			int step = Math.Max(1, groups.Count / 100);
			for (int g = 0; g < groups.Count; g++)
			{
				candidates.AddRange(FindPipesInGroup(groups[g], maxDiameter));
				if ((g + 1) % step == 0 || g + 1 == groups.Count)
					ExLoadingUtils.ReportProgress("Đang dò ống theo hướng", g + 1, groups.Count, 0, 40);
			}
			return SelectDisjoint(candidates);
		}

		// Ưu tiên cặp hẹp trước, mỗi line biên chỉ thuộc 1 cặp
		private static List<Pipe> SelectDisjoint(IEnumerable<Pipe> candidates)
		{
			var used = new HashSet<Seg>();
			var result = new List<Pipe>();
			foreach (Pipe pipe in candidates.OrderBy(p => p.Width))
			{
				if (used.Contains(pipe.A) || used.Contains(pipe.B)) continue;
				used.Add(pipe.A);
				used.Add(pipe.B);
				result.Add(pipe);
			}
			return result;
		}

		private static List<List<Seg>> GroupByDirection(List<Seg> segs)
		{
			var groups = new List<List<Seg>>();
			List<Seg>? current = null;
			foreach (Seg seg in segs.OrderBy(s => s.Angle))
			{
				if (current == null || seg.Angle - current[0].Angle > AngleTolerance)
				{
					current = new List<Seg>();
					groups.Add(current);
				}
				current.Add(seg);
			}
			return groups;
		}

		// Hệ trục chung lệch tối đa AngleTolerance so với từng line, nên offset sơ bộ sai tối đa slack (giá trị trả về)
		private static double PrepareGroup(List<Seg> group)
		{
			double angle = group[0].Angle;
			var u = new Vector2d(Math.Cos(angle), Math.Sin(angle));
			var n = new Vector2d(-u.Y, u.X);
			foreach (Seg seg in group)
			{
				Vector2d mid = (seg.Start.GetAsVector() + seg.End.GetAsVector()) / 2;
				seg.Offset = mid.DotProduct(n);
				double t0 = seg.Start.GetAsVector().DotProduct(u);
				double t1 = seg.End.GetAsVector().DotProduct(u);
				seg.T0 = Math.Min(t0, t1);
				seg.T1 = Math.Max(t0, t1);
			}
			return AngleTolerance * (group.Max(s => s.T1) - group.Min(s => s.T0));
		}

		private static IEnumerable<Pipe> FindPipesInGroup(List<Seg> group, double maxDiameter)
		{
			double slack = PrepareGroup(group);
			List<Seg> all = group.OrderBy(s => s.Offset).ToList();
			double[] offsets = all.Select(s => s.Offset).ToArray();
			List<Seg> boundaries = all.Where(s => s.IsBoundary).ToList();

			for (int i = 0; i < boundaries.Count; i++)
			{
				for (int j = i + 1; j < boundaries.Count; j++)
				{
					if (boundaries[j].Offset - boundaries[i].Offset > maxDiameter + slack) break;
					if (boundaries[i].LayerId != boundaries[j].LayerId) continue;

					Pipe? pipe = TryMakePipe(boundaries[i], boundaries[j], maxDiameter);
					if (pipe == null) continue;

					// Ống phải dài hơn bề rộng, tránh nhận nhầm 2 cạnh của khớp nối là ống
					if (pipe.Hi - pipe.Lo < pipe.Width * MinLengthRatio) continue;

					double tol = pipe.Width * CenterTolerance;
					double coarseCenter = (boundaries[i].Offset + boundaries[j].Offset) / 2;
					if (!HasCenterLine(all, offsets, pipe, coarseCenter, tol + slack, tol)) continue;
					if (HasBoundaryBetween(boundaries, i, j, pipe, tol)) continue;
					yield return pipe;
				}
			}
		}

		// Cặp line biên song song cùng layer, chồng nhau và cách nhau không quá đường kính tối đa
		private static IEnumerable<Pipe> FindPairs(List<Seg> segs, double maxDiameter)
		{
			foreach (List<Seg> group in GroupByDirection(segs))
			{
				double slack = PrepareGroup(group);
				List<Seg> sorted = group.OrderBy(s => s.Offset).ToList();
				for (int i = 0; i < sorted.Count; i++)
				{
					for (int j = i + 1; j < sorted.Count; j++)
					{
						if (sorted[j].Offset - sorted[i].Offset > maxDiameter + slack) break;
						if (sorted[i].LayerId != sorted[j].LayerId) continue;

						Pipe? pair = TryMakePipe(sorted[i], sorted[j], maxDiameter);
						if (pair != null) yield return pair;
					}
				}
			}
		}

		// Dựng hệ trục từ chính line a, đo b tại giữa đoạn chồng nhau
		private static Pipe? TryMakePipe(Seg a, Seg b, double maxDiameter)
		{
			Vector2d u = (a.End - a.Start).GetNormal();
			var pipe = new Pipe { A = a, B = b, O = a.Start, U = u, N = new Vector2d(-u.Y, u.X) };

			pipe.Lo = Math.Max(OverlapStart(pipe, a), OverlapStart(pipe, b));
			pipe.Hi = Math.Min(OverlapEnd(pipe, a), OverlapEnd(pipe, b));
			if (pipe.Hi <= pipe.Lo) return null;

			double offset = OffsetAt(pipe, b, (pipe.Lo + pipe.Hi) / 2);
			if (offset < 0)
			{
				pipe.N = -pipe.N;
				offset = -offset;
			}
			if (offset <= Tolerance.Global.EqualPoint || offset > maxDiameter * (1 + 1e-6)) return null;

			pipe.S0 = 0;
			pipe.S1 = offset;
			pipe.T0 = Math.Min(OverlapStart(pipe, a), OverlapStart(pipe, b));
			pipe.T1 = Math.Max(OverlapEnd(pipe, a), OverlapEnd(pipe, b));
			return pipe;
		}

		private static bool HasCenterLine(List<Seg> all, double[] offsets, Pipe pipe, double coarseCenter, double window, double tol)
		{
			double center = pipe.Width / 2;
			int k = Array.BinarySearch(offsets, coarseCenter - window);
			if (k < 0) k = ~k;
			for (; k < all.Count && offsets[k] <= coarseCenter + window; k++)
			{
				Seg c = all[k];
				if (c == pipe.A || c == pipe.B) continue;

				double cLo = Math.Max(OverlapStart(pipe, c), pipe.Lo), cHi = Math.Min(OverlapEnd(pipe, c), pipe.Hi);
				if (cHi - cLo <= tol) continue;
				if (Math.Abs(OffsetAt(pipe, c, cLo) - center) <= tol && Math.Abs(OffsetAt(pipe, c, cHi) - center) <= tol)
					return true;
			}
			return false;
		}

		// Có line biên khác nằm giữa a và b (không phải tim) thì a-b là 2 biên của 2 ống khác nhau
		private static bool HasBoundaryBetween(List<Seg> boundaries, int i, int j, Pipe pipe, double tol)
		{
			double center = pipe.Width / 2;
			for (int k = i + 1; k < j; k++)
			{
				Seg c = boundaries[k];
				double cLo = Math.Max(OverlapStart(pipe, c), pipe.Lo), cHi = Math.Min(OverlapEnd(pipe, c), pipe.Hi);
				if (cHi - cLo <= tol) continue;

				double s = OffsetAt(pipe, c, (cLo + cHi) / 2);
				if (s <= tol || s >= pipe.Width - tol || Math.Abs(s - center) <= tol) continue;
				return true;
			}
			return false;
		}

		private static double OverlapStart(Pipe pipe, Seg seg) => Math.Min(pipe.T(seg.Start), pipe.T(seg.End));

		private static double OverlapEnd(Pipe pipe, Seg seg) => Math.Max(pipe.T(seg.Start), pipe.T(seg.End));

		// Khoảng cách tới biên A của điểm trên seg có tọa độ dọc ống = t
		private static double OffsetAt(Pipe pipe, Seg seg, double t)
		{
			double ts = pipe.T(seg.Start), te = pipe.T(seg.End);
			if (Math.Abs(te - ts) <= Tolerance.Global.EqualPoint) return pipe.S(seg.Start);
			double k = (t - ts) / (te - ts);
			return pipe.S(seg.Start + (seg.End - seg.Start) * k);
		}

		// Đưa các đoạn tim (nằm chính giữa 2 biên) vào ids; đoạn vượt ra ngoài đầu ống thì cắt,
		// phần ngoài giữ lại trên bản vẽ và tiếp tục được xét cho các ống sau
		private static int CollectCenterLines(Transaction tr, Database db, Pipe pipe, List<Seg> segs, HashSet<ObjectId> used, ObjectIdCollection ids)
		{
			double tol = pipe.Width * CenterTolerance;
			double center = pipe.Width / 2;
			double eps = 1e-6 * (pipe.T1 - pipe.T0 + pipe.Width);
			int count = 0;
			BlockTableRecord? space = null;

			foreach (Seg seg in segs.ToList())
			{
				if (used.Contains(seg.Id)) continue;
				if (Math.Abs(pipe.S(seg.Start) - center) > tol || Math.Abs(pipe.S(seg.End) - center) > tol) continue;

				double ts = pipe.T(seg.Start), te = pipe.T(seg.End);
				double lo = Math.Min(ts, te), hi = Math.Max(ts, te);
				if (hi <= pipe.T0 + eps || lo >= pipe.T1 - eps) continue;
				if (lo >= pipe.T0 - eps && hi <= pipe.T1 + eps)
				{
					used.Add(seg.Id);
					ids.Add(seg.Id);
					continue;
				}

				var curve = (Curve)tr.GetObject(seg.Id, OpenMode.ForRead);
				var parameters = new List<double>();
				foreach (double t in new[] { pipe.T0, pipe.T1 })
				{
					if (t <= lo + eps || t >= hi - eps) continue;
					Point3d p = curve.StartPoint + (curve.EndPoint - curve.StartPoint) * ((t - ts) / (te - ts));
					parameters.Add(curve.GetParameterAtPoint(curve.GetClosestPointTo(p, false)));
				}
				parameters.Sort();

				DBObjectCollection pieces = curve.GetSplitCurves(new DoubleCollection(parameters.ToArray()));
				space ??= (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
				foreach (DBObject obj in pieces)
				{
					var piece = (Curve)obj;
					space.AppendEntity(piece);
					tr.AddNewlyCreatedDBObject(piece, true);

					var mid = new Point2d((piece.StartPoint.X + piece.EndPoint.X) / 2, (piece.StartPoint.Y + piece.EndPoint.Y) / 2);
					double tMid = pipe.T(mid);
					if (tMid >= pipe.T0 && tMid <= pipe.T1)
					{
						used.Add(piece.ObjectId);
						ids.Add(piece.ObjectId);
						continue;
					}

					Seg? pieceSeg = ToSeg(piece, null);
					if (pieceSeg != null) segs.Add(pieceSeg);
				}

				curve.UpgradeOpen();
				curve.Erase();
				used.Add(seg.Id);
				count++;
			}
			return count;
		}

		private static void AddMarkers(Transaction tr, Database db, List<Pipe> pairs, List<Seg> singles, double maxDiameter)
		{
			if (pairs.Count == 0 && singles.Count == 0) return;

			double margin = maxDiameter * MarkerMarginRatio;
			double width = maxDiameter * MarkerWidthRatio;
			ObjectId layerId = EnsureMarkerLayer(tr, db);
			EnsureRegApp(tr, db);
			var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

			foreach (Pipe pair in pairs)
				AddRectangle(tr, space, layerId, pair.O, pair.U, pair.N,
					pair.T0 - margin, pair.T1 + margin, pair.S0 - margin, pair.S1 + margin, width, PairColor, PairType, pair.A.Id, pair.B.Id);

			foreach (Seg seg in singles)
			{
				Vector2d u = (seg.End - seg.Start).GetNormal();
				AddRectangle(tr, space, layerId, seg.Start, u, new Vector2d(-u.Y, u.X),
					-margin, seg.Start.GetDistanceTo(seg.End) + margin, -margin, margin, width, SingleColor, SingleType, seg.Id);
			}
		}

		// Rectangle [t0, t1] x [s0, s1] trong hệ trục (o, u, n); XData = app + loại (PairType/SingleType) + handle các line liên kết
		private static void AddRectangle(Transaction tr, BlockTableRecord space, ObjectId layerId, Point2d o, Vector2d u, Vector2d n,
			double t0, double t1, double s0, double s1, double width, short colorIndex, string markerType, params ObjectId[] links)
		{
			var rect = new Polyline(4);
			rect.SetDatabaseDefaults(space.Database);
			rect.AddVertexAt(0, o + u * t0 + n * s0, 0, 0, 0);
			rect.AddVertexAt(1, o + u * t1 + n * s0, 0, 0, 0);
			rect.AddVertexAt(2, o + u * t1 + n * s1, 0, 0, 0);
			rect.AddVertexAt(3, o + u * t0 + n * s1, 0, 0, 0);
			rect.Closed = true;
			rect.ConstantWidth = width;
			rect.LayerId = layerId;
			rect.ColorIndex = colorIndex;

			space.AppendEntity(rect);
			tr.AddNewlyCreatedDBObject(rect, true);
			var xdata = new ResultBuffer(
				new TypedValue((int)DxfCode.ExtendedDataRegAppName, MarkerApp),
				new TypedValue((int)DxfCode.ExtendedDataAsciiString, markerType));
			foreach (ObjectId link in links)
				xdata.Add(new TypedValue((int)DxfCode.ExtendedDataHandle, link.Handle));
			rect.XData = xdata;
		}

		private static string? GetMarkerType(Entity ent)
		{
			using (ResultBuffer? rb = ent.GetXDataForApplication(MarkerApp))
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

		// Line liên kết của rectangle đánh dấu (bỏ line đã bị xóa); không phải rectangle của GOM thì rỗng
		internal static List<ObjectId> GetMarkerLinks(Entity ent)
		{
			var links = new List<ObjectId>();
			using (ResultBuffer? rb = ent.GetXDataForApplication(MarkerApp))
			{
				if (rb == null) return links;
				foreach (TypedValue value in rb)
				{
					if (value.TypeCode != (int)DxfCode.ExtendedDataHandle) continue;
					Handle handle = value.Value is Handle h ? h : new Handle(Convert.ToInt64(value.Value.ToString(), 16));
					if (ent.Database.TryGetObjectId(handle, out ObjectId id) && !id.IsErased)
						links.Add(id);
				}
			}
			return links;
		}

		// Layer riêng không in, bật lại nếu user đã tắt/khóa
		private static ObjectId EnsureMarkerLayer(Transaction tr, Database db)
		{
			var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
			if (layerTable.Has(MarkerLayer))
			{
				var layer = (LayerTableRecord)tr.GetObject(layerTable[MarkerLayer], OpenMode.ForWrite);
				layer.IsLocked = false;
				layer.IsOff = false;
				layer.IsFrozen = false;
				return layer.ObjectId;
			}

			layerTable.UpgradeOpen();
			var record = new LayerTableRecord { Name = MarkerLayer, IsPlottable = false };
			ObjectId id = layerTable.Add(record);
			tr.AddNewlyCreatedDBObject(record, true);
			return id;
		}

		private static void EnsureRegApp(Transaction tr, Database db)
		{
			var regAppTable = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
			if (regAppTable.Has(MarkerApp)) return;

			regAppTable.UpgradeOpen();
			var record = new RegAppTableRecord { Name = MarkerApp };
			regAppTable.Add(record);
			tr.AddNewlyCreatedDBObject(record, true);
		}

		private static Point3d GetLowerLeft(Transaction tr, ObjectIdCollection ids)
		{
			var extents = new Extents3d();
			bool hasExtents = false;
			foreach (ObjectId id in ids)
			{
				Extents3d? bounds = ((Entity)tr.GetObject(id, OpenMode.ForRead)).Bounds;
				if (bounds == null) continue;
				if (hasExtents) extents.AddExtents(bounds.Value);
				else extents = bounds.Value;
				hasExtents = true;
			}
			return extents.MinPoint;
		}
	}
}
