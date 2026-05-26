using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Geometry;

namespace Plugin01
{
    /// <summary>
    /// 평면 패턴 커브(월드 XY 위)를 표면 UV에 타일링(반복 매핑)합니다.
    /// 세 가지 방식 모두 "nU x nV 반복"으로 일반화됩니다:
    ///   - 한 장 늘려 맞춤  -> nU = nV = 1
    ///   - 실제 크기로 반복 -> 표면 크기 / 셀 크기로 nU, nV 계산
    ///   - 반복 횟수 제어   -> nU, nV 직접 지정
    /// </summary>
    public static class SurfaceTiler
    {
        public static List<Curve> Tile(Surface srf, IList<Curve> pattern, BoundingBox patternBox,
                                       int nU, int nV, double sampleChord)
        {
            var result = new List<Curve>();
            if (srf == null || pattern == null || pattern.Count == 0) return result;

            double pw = patternBox.Max.X - patternBox.Min.X;
            double ph = patternBox.Max.Y - patternBox.Min.Y;
            if (pw <= 1e-9 || ph <= 1e-9) return result;

            var ud = srf.Domain(0);
            var vd = srf.Domain(1);
            nU = Math.Max(1, nU);
            nV = Math.Max(1, nV);

            // 닫힌(주기) 방향(예: revolve 표면)은 솔기에서 패턴 끝열이 시작열과 겹친다.
            // 그 방향에선 매핑 폭에 "한 칸 간격"을 더해 한 바퀴 돌 때 간격이 균일하게 떨어지도록.
            double Wx = pw, Wy = ph;
            if (srf.IsClosed(0)) Wx = pw + EstimateGap(pattern, 0);
            if (srf.IsClosed(1)) Wy = ph + EstimateGap(pattern, 1);

            // 패턴 커브를 한 번만 점으로 샘플링해 재사용
            var sampled = new List<Point3d[]>(pattern.Count);
            foreach (var c in pattern)
                sampled.Add(SampleCurve(c, sampleChord));

            for (int i = 0; i < nU; i++)
            {
                for (int j = 0; j < nV; j++)
                {
                    foreach (var pts in sampled)
                    {
                        var mapped = new Point3d[pts.Length];
                        for (int k = 0; k < pts.Length; k++)
                        {
                            double fx = (pts[k].X - patternBox.Min.X) / Wx;
                            double fy = (pts[k].Y - patternBox.Min.Y) / Wy;
                            double u = ud.T0 + (i + fx) / nU * (ud.T1 - ud.T0);
                            double v = vd.T0 + (j + fy) / nV * (vd.T1 - vd.T0);
                            mapped[k] = srf.PointAt(u, v);
                        }
                        var crv = new PolylineCurve(mapped);
                        if (crv.IsValid) result.Add(crv);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 같은 바탕 곡면을 공유하는 연결면들 위에, 지정한 UV 영역으로 패턴을 늘려(stretch/repeat) 깐다.
        /// 면이 나뉘어 있어도 공유 곡면의 연속 UV를 쓰므로 경계에서 패턴이 끊기지 않는다 (이중곡면 포함).
        /// 실제 면 영역 밖(트림 바깥)의 셀은 제외한다.
        /// </summary>
        public static List<Curve> TileRegion(Surface srf, IList<BrepFace> clipFaces,
                                             Interval uReg, Interval vReg,
                                             IList<Curve> pattern, BoundingBox patternBox,
                                             int nU, int nV, double sampleChord,
                                             double margin = 0, bool flipH = false, bool flipV = false,
                                             double rotationDeg = 0)
        {
            var result = new List<Curve>();
            if (srf == null || pattern == null || pattern.Count == 0) return result;

            double pw = patternBox.Max.X - patternBox.Min.X;
            double ph = patternBox.Max.Y - patternBox.Min.Y;
            if (pw <= 1e-9 || ph <= 1e-9) return result;

            nU = Math.Max(1, nU);
            nV = Math.Max(1, nV);

            // 마진 인셋: 외곽선에서 margin mm 안쪽으로 영역을 줄여 패턴을 채움
            if (margin > 1e-9)
            {
                double sw, sh;
                if (srf.GetSurfaceSize(out sw, out sh) && sw > 1e-9 && sh > 1e-9)
                {
                    double insetU = (margin / sw) * uReg.Length;
                    double insetV = (margin / sh) * vReg.Length;
                    uReg = new Interval(uReg.T0 + insetU, uReg.T1 - insetU);
                    vReg = new Interval(vReg.T0 + insetV, vReg.T1 - insetV);
                    if (uReg.Length <= 1e-9 || vReg.Length <= 1e-9) return result;
                }
            }

            // 영역이 닫힌 방향의 전체 도메인을 덮을 때만 솔기 간격 보정
            var ud = srf.Domain(0);
            var vd = srf.Domain(1);
            bool fullU = Math.Abs(uReg.Length - ud.Length) < 1e-4 * Math.Max(1.0, ud.Length);
            bool fullV = Math.Abs(vReg.Length - vd.Length) < 1e-4 * Math.Max(1.0, vd.Length);
            // 회전된 패턴 bbox 크기 (회전 0이면 원래 pw, ph)
            double absCr = Math.Abs(Math.Cos(rotationDeg * Math.PI / 180.0));
            double absSr = Math.Abs(Math.Sin(rotationDeg * Math.PI / 180.0));
            double Wrot = pw * absCr + ph * absSr;
            double Hrot = pw * absSr + ph * absCr;
            double Wx = Wrot, Wy = Hrot;
            if (srf.IsClosed(0) && fullU) Wx = Wrot + EstimateGap(pattern, 0);
            if (srf.IsClosed(1) && fullV) Wy = Hrot + EstimateGap(pattern, 1);

            // 호 길이 기준 매핑: 영역 안에서 표면 점을 직접 샘플링해 실제 거리 테이블 구성
            // (파라미터 불균일 때문에 평면/곡면 셀 크기가 달라지거나 경계서 뭉치는 문제 방지)
            double[] uPar, uCum, vPar, vCum;
            double totalU, totalV;
            BuildArcTable(srf, false, vReg.ParameterAt(0.5), uReg.T0, uReg.T1, out uPar, out uCum, out totalU);
            BuildArcTable(srf, true, uReg.ParameterAt(0.5), vReg.T0, vReg.T1, out vPar, out vCum, out totalV);
            if (totalU < 1e-9 || totalV < 1e-9) return result;

            var sampled = new List<Point3d[]>(pattern.Count);
            foreach (var c in pattern) sampled.Add(SampleCurve(c, sampleChord));

            for (int i = 0; i < nU; i++)
            {
                for (int j = 0; j < nV; j++)
                {
                    foreach (var pts in sampled)
                    {
                        var mapped = new Point3d[pts.Length];
                        for (int k = 0; k < pts.Length; k++)
                        {
                            double xRel = pts[k].X - patternBox.Min.X;
                            double yRel = pts[k].Y - patternBox.Min.Y;
                            if (flipH) xRel = pw - xRel;
                            if (flipV) yRel = ph - yRel;
                            // 회전 후 새 bbox(Wrot × Hrot) 내 좌표로 변환
                            if (Math.Abs(rotationDeg) > 1e-9)
                            {
                                double rr = rotationDeg * Math.PI / 180.0;
                                double cosR = Math.Cos(rr), sinR = Math.Sin(rr);
                                double dx = xRel - pw * 0.5, dy = yRel - ph * 0.5;
                                xRel = dx * cosR - dy * sinR + Wrot * 0.5;
                                yRel = dx * sinR + dy * cosR + Hrot * 0.5;
                            }
                            double fx = xRel / Wx;
                            double fy = yRel / Wy;
                            double sU = (i + fx) / nU * totalU;
                            double sV = (j + fy) / nV * totalV;
                            double u = InterpParam(uCum, uPar, sU);
                            double v = InterpParam(vCum, vPar, sV);
                            mapped[k] = srf.PointAt(u, v);
                        }
                        var crv = new PolylineCurve(mapped);
                        if (!crv.IsValid) continue;

                        // 셀 중심이 실제 면 영역 안에 있을 때만 채택
                        if (clipFaces != null && clipFaces.Count > 0)
                        {
                            var cen = crv.GetBoundingBox(false).Center;
                            double cu, cv;
                            if (!srf.ClosestPoint(cen, out cu, out cv)) continue;
                            bool on = false;
                            foreach (var f in clipFaces)
                                if (f.IsPointOnFace(cu, cv) != PointFaceRelation.Exterior) { on = true; break; }
                            if (!on) continue;
                        }
                        result.Add(crv);
                    }
                }
            }

            return result;
        }

        /// <summary>여러 면(같은 곡면 공유)의 트림 UV 영역을 합친 전체 UV 영역.</summary>
        public static void CombinedUvRegion(IList<BrepFace> faces, out Interval uReg, out Interval vReg)
        {
            double uMin = double.MaxValue, uMax = double.MinValue;
            double vMin = double.MaxValue, vMax = double.MinValue;
            foreach (var face in faces)
            {
                double a = face.Domain(0).T0, b = face.Domain(0).T1;
                double c = face.Domain(1).T0, d = face.Domain(1).T1;
                try
                {
                    var c2 = face.OuterLoop?.To2dCurve();
                    if (c2 != null)
                    {
                        var bb = c2.GetBoundingBox(true);
                        a = bb.Min.X; b = bb.Max.X; c = bb.Min.Y; d = bb.Max.Y;
                    }
                }
                catch { }
                uMin = Math.Min(uMin, a); uMax = Math.Max(uMax, b);
                vMin = Math.Min(vMin, c); vMax = Math.Max(vMax, d);
            }
            uReg = new Interval(uMin, uMax);
            vReg = new Interval(vMin, vMax);
        }

        /// <summary>
        /// 분석된 패턴(단위 도형 + 간격)을 표면에 "실제 크기"로 배치한다.
        /// V 방향은 호 길이로 행을 나누고, 각 행의 둘레(호 길이)에 맞춰 들어갈 만큼만 셀을 배치한다.
        /// 곡면/구에서도 셀이 실제 크기를 유지하며, 둘레가 줄면 자동으로 개수가 준다.
        /// </summary>
        public static List<Curve> TileRealSize(BrepFace face, PatternInfo info, Vector3d refDir = default(Vector3d))
        {
            var result = new List<Curve>();
            if (face == null || info == null || !info.Valid || info.UnitCells.Count == 0) return result;

            Surface srf = face;
            bool closedU = srf.IsClosed(0);
            var ud = srf.Domain(0);
            var vd = srf.Domain(1);

            // 트림된 면의 실제 UV 사용 영역 (언트림 표면이 화면보다 클 수 있으므로)
            double uMin = ud.T0, uMax = ud.T1, vMin = vd.T0, vMax = vd.T1;
            try
            {
                var loop = face.OuterLoop;
                var c2 = loop?.To2dCurve();
                if (c2 != null)
                {
                    var bb = c2.GetBoundingBox(true);
                    uMin = bb.Min.X; uMax = bb.Max.X;
                    vMin = bb.Min.Y; vMax = bb.Max.Y;
                }
            }
            catch { }

            double chord = Math.Max(info.CellW, info.CellH) / 20.0;
            var cellPts = new List<Point3d[]>();
            foreach (var c in info.UnitCells) cellPts.Add(SampleCurve(c, chord));

            const int safetyCap = 100000;

            // 세로(V): 가운데 U에서 표면 점을 직접 샘플링한 실제 호 길이로 행 위치 결정
            double uMid = 0.5 * (uMin + uMax);
            var vRows = ArcLengthParams(srf, true, uMid, vMin, vMax, info.PitchV, false);

            foreach (double v in vRows)
            {
                // 가로(U): 이 행에서 표면 점을 직접 샘플링한 실제 호 길이로 셀 위치 결정
                var uCols = ArcLengthParams(srf, false, v, uMin, uMax, info.PitchU, closedU);

                foreach (double u in uCols)
                {
                    if (face.IsPointOnFace(u, v) == PointFaceRelation.Exterior) continue;

                    Point3d s0; Vector3d du, dv;
                    if (!EvalDeriv(srf, u, v, out s0, out du, out dv)) continue;
                    double lu = du.Length, lv = dv.Length;
                    if (lu < 1e-9 || lv < 1e-9) continue; // 극점 등 특이점

                    // 기준 방향 정렬: 셀의 +X를 refDir 방향에 정렬.
                    // refDir을 du, dv 단위벡터에 직접 투영 -> 부호/법선 모호함 없음.
                    double cosA = 1.0, sinA = 0.0;
                    if (refDir.Length > 1e-9)
                    {
                        var duHat = du / lu;
                        var dvHat = dv / lv;
                        cosA = refDir * duHat;
                        sinA = refDir * dvHat;
                        double mag = Math.Sqrt(cosA * cosA + sinA * sinA);
                        if (mag > 1e-9) { cosA /= mag; sinA /= mag; }
                        else { cosA = 1.0; sinA = 0.0; }
                    }

                    foreach (var pts in cellPts)
                    {
                        var mapped = new Point3d[pts.Length];
                        for (int k = 0; k < pts.Length; k++)
                        {
                            double rx = pts[k].X * cosA - pts[k].Y * sinA;
                            double ry = pts[k].X * sinA + pts[k].Y * cosA;
                            double uu = u + rx / lu;
                            double vv = v + ry / lv;
                            if (closedU) uu = WrapToDomain(uu, ud);
                            vv = Math.Min(vd.T1, Math.Max(vd.T0, vv));
                            mapped[k] = srf.PointAt(uu, vv);
                        }
                        var crv = new PolylineCurve(mapped);
                        if (crv.IsValid) result.Add(crv);
                        if (result.Count > safetyCap) return result;
                    }
                }
            }

            return result;
        }

        // 한 방향(alongV=true면 V, false면 U)으로 표면 점을 샘플링해 실제 3D 호 길이를 누적하고,
        // pitch 간격으로 균등하게 떨어진 파라미터들을 돌려준다. (파라미터 불균일/비정상 길이에 강건)
        private static List<double> ArcLengthParams(Surface srf, bool alongV, double fixedParam,
                                                    double pStart, double pEnd, double pitch, bool closed)
        {
            const int samples = 400;
            var ps = new double[samples + 1];
            var ds = new double[samples + 1];
            Point3d prev = Eval(srf, alongV, fixedParam, pStart);
            ps[0] = pStart; ds[0] = 0; double cum = 0;
            for (int s = 1; s <= samples; s++)
            {
                double p = pStart + (pEnd - pStart) * s / samples;
                var pt = Eval(srf, alongV, fixedParam, p);
                cum += pt.DistanceTo(prev);
                prev = pt;
                ps[s] = p; ds[s] = cum;
            }

            var outp = new List<double>();
            double total = cum;
            if (total < 1e-9 || pitch < 1e-9) return outp;

            int n = Math.Max(1, (int)Math.Round(total / pitch));
            for (int i = 0; i < n; i++)
            {
                double target = closed ? total * i / n : total * (i + 0.5) / n;
                outp.Add(InterpParam(ds, ps, target));
            }
            return outp;
        }

        private static Point3d Eval(Surface srf, bool alongV, double fixedParam, double p)
            => alongV ? srf.PointAt(fixedParam, p) : srf.PointAt(p, fixedParam);

        // 한 방향으로 표면 점을 샘플링해 (파라미터 ↔ 누적 호 길이) 테이블과 총 길이를 만든다.
        private static void BuildArcTable(Surface srf, bool alongV, double fixedParam,
                                          double pStart, double pEnd,
                                          out double[] pars, out double[] cum, out double total)
        {
            const int n = 400;
            pars = new double[n + 1];
            cum = new double[n + 1];
            Point3d prev = Eval(srf, alongV, fixedParam, pStart);
            pars[0] = pStart; cum[0] = 0; double c = 0;
            for (int s = 1; s <= n; s++)
            {
                double p = pStart + (pEnd - pStart) * s / n;
                var pt = Eval(srf, alongV, fixedParam, p);
                c += pt.DistanceTo(prev);
                prev = pt;
                pars[s] = p; cum[s] = c;
            }
            total = c;
        }

        private static double InterpParam(double[] ds, double[] ps, double target)
        {
            int n = ds.Length;
            if (target <= ds[0]) return ps[0];
            if (target >= ds[n - 1]) return ps[n - 1];
            for (int s = 1; s < n; s++)
            {
                if (ds[s] >= target)
                {
                    double seg = ds[s] - ds[s - 1];
                    double f = seg > 1e-12 ? (target - ds[s - 1]) / seg : 0;
                    return ps[s - 1] + (ps[s] - ps[s - 1]) * f;
                }
            }
            return ps[n - 1];
        }

        private static bool EvalDeriv(Surface srf, double u, double v,
                                      out Point3d pt, out Vector3d du, out Vector3d dv)
        {
            pt = Point3d.Origin; du = Vector3d.Zero; dv = Vector3d.Zero;
            Vector3d[] ders;
            if (!srf.Evaluate(u, v, 1, out pt, out ders)) return false;
            if (ders == null || ders.Length < 2) return false;
            du = ders[0]; dv = ders[1];
            return true;
        }

        private static double WrapToDomain(double t, Interval dom)
        {
            double len = dom.Length;
            if (len <= 1e-12) return t;
            double x = (t - dom.T0) % len;
            if (x < 0) x += len;
            return dom.T0 + x;
        }

        // 패턴 도형들 사이의 대표 간격(축 방향)을 추정. 분리된 열/행 사이 빈틈의 중앙값.
        private static double EstimateGap(IList<Curve> curves, int axis)
        {
            if (curves == null || curves.Count < 2) return 0;

            var intervals = new List<double[]>(curves.Count);
            foreach (var c in curves)
            {
                var b = c.GetBoundingBox(true);
                double mn = axis == 0 ? b.Min.X : b.Min.Y;
                double mx = axis == 0 ? b.Max.X : b.Max.Y;
                intervals.Add(new[] { mn, mx });
            }
            intervals.Sort((a, b) => a[0].CompareTo(b[0]));

            var gaps = new List<double>();
            double curEnd = intervals[0][1];
            for (int i = 1; i < intervals.Count; i++)
            {
                double s = intervals[i][0], e = intervals[i][1];
                if (s > curEnd + 1e-9) { gaps.Add(s - curEnd); curEnd = e; }
                else if (e > curEnd) { curEnd = e; }
            }
            if (gaps.Count == 0) return 0;
            gaps.Sort();
            return gaps[gaps.Count / 2]; // 중앙값
        }

        private static Point3d[] SampleCurve(Curve c, double chord)
        {
            // 폴리라인이면 각 변을 chord로 분할하되 꼭짓점은 정확히 유지 (찌그러짐 방지)
            var plc = c as PolylineCurve;
            Polyline pl = null;
            if (plc != null && plc.TryGetPolyline(out pl) && pl.Count >= 2)
            {
                var ptsList = new List<Point3d>();
                int nv = pl.Count;
                for (int i = 0; i < nv - 1; i++)
                {
                    Point3d a = pl[i];
                    Point3d b = pl[i + 1];
                    double edgeLen = a.DistanceTo(b);
                    int subs = chord > 1e-9 ? Math.Max(1, (int)Math.Ceiling(edgeLen / chord)) : 1;
                    for (int j = 0; j < subs; j++)
                    {
                        double t = (double)j / subs;
                        ptsList.Add(new Point3d(
                            a.X + (b.X - a.X) * t,
                            a.Y + (b.Y - a.Y) * t,
                            a.Z + (b.Z - a.Z) * t));
                    }
                }
                ptsList.Add(pl[nv - 1]);
                return ptsList.ToArray();
            }

            double len = c.GetLength();
            int n = chord > 1e-9 ? (int)Math.Ceiling(len / chord) : 12;
            n = Math.Max(6, Math.Min(n, 300));

            double[] ts = c.DivideByCount(n, true);
            List<Point3d> pts;
            if (ts == null || ts.Length == 0)
                pts = new List<Point3d> { c.PointAtStart, c.PointAtEnd };
            else
            {
                pts = new List<Point3d>(ts.Length);
                foreach (var t in ts) pts.Add(c.PointAt(t));
            }

            // 닫힌 원본 커브는 샘플 첫=끝을 동일 점으로 보정 -> 매핑 후에도 닫힘 유지
            if (c.IsClosed && pts.Count > 1 &&
                pts[0].DistanceTo(pts[pts.Count - 1]) > 1e-9)
                pts.Add(pts[0]);

            return pts.ToArray();
        }

        // ============================================================
        // 다면(서로 다른 바탕 곡면) 연속 stretch: BFS 위상 전달 + 호 길이 균등 셀
        // ============================================================

        private class FacePhase
        {
            public double AnchorU, AnchorV;
            public double CosA, SinA;
            public double IOffset, JOffset;
            // 앵커에서 +/- 방향으로 만든 호 길이 테이블 (u, v 각각)
            public double[] UPars, UArcs; public double UAnchorArc, UTotal;
            public double[] VPars, VArcs; public double VAnchorArc, VTotal;
            public double UMin, UMax, VMin, VMax;
        }

        /// <summary>
        /// 탄젠트로 연결된 여러 면에 패턴을 "이어지도록" 배치한다.
        /// 면 그래프를 BFS로 돌며 공유 모서리에서 격자 위상을 전달하고,
        /// 각 면 위에서는 호 길이 테이블로 셀을 균일하게 놓는다.
        /// </summary>
        public static List<Curve> TileConnected(Brep brep, IList<int> faceIndices,
                                                PatternInfo info, Vector3d refDir,
                                                double angleTolRad)
        {
            var result = new List<Curve>();
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return result;
            if (info == null || !info.Valid || info.UnitCells.Count == 0) return result;

            var faceSet = new HashSet<int>(faceIndices);
            var phases = new Dictionary<int, FacePhase>();
            var fromMap = new Dictionary<int, int>(); // child face -> parent face

            // BFS
            var queue = new Queue<int>();
            int seed = faceIndices[0];
            queue.Enqueue(seed);
            fromMap[seed] = -1;

            while (queue.Count > 0)
            {
                int fi = queue.Dequeue();
                if (phases.ContainsKey(fi)) continue;
                var face = brep.Faces[fi];

                FacePhase phase;
                if (fromMap[fi] < 0)
                    phase = MakeSeedPhase(face, refDir);
                else
                    phase = MakeChildPhase(face, brep.Faces[fromMap[fi]], phases[fromMap[fi]], brep, fi, fromMap[fi], info, refDir, angleTolRad);

                if (phase == null) continue;
                phases[fi] = phase;

                foreach (int ei in face.AdjacentEdges())
                {
                    var edge = brep.Edges[ei];
                    if (!edge.IsSmoothManifoldEdge(angleTolRad)) continue;
                    foreach (int nfi in edge.AdjacentFaces())
                    {
                        if (nfi != fi && faceSet.Contains(nfi) && !phases.ContainsKey(nfi) && !fromMap.ContainsKey(nfi))
                        {
                            fromMap[nfi] = fi;
                            queue.Enqueue(nfi);
                        }
                    }
                }
            }

            // 셀 생성
            double chord = Math.Max(info.CellW, info.CellH) / 20.0;
            var cellPts = new List<Point3d[]>();
            foreach (var c in info.UnitCells) cellPts.Add(SampleCurve(c, chord));

            foreach (var kv in phases)
            {
                var face = brep.Faces[kv.Key];
                GenerateCellsForFace(brep, face, kv.Key, kv.Value, phases, info, refDir, cellPts, result);
            }
            return result;
        }

        /// <summary>
        /// "실제 크기" 다면 버전: 셀 크기는 실제 크기 유지, 셀 위치만 영역에 균등 분포해 끝까지 채움.
        /// (정수 격자 대신 영역 경계까지 닿도록 spacing을 미세 조정)
        /// </summary>
        public static List<Curve> TileConnectedRealSizeFit(Brep brep, IList<int> faceIndices,
                                                            PatternInfo info, Vector3d refDir, double angleTolRad,
                                                            double rotationDeg = 0)
        {
            var result = new List<Curve>();
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return result;
            if (info == null || !info.Valid || info.UnitCells.Count == 0) return result;

            // BFS 위상
            var faceSet = new HashSet<int>(faceIndices);
            var phases = new Dictionary<int, FacePhase>();
            var fromMap = new Dictionary<int, int>();
            var queue = new Queue<int>();
            int seed = faceIndices[0];
            queue.Enqueue(seed);
            fromMap[seed] = -1;
            while (queue.Count > 0)
            {
                int fi = queue.Dequeue();
                if (phases.ContainsKey(fi)) continue;
                var face = brep.Faces[fi];
                FacePhase phase = (fromMap[fi] < 0)
                    ? MakeSeedPhase(face, refDir)
                    : MakeChildPhase(face, brep.Faces[fromMap[fi]], phases[fromMap[fi]], brep, fi, fromMap[fi], info, refDir, angleTolRad);
                if (phase == null) continue;
                phases[fi] = phase;
                foreach (int ei in face.AdjacentEdges())
                {
                    var edge = brep.Edges[ei];
                    if (!edge.IsSmoothManifoldEdge(angleTolRad)) continue;
                    foreach (int nfi in edge.AdjacentFaces())
                    {
                        if (nfi != fi && faceSet.Contains(nfi) && !phases.ContainsKey(nfi) && !fromMap.ContainsKey(nfi))
                        {
                            fromMap[nfi] = fi;
                            queue.Enqueue(nfi);
                        }
                    }
                }
            }
            if (phases.Count == 0) return result;

            // 전체 격자 bbox
            double iMinG = double.MaxValue, iMaxG = double.MinValue;
            double jMinG = double.MaxValue, jMaxG = double.MinValue;
            foreach (var kv in phases)
            {
                var ph = kv.Value;
                double[] cornU = { ph.UMin, ph.UMax, ph.UMin, ph.UMax };
                double[] cornV = { ph.VMin, ph.VMin, ph.VMax, ph.VMax };
                for (int k = 0; k < 4; k++)
                {
                    double sU = InterpArcAtParam(ph.UPars, ph.UArcs, cornU[k]) - ph.UAnchorArc;
                    double sV = InterpArcAtParam(ph.VPars, ph.VArcs, cornV[k]) - ph.VAnchorArc;
                    double iLoc = (sU * ph.CosA + sV * ph.SinA) / info.PitchU;
                    double jLoc = (-sU * ph.SinA + sV * ph.CosA) / info.PitchV;
                    double iVal = iLoc + ph.IOffset;
                    double jVal = jLoc + ph.JOffset;
                    if (iVal < iMinG) iMinG = iVal;
                    if (iVal > iMaxG) iMaxG = iVal;
                    if (jVal < jMinG) jMinG = jVal;
                    if (jVal > jMaxG) jMaxG = jVal;
                }
            }
            if (iMinG >= iMaxG || jMinG >= jMaxG) return result;

            // 셀이 경계까지 닿도록 균등 분포 (cell edge가 region edge에 맞도록)
            double halfI = 0.5 * info.CellW / info.PitchU;
            double halfJ = 0.5 * info.CellH / info.PitchV;
            double iSpan = iMaxG - iMinG;
            double jSpan = jMaxG - jMinG;
            double effI = Math.Max(0, iSpan - 2 * halfI);
            double effJ = Math.Max(0, jSpan - 2 * halfJ);
            // 원본 pitch에 가까운 step이 되도록 cell 개수 결정
            int nU = Math.Max(1, (int)Math.Round(effI) + 1);
            int nV = Math.Max(1, (int)Math.Round(effJ) + 1);
            double stepI = nU > 1 ? effI / (nU - 1) : 0;
            double stepJ = nV > 1 ? effJ / (nV - 1) : 0;

            double chord = Math.Max(info.CellW, info.CellH) / 20.0;
            var cellPts = new List<Point3d[]>();
            foreach (var c in info.UnitCells) cellPts.Add(SampleCurve(c, chord));
            var unitBBox = BoundingBox.Empty;
            foreach (var c in info.UnitCells) unitBBox.Union(c.GetBoundingBox(true));
            double ucX = unitBBox.Center.X, ucY = unitBBox.Center.Y;

            double rotRad = rotationDeg * Math.PI / 180.0;
            double cosR = Math.Cos(rotRad), sinR = Math.Sin(rotRad);
            double absC = Math.Abs(cosR), absS = Math.Abs(sinR);
            double centerI = 0.5 * (iMinG + iMaxG);
            double centerJ = 0.5 * (jMinG + jMaxG);

            // 회전 후 원본 영역을 덮도록 소스 격자 영역 확장
            double iSpanMm = iSpan * info.PitchU;
            double jSpanMm = jSpan * info.PitchV;
            double expISpan = (iSpanMm * absC + jSpanMm * absS) / info.PitchU;
            double expJSpan = (iSpanMm * absS + jSpanMm * absC) / info.PitchV;
            double effIE = Math.Max(0, expISpan - 2 * halfI);
            double effJE = Math.Max(0, expJSpan - 2 * halfJ);
            int nUE = Math.Max(1, (int)Math.Round(effIE) + 1);
            int nVE = Math.Max(1, (int)Math.Round(effJE) + 1);
            double stepIE = nUE > 1 ? effIE / (nUE - 1) : 0;
            double stepJE = nVE > 1 ? effJE / (nVE - 1) : 0;
            double iMinE = centerI - 0.5 * expISpan;
            double jMinE = centerJ - 0.5 * expJSpan;

            // 회전이 거의 0이면 원래 그리드 사용 (정확한 가장자리 맞춤)
            if (Math.Abs(rotationDeg) < 1e-6)
            {
                nUE = nU; nVE = nV;
                stepIE = stepI; stepJE = stepJ;
                iMinE = iMinG; jMinE = jMinG;
            }

            for (int ki = 0; ki < nUE; ki++)
            {
                for (int kj = 0; kj < nVE; kj++)
                {
                    double vi_raw = iMinE + halfI + ki * stepIE;
                    double vj_raw = jMinE + halfJ + kj * stepJE;
                    // 영역 중심 기준 회전 (격자 전체가 강체로 회전)
                    double dxC = (vi_raw - centerI) * info.PitchU;
                    double dyC = (vj_raw - centerJ) * info.PitchV;
                    double dxCR = dxC * cosR - dyC * sinR;
                    double dyCR = dxC * sinR + dyC * cosR;
                    double vi_c = centerI + dxCR / info.PitchU;
                    double vj_c = centerJ + dyCR / info.PitchV;

                    foreach (var pts in cellPts)
                    {
                        var mapped = new Point3d[pts.Length];
                        bool ok = true;
                        for (int k = 0; k < pts.Length; k++)
                        {
                            double dx = pts[k].X - ucX;
                            double dy = pts[k].Y - ucY;
                            // 셀 자체도 같은 각도로 회전
                            double dxR = dx * cosR - dy * sinR;
                            double dyR = dx * sinR + dy * cosR;
                            double vi = vi_c + dxR / info.PitchU;
                            double vj = vj_c + dyR / info.PitchV;

                            bool found = false;
                            foreach (var kv in phases)
                            {
                                double u, v;
                                if (!LatticeToFaceUV(kv.Value, vi, vj, info, out u, out v)) continue;
                                var f = brep.Faces[kv.Key];
                                if (f.IsPointOnFace(u, v) != PointFaceRelation.Exterior)
                                {
                                    mapped[k] = ((Surface)f).PointAt(u, v);
                                    found = true;
                                    break;
                                }
                            }
                            if (!found) { ok = false; break; }
                        }
                        if (ok)
                        {
                            var crv = new PolylineCurve(mapped);
                            if (crv.IsValid) result.Add(crv);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// "실제 크기 - 패턴 부분적용": 패턴 N개를 실제 크기로 표면 위에 한 묶음 올리고
        /// 사용자가 U/V 오프셋(mm)과 회전(도)으로 위치를 자유롭게 잡는다.
        /// </summary>
        public static List<Curve> TileConnectedPartial(Brep brep, IList<int> faceIndices,
                                                       IList<Curve> patternCurves, BoundingBox patternBox,
                                                       Vector3d refDir, double angleTolRad,
                                                       double uOffsetMm, double vOffsetMm, double rotationDeg,
                                                       double scale = 1.0)
        {
            var result = new List<Curve>();
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return result;
            if (patternCurves == null || patternCurves.Count == 0) return result;

            var info = PatternAnalyzer.Analyze(patternCurves);
            if (!info.Valid) return result;

            // BFS 위상 (다른 메서드와 동일)
            var faceSet = new HashSet<int>(faceIndices);
            var phases = new Dictionary<int, FacePhase>();
            var fromMap = new Dictionary<int, int>();
            var queue = new Queue<int>();
            int seed = faceIndices[0];
            queue.Enqueue(seed);
            fromMap[seed] = -1;
            while (queue.Count > 0)
            {
                int fi = queue.Dequeue();
                if (phases.ContainsKey(fi)) continue;
                var face = brep.Faces[fi];
                FacePhase phase = (fromMap[fi] < 0)
                    ? MakeSeedPhase(face, refDir)
                    : MakeChildPhase(face, brep.Faces[fromMap[fi]], phases[fromMap[fi]], brep, fi, fromMap[fi], info, refDir, angleTolRad);
                if (phase == null) continue;
                phases[fi] = phase;
                foreach (int ei in face.AdjacentEdges())
                {
                    var edge = brep.Edges[ei];
                    if (!edge.IsSmoothManifoldEdge(angleTolRad)) continue;
                    foreach (int nfi in edge.AdjacentFaces())
                    {
                        if (nfi != fi && faceSet.Contains(nfi) && !phases.ContainsKey(nfi) && !fromMap.ContainsKey(nfi))
                        {
                            fromMap[nfi] = fi;
                            queue.Enqueue(nfi);
                        }
                    }
                }
            }
            if (phases.Count == 0) return result;

            double rotRad = rotationDeg * Math.PI / 180.0;
            double cosR = Math.Cos(rotRad), sinR = Math.Sin(rotRad);
            double pCx = 0.5 * (patternBox.Min.X + patternBox.Max.X);
            double pCy = 0.5 * (patternBox.Min.Y + patternBox.Max.Y);
            double chord = Math.Max(info.CellW, info.CellH) / 20.0;

            foreach (var c in patternCurves)
            {
                var pts = SampleCurve(c, chord);
                var mapped = new Point3d[pts.Length];
                bool ok = true;
                for (int k = 0; k < pts.Length; k++)
                {
                    // 패턴 중심 기준 오프셋 + 스케일
                    double dx = (pts[k].X - pCx) * scale;
                    double dy = (pts[k].Y - pCy) * scale;
                    // 회전
                    double rx = dx * cosR - dy * sinR;
                    double ry = dx * sinR + dy * cosR;
                    // 평행이동 (mm = arc-length)
                    rx += uOffsetMm;
                    ry += vOffsetMm;
                    // 격자 좌표 (seed = (0, 0))
                    double vi = rx / info.PitchU;
                    double vj = ry / info.PitchV;

                    bool found = false;
                    foreach (var kv in phases)
                    {
                        double u, v;
                        if (!LatticeToFaceUV(kv.Value, vi, vj, info, out u, out v)) continue;
                        var f = brep.Faces[kv.Key];
                        if (f.IsPointOnFace(u, v) != PointFaceRelation.Exterior)
                        {
                            mapped[k] = ((Surface)f).PointAt(u, v);
                            found = true;
                            break;
                        }
                    }
                    if (!found) { ok = false; break; }
                }
                if (ok)
                {
                    var crv = new PolylineCurve(mapped);
                    if (crv.IsValid) result.Add(crv);
                }
            }
            return result;
        }

        /// <summary>
        /// "한 장 늘려 맞춤" 다면 버전: 패턴 커브 N개를 그대로 발생시켜 영역에 매핑한다.
        /// BFS 위상으로 격자를 만들고, 패턴 bbox -> 영역 격자 bbox(균일 스케일)로 매핑.
        /// 결과 셀 수 = 패턴 커브 수 (모두 영역 안에 들어가는 경우).
        /// </summary>
        public static List<Curve> TileConnectedStretch(Brep brep, IList<int> faceIndices,
                                                       IList<Curve> patternCurves, BoundingBox patternBox,
                                                       Vector3d refDir, double angleTolRad,
                                                       int nU = 1, int nV = 1, double margin = 0,
                                                       bool flipH = false, bool flipV = false,
                                                       double rotationDeg = 0)
        {
            var result = new List<Curve>();
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return result;
            if (patternCurves == null || patternCurves.Count == 0) return result;

            var info = PatternAnalyzer.Analyze(patternCurves);
            if (!info.Valid) return result;

            // BFS 위상 (TileConnected와 동일)
            var faceSet = new HashSet<int>(faceIndices);
            var phases = new Dictionary<int, FacePhase>();
            var fromMap = new Dictionary<int, int>();
            var queue = new Queue<int>();
            int seed = faceIndices[0];
            queue.Enqueue(seed);
            fromMap[seed] = -1;

            while (queue.Count > 0)
            {
                int fi = queue.Dequeue();
                if (phases.ContainsKey(fi)) continue;
                var face = brep.Faces[fi];

                FacePhase phase;
                if (fromMap[fi] < 0)
                    phase = MakeSeedPhase(face, refDir);
                else
                    phase = MakeChildPhase(face, brep.Faces[fromMap[fi]], phases[fromMap[fi]], brep, fi, fromMap[fi], info, refDir, angleTolRad);

                if (phase == null) continue;
                phases[fi] = phase;

                foreach (int ei in face.AdjacentEdges())
                {
                    var edge = brep.Edges[ei];
                    if (!edge.IsSmoothManifoldEdge(angleTolRad)) continue;
                    foreach (int nfi in edge.AdjacentFaces())
                    {
                        if (nfi != fi && faceSet.Contains(nfi) && !phases.ContainsKey(nfi) && !fromMap.ContainsKey(nfi))
                        {
                            fromMap[nfi] = fi;
                            queue.Enqueue(nfi);
                        }
                    }
                }
            }

            if (phases.Count == 0) return result;

            // 전체 격자 bbox (fractional)
            double iMinG = double.MaxValue, iMaxG = double.MinValue;
            double jMinG = double.MaxValue, jMaxG = double.MinValue;
            foreach (var kv in phases)
            {
                var ph = kv.Value;
                double[] cornU = { ph.UMin, ph.UMax, ph.UMin, ph.UMax };
                double[] cornV = { ph.VMin, ph.VMin, ph.VMax, ph.VMax };
                for (int k = 0; k < 4; k++)
                {
                    double sU = InterpArcAtParam(ph.UPars, ph.UArcs, cornU[k]) - ph.UAnchorArc;
                    double sV = InterpArcAtParam(ph.VPars, ph.VArcs, cornV[k]) - ph.VAnchorArc;
                    double iLoc = (sU * ph.CosA + sV * ph.SinA) / info.PitchU;
                    double jLoc = (-sU * ph.SinA + sV * ph.CosA) / info.PitchV;
                    double iVal = iLoc + ph.IOffset;
                    double jVal = jLoc + ph.JOffset;
                    if (iVal < iMinG) iMinG = iVal;
                    if (iVal > iMaxG) iMaxG = iVal;
                    if (jVal < jMinG) jMinG = jVal;
                    if (jVal > jMaxG) jMaxG = jVal;
                }
            }
            if (iMinG >= iMaxG || jMinG >= jMaxG) return result;

            // 마진 인셋: 외곽선에서 margin mm 안쪽으로 격자 영역을 줄임
            if (margin > 1e-9)
            {
                double mi = margin / info.PitchU;
                double mj = margin / info.PitchV;
                iMinG += mi; iMaxG -= mi;
                jMinG += mj; jMaxG -= mj;
                if (iMinG >= iMaxG || jMinG >= jMaxG) return result;
            }

            double pw = patternBox.Max.X - patternBox.Min.X;
            double ph2 = patternBox.Max.Y - patternBox.Min.Y;
            if (pw < 1e-9 || ph2 < 1e-9) return result;

            // 비균일 스케일 + nU x nV 반복 (반복 사이에 패턴 내부 간격과 동일한 갭 유지)
            nU = Math.Max(1, nU);
            nV = Math.Max(1, nV);
            double iSpan = iMaxG - iMinG;
            double jSpan = jMaxG - jMinG;

            // 회전된 패턴 bbox 크기 (회전 0이면 원래 pw, ph2)
            double absC = Math.Abs(Math.Cos(rotationDeg * Math.PI / 180.0));
            double absS = Math.Abs(Math.Sin(rotationDeg * Math.PI / 180.0));
            double Wrot = pw * absC + ph2 * absS;
            double Hrot = pw * absS + ph2 * absC;

            // 패턴 내부 인접 셀 사이 간격 (반복 사이 간격으로 사용)
            double gapX = nU > 1 ? EstimateGap(patternCurves, 0) : 0;
            double gapY = nV > 1 ? EstimateGap(patternCurves, 1) : 0;
            double effSpanU = nU * Wrot + (nU - 1) * gapX;
            double effSpanV = nV * Hrot + (nV - 1) * gapY;

            double chord = Math.Max(info.CellW, info.CellH) / 20.0;

            for (int ti = 0; ti < nU; ti++)
            {
                for (int tj = 0; tj < nV; tj++)
                {
                    foreach (var c in patternCurves)
                    {
                        var pts = SampleCurve(c, chord);
                        var mapped = new Point3d[pts.Length];
                        bool ok = true;
                        for (int k = 0; k < pts.Length; k++)
                        {
                            double xRel = pts[k].X - patternBox.Min.X;
                            double yRel = pts[k].Y - patternBox.Min.Y;
                            if (flipH) xRel = pw - xRel;
                            if (flipV) yRel = ph2 - yRel;
                            // 회전 후 새 bbox(Wrot × Hrot) 내 좌표로 변환
                            if (Math.Abs(rotationDeg) > 1e-9)
                            {
                                double rr = rotationDeg * Math.PI / 180.0;
                                double cosR = Math.Cos(rr), sinR = Math.Sin(rr);
                                double dx = xRel - pw * 0.5, dy = yRel - ph2 * 0.5;
                                xRel = dx * cosR - dy * sinR + Wrot * 0.5;
                                yRel = dx * sinR + dy * cosR + Hrot * 0.5;
                            }
                            double gx = ti * (Wrot + gapX) + xRel;
                            double gy = tj * (Hrot + gapY) + yRel;
                            double vi = iMinG + gx / effSpanU * iSpan;
                            double vj = jMinG + gy / effSpanV * jSpan;

                            bool found = false;
                            foreach (var kv in phases)
                            {
                                double u2, v2;
                                if (!LatticeToFaceUV(kv.Value, vi, vj, info, out u2, out v2)) continue;
                                var f2 = brep.Faces[kv.Key];
                                if (f2.IsPointOnFace(u2, v2) != PointFaceRelation.Exterior)
                                {
                                    mapped[k] = ((Surface)f2).PointAt(u2, v2);
                                    found = true;
                                    break;
                                }
                            }
                            if (!found) { ok = false; break; }
                        }
                        if (ok)
                        {
                            var crv = new PolylineCurve(mapped);
                            if (crv.IsValid) result.Add(crv);
                        }
                    }
                }
            }
            return result;
        }

        // 격자 좌표 (vi, vj) -> 특정 면의 UV (있으면 true)
        private static bool LatticeToFaceUV(FacePhase ph, double vi, double vj, PatternInfo info, out double u, out double v)
        {
            u = 0; v = 0;
            double iLoc = vi - ph.IOffset;
            double jLoc = vj - ph.JOffset;
            double sU = iLoc * info.PitchU * ph.CosA - jLoc * info.PitchV * ph.SinA;
            double sV = iLoc * info.PitchU * ph.SinA + jLoc * info.PitchV * ph.CosA;
            double targetUArc = ph.UAnchorArc + sU;
            double targetVArc = ph.VAnchorArc + sV;
            if (targetUArc < ph.UArcs[0] - 1e-6 || targetUArc > ph.UArcs[ph.UArcs.Length - 1] + 1e-6) return false;
            if (targetVArc < ph.VArcs[0] - 1e-6 || targetVArc > ph.VArcs[ph.VArcs.Length - 1] + 1e-6) return false;
            u = InterpParam(ph.UArcs, ph.UPars, targetUArc);
            v = InterpParam(ph.VArcs, ph.VPars, targetVArc);
            return true;
        }

        private static FacePhase MakeSeedPhase(BrepFace face, Vector3d refDir)
        {
            double uMin, uMax, vMin, vMax;
            GetFaceUvBox(face, out uMin, out uMax, out vMin, out vMax);
            double uA = 0.5 * (uMin + uMax);
            double vA = 0.5 * (vMin + vMax);

            Point3d s0; Vector3d du, dv;
            if (!EvalDeriv(face, uA, vA, out s0, out du, out dv)) return null;
            double lu = du.Length, lv = dv.Length;
            if (lu < 1e-9 || lv < 1e-9) return null;

            double cosA, sinA;
            ComputeRotation(du, dv, lu, lv, refDir, out cosA, out sinA);

            var ph = new FacePhase
            {
                AnchorU = uA, AnchorV = vA,
                CosA = cosA, SinA = sinA,
                IOffset = 0, JOffset = 0,
                UMin = uMin, UMax = uMax, VMin = vMin, VMax = vMax
            };
            BuildAnchoredTables(face, ph);
            return ph;
        }

        private static FacePhase MakeChildPhase(BrepFace face, BrepFace fromFace, FacePhase fromPhase,
                                                 Brep brep, int fi, int fromFi,
                                                 PatternInfo info, Vector3d refDir, double angleTolRad)
        {
            int sharedEdgeIdx = FindSharedSmoothEdge(brep, fi, fromFi, angleTolRad);
            if (sharedEdgeIdx < 0) return null;
            var edge = brep.Edges[sharedEdgeIdx];
            var Pe = edge.PointAtNormalizedLength(0.5);

            // fromFace에서 Pe의 격자 인덱스 (호 길이 기준)
            double uPeF, vPeF;
            if (!((Surface)fromFace).ClosestPoint(Pe, out uPeF, out vPeF)) return null;

            double sFromU = ArcOffsetFromAnchor(fromPhase, true, uPeF) - fromPhase.UAnchorArc;
            double sFromV = ArcOffsetFromAnchor(fromPhase, false, vPeF) - fromPhase.VAnchorArc;
            // 회전 적용해 local (i,j)
            double iLoc = (sFromU * fromPhase.CosA + sFromV * fromPhase.SinA) / info.PitchU;
            double jLoc = (-sFromU * fromPhase.SinA + sFromV * fromPhase.CosA) / info.PitchV;
            double iAt = fromPhase.IOffset + iLoc;
            double jAt = fromPhase.JOffset + jLoc;

            // 새 면에서 Pe 위치 (앵커)
            double uPeT, vPeT;
            if (!((Surface)face).ClosestPoint(Pe, out uPeT, out vPeT)) return null;

            Point3d s0; Vector3d du, dv;
            if (!EvalDeriv(face, uPeT, vPeT, out s0, out du, out dv)) return null;
            double lu = du.Length, lv = dv.Length;
            if (lu < 1e-9 || lv < 1e-9) return null;

            double cosA, sinA;
            ComputeRotation(du, dv, lu, lv, refDir, out cosA, out sinA);

            double uMin, uMax, vMin, vMax;
            GetFaceUvBox(face, out uMin, out uMax, out vMin, out vMax);
            var ph = new FacePhase
            {
                AnchorU = uPeT, AnchorV = vPeT,
                CosA = cosA, SinA = sinA,
                IOffset = iAt, JOffset = jAt,
                UMin = uMin, UMax = uMax, VMin = vMin, VMax = vMax
            };
            BuildAnchoredTables(face, ph);
            return ph;
        }

        private static void BuildAnchoredTables(BrepFace face, FacePhase ph)
        {
            // u 방향: v=anchor 고정, u∈[uMin,uMax]
            BuildArcTable((Surface)face, false, ph.AnchorV, ph.UMin, ph.UMax, out ph.UPars, out ph.UArcs, out ph.UTotal);
            ph.UAnchorArc = InterpArcAtParam(ph.UPars, ph.UArcs, ph.AnchorU);
            // v 방향: u=anchor 고정, v∈[vMin,vMax]
            BuildArcTable((Surface)face, true, ph.AnchorU, ph.VMin, ph.VMax, out ph.VPars, out ph.VArcs, out ph.VTotal);
            ph.VAnchorArc = InterpArcAtParam(ph.VPars, ph.VArcs, ph.AnchorV);
        }

        // 파라미터 -> 누적 호 길이
        private static double InterpArcAtParam(double[] pars, double[] arcs, double target)
        {
            int n = pars.Length;
            if (target <= pars[0]) return arcs[0];
            if (target >= pars[n - 1]) return arcs[n - 1];
            for (int i = 1; i < n; i++)
            {
                if (pars[i] >= target)
                {
                    double seg = pars[i] - pars[i - 1];
                    double f = seg > 1e-12 ? (target - pars[i - 1]) / seg : 0;
                    return arcs[i - 1] + (arcs[i] - arcs[i - 1]) * f;
                }
            }
            return arcs[n - 1];
        }

        private static double ArcOffsetFromAnchor(FacePhase ph, bool uDir, double param)
        {
            return uDir ? InterpArcAtParam(ph.UPars, ph.UArcs, param)
                        : InterpArcAtParam(ph.VPars, ph.VArcs, param);
        }

        private static int FindSharedSmoothEdge(Brep brep, int faceA, int faceB, double angleTolRad)
        {
            foreach (int ei in brep.Faces[faceA].AdjacentEdges())
            {
                var edge = brep.Edges[ei];
                if (!edge.IsSmoothManifoldEdge(angleTolRad)) continue;
                foreach (int fi in edge.AdjacentFaces())
                    if (fi == faceB) return ei;
            }
            return -1;
        }

        private static void ComputeRotation(Vector3d du, Vector3d dv, double lu, double lv, Vector3d refDir,
                                            out double cosA, out double sinA)
        {
            cosA = 1.0; sinA = 0.0;
            if (refDir.Length < 1e-9) return;
            var duHat = du / lu;
            var dvHat = dv / lv;
            cosA = refDir * duHat;
            sinA = refDir * dvHat;
            double mag = Math.Sqrt(cosA * cosA + sinA * sinA);
            if (mag > 1e-9) { cosA /= mag; sinA /= mag; }
            else { cosA = 1.0; sinA = 0.0; }
        }

        private static void GetFaceUvBox(BrepFace face, out double uMin, out double uMax, out double vMin, out double vMax)
        {
            uMin = face.Domain(0).T0; uMax = face.Domain(0).T1;
            vMin = face.Domain(1).T0; vMax = face.Domain(1).T1;
            try
            {
                var c2 = face.OuterLoop?.To2dCurve();
                if (c2 != null)
                {
                    var bb = c2.GetBoundingBox(true);
                    uMin = bb.Min.X; uMax = bb.Max.X;
                    vMin = bb.Min.Y; vMax = bb.Max.Y;
                }
            }
            catch { }
        }

        private static void GenerateCellsForFace(Brep brep, BrepFace face, int faceIndex, FacePhase ph,
                                                  Dictionary<int, FacePhase> phases,
                                                  PatternInfo info,
                                                  Vector3d refDir, List<Point3d[]> cellPts, List<Curve> outResult)
        {
            // 면 위에서 셀 i, j 범위 추정: UV 박스 모서리들의 lattice 좌표 범위
            double iMin = double.MaxValue, iMax = double.MinValue;
            double jMin = double.MaxValue, jMax = double.MinValue;
            double[] corners = { ph.UMin, ph.UMax, ph.UMin, ph.UMax };
            double[] cornersV = { ph.VMin, ph.VMin, ph.VMax, ph.VMax };
            for (int k = 0; k < 4; k++)
            {
                double sU = InterpArcAtParam(ph.UPars, ph.UArcs, corners[k]) - ph.UAnchorArc;
                double sV = InterpArcAtParam(ph.VPars, ph.VArcs, cornersV[k]) - ph.VAnchorArc;
                double iLoc = (sU * ph.CosA + sV * ph.SinA) / info.PitchU;
                double jLoc = (-sU * ph.SinA + sV * ph.CosA) / info.PitchV;
                if (iLoc < iMin) iMin = iLoc;
                if (iLoc > iMax) iMax = iLoc;
                if (jLoc < jMin) jMin = jLoc;
                if (jLoc > jMax) jMax = jLoc;
            }
            // 전역 정수 인덱스 범위
            int giLo = (int)Math.Floor(iMin + ph.IOffset) - 1;
            int giHi = (int)Math.Ceiling(iMax + ph.IOffset) + 1;
            int gjLo = (int)Math.Floor(jMin + ph.JOffset) - 1;
            int gjHi = (int)Math.Ceiling(jMax + ph.JOffset) + 1;
            const int safetyCap = 100000;

            Surface srf = face;
            var ud = srf.Domain(0); var vd = srf.Domain(1);

            for (int gi = giLo; gi <= giHi; gi++)
            {
                for (int gj = gjLo; gj <= gjHi; gj++)
                {
                    double iLoc = gi - ph.IOffset;
                    double jLoc = gj - ph.JOffset;
                    // 셀 중심의 (sU, sV) - 앵커 기준 호 길이 오프셋
                    double sU = iLoc * info.PitchU * ph.CosA - jLoc * info.PitchV * ph.SinA;
                    double sV = iLoc * info.PitchU * ph.SinA + jLoc * info.PitchV * ph.CosA;
                    // 호 길이 → 파라미터
                    double targetUArc = ph.UAnchorArc + sU;
                    double targetVArc = ph.VAnchorArc + sV;
                    if (targetUArc < ph.UArcs[0] - 1e-6 || targetUArc > ph.UArcs[ph.UArcs.Length - 1] + 1e-6) continue;
                    if (targetVArc < ph.VArcs[0] - 1e-6 || targetVArc > ph.VArcs[ph.VArcs.Length - 1] + 1e-6) continue;
                    double u = InterpParam(ph.UArcs, ph.UPars, targetUArc);
                    double v = InterpParam(ph.VArcs, ph.VPars, targetVArc);
                    if (face.IsPointOnFace(u, v) == PointFaceRelation.Exterior) continue;

                    Point3d s0; Vector3d du, dv;
                    if (!EvalDeriv(srf, u, v, out s0, out du, out dv)) continue;
                    double lu = du.Length, lv = dv.Length;
                    if (lu < 1e-9 || lv < 1e-9) continue;

                    var duHat = du / lu;
                    var dvHat = dv / lv;

                    foreach (var pts in cellPts)
                    {
                        var mapped = new Point3d[pts.Length];
                        for (int k = 0; k < pts.Length; k++)
                        {
                            // 꼭짓점의 (refDir, perp) 오프셋 -> (U_arc, V_arc) 회전
                            double sUoff = pts[k].X * ph.CosA - pts[k].Y * ph.SinA;
                            double sVoff = pts[k].X * ph.SinA + pts[k].Y * ph.CosA;
                            double tUArc = ph.UAnchorArc + sU + sUoff;
                            double tVArc = ph.VAnchorArc + sV + sVoff;

                            bool placed = false;
                            // 1) 현재 면 호 길이 테이블로 직접 조회 (곡률 무관 균일 셀 크기)
                            if (tUArc >= ph.UArcs[0] - 1e-6 && tUArc <= ph.UArcs[ph.UArcs.Length - 1] + 1e-6 &&
                                tVArc >= ph.VArcs[0] - 1e-6 && tVArc <= ph.VArcs[ph.VArcs.Length - 1] + 1e-6)
                            {
                                double uu = InterpParam(ph.UArcs, ph.UPars, tUArc);
                                double vv = InterpParam(ph.VArcs, ph.VPars, tVArc);
                                if (face.IsPointOnFace(uu, vv) != PointFaceRelation.Exterior)
                                {
                                    mapped[k] = srf.PointAt(uu, vv);
                                    placed = true;
                                }
                            }

                            if (!placed)
                            {
                                // 2) 격자 좌표로 인접 면 조회
                                double vi = gi + pts[k].X / info.PitchU;
                                double vj = gj + pts[k].Y / info.PitchV;
                                foreach (var kv2 in phases)
                                {
                                    if (kv2.Key == faceIndex) continue;
                                    double u2, v2;
                                    if (!LatticeToFaceUV(kv2.Value, vi, vj, info, out u2, out v2)) continue;
                                    var f2 = brep.Faces[kv2.Key];
                                    if (f2.IsPointOnFace(u2, v2) != PointFaceRelation.Exterior)
                                    {
                                        mapped[k] = ((Surface)f2).PointAt(u2, v2);
                                        placed = true;
                                        break;
                                    }
                                }
                            }

                            if (!placed)
                            {
                                // 3) 최후 폴백: 접선 외삽 → 브렙 가장 가까운 점
                                double rxT = pts[k].X * ph.CosA - pts[k].Y * ph.SinA;
                                double ryT = pts[k].X * ph.SinA + pts[k].Y * ph.CosA;
                                var tangentPos = s0 + rxT * duHat + ryT * dvHat;
                                Point3d cp; ComponentIndex ci; double cs2, ct2; Vector3d nrm;
                                if (brep.ClosestPoint(tangentPos, out cp, out ci, out cs2, out ct2, double.MaxValue, out nrm))
                                    mapped[k] = cp;
                                else
                                    mapped[k] = tangentPos;
                            }
                        }
                        var crv = new PolylineCurve(mapped);
                        if (crv.IsValid) outResult.Add(crv);
                        if (outResult.Count > safetyCap) return;
                    }
                }
            }
        }
    }
}
