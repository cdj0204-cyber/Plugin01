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
        /// BFS phase 기반 RealSize (참조용 비활성) — polar-UV face 에서 radial 패턴 문제로 사용 안 함.
        /// </summary>
        private static List<Curve> TileConnectedRealSizeFit_BFS_Disabled(Brep brep, IList<int> faceIndices,
                                                            PatternInfo info, Vector3d refDir, double angleTolRad,
                                                            double rotationDeg = 0)
        {
            var result = new List<Curve>();
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return result;
            if (info == null || !info.Valid || info.UnitCells.Count == 0) return result;
            var faceSet = new HashSet<int>(faceIndices);

            // === BFS Phase Propagation (whole brep, multi-pass) ===
            var phases = new Dictionary<int, FacePhase>();
            var fromMap = new Dictionary<int, int>();
            var queue = new Queue<int>();
            int seed = faceIndices[0];
            queue.Enqueue(seed);
            fromMap[seed] = -1;
            // Pass 1: smooth edges
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
                        if (nfi != fi && !phases.ContainsKey(nfi) && !fromMap.ContainsKey(nfi))
                        {
                            fromMap[nfi] = fi;
                            queue.Enqueue(nfi);
                        }
                    }
                }
            }
            // Pass 2: loose (any shared edge)
            bool progressed = true;
            int loopGuard = brep.Faces.Count + 4;
            while (progressed && loopGuard-- > 0)
            {
                progressed = false;
                for (int fi = 0; fi < brep.Faces.Count; fi++)
                {
                    if (phases.ContainsKey(fi)) continue;
                    int neighbor = FindAnyPhasedNeighbor(brep, fi, phases);
                    if (neighbor < 0) continue;
                    var ph = MakeChildPhaseLoose(brep.Faces[fi], brep.Faces[neighbor], phases[neighbor], brep, fi, neighbor, info, refDir);
                    if (ph != null) { phases[fi] = ph; progressed = true; }
                }
            }
            // Pass 3: independent seed for any remaining selected faces
            foreach (int fi in faceIndices)
            {
                if (phases.ContainsKey(fi)) continue;
                var sp = MakeSeedPhase(brep.Faces[fi], refDir);
                if (sp != null) phases[fi] = sp;
            }
            if (phases.Count == 0) return result;

            // === Lattice bounds (arc-length based, selected face phases only) ===
            double iMinG = double.MaxValue, iMaxG = double.MinValue;
            double jMinG = double.MaxValue, jMaxG = double.MinValue;
            foreach (var kv in phases)
            {
                if (!faceSet.Contains(kv.Key)) continue;
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

            // === Lattice iteration setup (rotation + 끝까지 채우는 spacing) ===
            double rotRad = rotationDeg * Math.PI / 180.0;
            double cosR = Math.Cos(rotRad), sinR = Math.Sin(rotRad);
            double absC = Math.Abs(cosR), absS = Math.Abs(sinR);
            double centerI = 0.5 * (iMinG + iMaxG);
            double centerJ = 0.5 * (jMinG + jMaxG);
            double iSpan = iMaxG - iMinG;
            double jSpan = jMaxG - jMinG;
            double halfI = 0.5 * info.CellW / info.PitchU;
            double halfJ = 0.5 * info.CellH / info.PitchV;
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
            if (Math.Abs(rotationDeg) < 1e-6)
            {
                double effI = Math.Max(0, iSpan - 2 * halfI);
                double effJ = Math.Max(0, jSpan - 2 * halfJ);
                int nU = Math.Max(1, (int)Math.Round(effI) + 1);
                int nV = Math.Max(1, (int)Math.Round(effJ) + 1);
                nUE = nU; nVE = nV;
                stepIE = nU > 1 ? effI / (nU - 1) : 0;
                stepJE = nV > 1 ? effJ / (nV - 1) : 0;
                iMinE = iMinG; jMinE = jMinG;
            }

            // 패턴 단위셀
            double chord = Math.Max(info.CellW, info.CellH) / 20.0;
            var cellPts = new List<Point3d[]>();
            foreach (var c in info.UnitCells) cellPts.Add(SampleCurve(c, chord));
            var unitBBox = BoundingBox.Empty;
            foreach (var c in info.UnitCells) unitBBox.Union(c.GetBoundingBox(true));
            double ucX = unitBBox.Center.X, ucY = unitBBox.Center.Y;

            // Dedup (가벼움 — arc-length 가 이미 균일 spacing 보장)
            var placedCells = new List<KeyValuePair<int, Point3d>>();
            double dedupSameFace = Math.Min(info.PitchU, info.PitchV) * 0.5;
            double dedupDiffFace = 0.5;
            double snapMaxVertex = Math.Max(info.PitchU, info.PitchV) * 2.0;

            for (int ki = 0; ki < nUE; ki++)
            {
                for (int kj = 0; kj < nVE; kj++)
                {
                    double vi_raw = iMinE + halfI + ki * stepIE;
                    double vj_raw = jMinE + halfJ + kj * stepJE;
                    double dxC = (vi_raw - centerI) * info.PitchU;
                    double dyC = (vj_raw - centerJ) * info.PitchV;
                    double dxCR = dxC * cosR - dyC * sinR;
                    double dyCR = dxC * sinR + dyC * cosR;
                    double vi_c = centerI + dxCR / info.PitchU;
                    double vj_c = centerJ + dyCR / info.PitchV;

                    // closest-anchor primary face (drift 최소화)
                    int primaryFi = -1;
                    double bestDistSq = double.MaxValue;
                    double primUc = 0, primVc = 0;
                    foreach (var kv in phases)
                    {
                        if (!faceSet.Contains(kv.Key)) continue;
                        double uc, vc;
                        if (!LatticeToFaceUV(kv.Value, vi_c, vj_c, info, out uc, out vc)) continue;
                        var f = brep.Faces[kv.Key];
                        if (f.IsPointOnFace(uc, vc) == PointFaceRelation.Exterior) continue;
                        double dvi = vi_c - kv.Value.IOffset;
                        double dvj = vj_c - kv.Value.JOffset;
                        double dsq = dvi * dvi + dvj * dvj;
                        if (dsq < bestDistSq)
                        {
                            bestDistSq = dsq;
                            primaryFi = kv.Key;
                            primUc = uc; primVc = vc;
                        }
                    }
                    if (primaryFi < 0) continue;

                    var primFace = brep.Faces[primaryFi];
                    var primPhase = phases[primaryFi];
                    Point3d cellCenter3d; Vector3d duVec, dvVec;
                    if (!EvalDeriv(primFace, primUc, primVc, out cellCenter3d, out duVec, out dvVec)) continue;
                    double luLen = duVec.Length, lvLen = dvVec.Length;
                    if (luLen < 1e-9 || lvLen < 1e-9) continue;
                    var duHat = duVec / luLen;
                    var dvHat = dvVec / lvLen;
                    // primary phase 의 rotation 으로 lattice axis 정렬
                    var Ti_local = primPhase.CosA * duHat + primPhase.SinA * dvHat;
                    var Tj_local = -primPhase.SinA * duHat + primPhase.CosA * dvHat;

                    // Dedup
                    bool isDuplicate = false;
                    foreach (var p in placedCells)
                    {
                        double thresh = (p.Key == primaryFi) ? dedupSameFace : dedupDiffFace;
                        if (p.Value.DistanceTo(cellCenter3d) < thresh) { isDuplicate = true; break; }
                    }
                    if (isDuplicate) continue;
                    placedCells.Add(new KeyValuePair<int, Point3d>(primaryFi, cellCenter3d));

                    // Hex 배치 — no scale (uniform size)
                    foreach (var pts in cellPts)
                    {
                        var mapped = new Point3d[pts.Length];
                        for (int k = 0; k < pts.Length; k++)
                        {
                            double dx = pts[k].X - ucX;
                            double dy = pts[k].Y - ucY;
                            double dxR = dx * cosR - dy * sinR;
                            double dyR = dx * sinR + dy * cosR;
                            // 자연 크기 그대로 (scale 없음) — 면 곡률 무관 동일 크기
                            Point3d flat = cellCenter3d + dxR * Ti_local + dyR * Tj_local;

                            // Vertex: primary face 우선 snap
                            double vU, vV;
                            bool placed = false;
                            if (((Surface)primFace).ClosestPoint(flat, out vU, out vV))
                            {
                                var vSnap = ((Surface)primFace).PointAt(vU, vV);
                                if (primFace.IsPointOnFace(vU, vV) != PointFaceRelation.Exterior &&
                                    vSnap.DistanceTo(flat) < snapMaxVertex)
                                {
                                    mapped[k] = vSnap;
                                    placed = true;
                                }
                            }
                            if (!placed)
                            {
                                Point3d snapped;
                                double tightSnap = Math.Max(info.PitchU, info.PitchV) * 1.0;
                                if (TrySnapToSelectedFaces(brep, faceSet, flat, tightSnap, out snapped))
                                    mapped[k] = snapped;
                                else
                                    mapped[k] = flat;
                            }
                        }
                        var crv = new PolylineCurve(mapped);
                        if (crv.IsValid) result.Add(crv);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// "실제 크기" 메인 (Surface Walking BFS):
        ///   - 패턴을 천처럼 표면 위에 입히는 방식. 각 cell 이 이웃에서 surface 위로 정확히 PitchU/V world 거리 walking
        ///   - 곡률 무관 동일 spacing (사용자 요구 #4)
        ///   - 선택 면 밖 walk 실패 → 자동 boundary 처리 (사용자 요구 #1, #2)
        ///   - 모든 cell 이 이웃과 surface 따라 연결 → seam 자연 연속 (사용자 요구 #3, #5)
        ///   - 5개 면을 단일한 통합 면으로 인식 (사용자 요구 #5)
        /// </summary>
        public static List<Curve> TileConnectedRealSizeFit(Brep brep, IList<int> faceIndices,
                                                            PatternInfo info, Vector3d refDir, double angleTolRad,
                                                            double rotationDeg = 0)
        {
            var result = new List<Curve>();
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return result;
            if (info == null || !info.Valid || info.UnitCells.Count == 0) return result;
            var faceSet = new HashSet<int>(faceIndices);

            // === Lattice anchor + 방향 (평균 normal + World 축 fallback) ===
            Vector3d avgN = Vector3d.Zero;
            Vector3d sumCenter = Vector3d.Zero;
            int validCount = 0;
            foreach (int fi in faceIndices)
            {
                var face = brep.Faces[fi];
                double fuMin, fuMax, fvMin, fvMax;
                GetFaceUvBox(face, out fuMin, out fuMax, out fvMin, out fvMax);
                double fuc = 0.5 * (fuMin + fuMax);
                double fvc = 0.5 * (fvMin + fvMax);
                Point3d c; Vector3d du, dv;
                if (!EvalDeriv(face, fuc, fvc, out c, out du, out dv)) continue;
                if (du.Length < 1e-9 || dv.Length < 1e-9) continue;
                var n = Vector3d.CrossProduct(du, dv);
                if (n.Length < 1e-9) continue;
                n.Unitize();
                avgN += n;
                sumCenter += (Vector3d)c;
                validCount++;
            }
            if (validCount == 0) return result;
            Point3d centroidPt = new Point3d(sumCenter / validCount);
            if (avgN.Length < 1e-6) return result;
            avgN.Unitize();

            // avgN 을 가까운 World 축으로 snap (사용자 #4: tilt 제거 — lattice 가 World 축 정렬)
            double absX = Math.Abs(avgN.X), absY = Math.Abs(avgN.Y), absZ = Math.Abs(avgN.Z);
            if (absZ > 0.9 && absZ >= absX && absZ >= absY) avgN = new Vector3d(0, 0, avgN.Z > 0 ? 1 : -1);
            else if (absY > 0.9 && absY >= absX && absY >= absZ) avgN = new Vector3d(0, avgN.Y > 0 ? 1 : -1, 0);
            else if (absX > 0.9 && absX >= absY && absX >= absZ) avgN = new Vector3d(avgN.X > 0 ? 1 : -1, 0, 0);

            // Ti, Tj 결정 (refDir → World 축 fallback)
            Vector3d Ti_init = Vector3d.Zero;
            if (refDir.Length > 1e-9)
            {
                var refOnPlane = refDir - (refDir * avgN) * avgN;
                if (refOnPlane.Length > 1e-6) { refOnPlane.Unitize(); Ti_init = refOnPlane; }
            }
            if (Ti_init.Length < 1e-6)
            {
                Vector3d[] axes = { Vector3d.YAxis, Vector3d.XAxis, Vector3d.ZAxis };
                foreach (var axis in axes)
                {
                    var proj = axis - (axis * avgN) * avgN;
                    if (proj.Length > 1e-6) { proj.Unitize(); Ti_init = proj; break; }
                }
            }
            if (Ti_init.Length < 1e-6) return result;
            var Tj_init = Vector3d.CrossProduct(avgN, Ti_init);
            Tj_init.Unitize();

            double rotRad = rotationDeg * Math.PI / 180.0;
            double cosR = Math.Cos(rotRad), sinR = Math.Sin(rotRad);
            var Ti_world = cosR * Ti_init + sinR * Tj_init;
            var Tj_world = -sinR * Ti_init + cosR * Tj_init;

            // seed: centroid → surface
            Point3d seedSurf; int seedFi;
            BoundingBox sbb = BoundingBox.Empty;
            foreach (int fi in faceIndices) sbb.Union(brep.Faces[fi].GetBoundingBox(true));
            double bboxDiag = sbb.Diagonal.Length;
            if (!TrySnapToSelectedFacesWithIndex(brep, faceSet, centroidPt, bboxDiag, out seedSurf, out seedFi))
                return result;

            // 패턴 단위셀
            double chord = Math.Max(info.CellW, info.CellH) / 20.0;
            var cellPts = new List<Point3d[]>();
            foreach (var c in info.UnitCells) cellPts.Add(SampleCurve(c, chord));
            var unitBBox = BoundingBox.Empty;
            foreach (var c in info.UnitCells) unitBBox.Union(c.GetBoundingBox(true));
            double ucX = unitBBox.Center.X, ucY = unitBBox.Center.Y;

            // === Surface Walking BFS ===
            // 각 cell 이 이웃에서 surface 위로 PitchU/V world 거리 walking → snap
            // → 곡률 무관 동일 spacing (사용자가 원하는 "패턴 천 입히기" 효과)
            var placed = new Dictionary<long, CellPos>();
            long Encode(int i, int j) => ((long)(i + 10000) << 20) | (long)(j + 10000);

            placed[Encode(0, 0)] = new CellPos { Pt = seedSurf, FaceIdx = seedFi };
            var queue = new Queue<KeyValuePair<int, int>>();
            queue.Enqueue(new KeyValuePair<int, int>(0, 0));

            // 안전 범위 — 너무 멀리 BFS 가 가지 않도록 lattice index 한계
            int maxLatticeRadius = (int)Math.Ceiling(bboxDiag / Math.Min(info.PitchU, info.PitchV)) + 4;

            // (Multi-seed 제거됨: face center 또는 expected lattice snap 둘 다 grid 일관성 깨뜨림.
            //  대신 direct projection 단계에서 충분한 snap 거리 + 적절한 dedup 으로 빈공간 채움)

            while (queue.Count > 0)
            {
                var key = queue.Dequeue();
                int ki = key.Key, kj = key.Value;
                if (Math.Abs(ki) > maxLatticeRadius || Math.Abs(kj) > maxLatticeRadius) continue;
                var current = placed[Encode(ki, kj)];

                // 4 directions (Ti_world / Tj_world 자체는 사용 안 함, 로컬 frame 으로 walk)
                int[] dis = { +1, -1, 0, 0 };
                int[] djs = { 0, 0, +1, -1 };
                double[] dists = { info.PitchU, info.PitchU, info.PitchV, info.PitchV };

                // Local frame at current cell: Ti_local = Ti_world projected, Tj_local = N × Ti_local
                // (Tj_local 이 항상 Ti_local 에 수직 → cylinder 같은 developable surface 에서 perfect grid)
                var curFace = brep.Faces[current.FaceIdx];
                double curU, curV;
                if (!((Surface)curFace).ClosestPoint(current.Pt, out curU, out curV)) continue;
                Point3d curDummy; Vector3d curDu, curDv;
                if (!EvalDeriv(curFace, curU, curV, out curDummy, out curDu, out curDv)) continue;
                Vector3d curN = Vector3d.CrossProduct(curDu, curDv);
                if (curN.Length < 1e-9) continue;
                curN.Unitize();
                Vector3d Ti_localCur = Ti_world - (Ti_world * curN) * curN;
                if (Ti_localCur.Length < 1e-6) continue;
                Ti_localCur.Unitize();
                Vector3d Tj_localCur = Vector3d.CrossProduct(curN, Ti_localCur);

                // 4 directions: ±Ti_local, ±Tj_local
                Vector3d[] localDirs = { Ti_localCur, -Ti_localCur, Tj_localCur, -Tj_localCur };

                for (int dirIdx = 0; dirIdx < 4; dirIdx++)
                {
                    int nki = ki + dis[dirIdx], nkj = kj + djs[dirIdx];
                    long nkey = Encode(nki, nkj);
                    if (placed.ContainsKey(nkey)) continue;
                    if (Math.Abs(nki) > maxLatticeRadius || Math.Abs(nkj) > maxLatticeRadius) continue;

                    Vector3d tangentDir = localDirs[dirIdx];
                    Point3d targetPt = current.Pt + dists[dirIdx] * tangentDir;
                    // Snap to surface (선택 면 밖이면 fail → cell 없음 → boundary 처리)
                    Point3d nextPt; int nextFi;
                    if (!TrySnapToSelectedFacesWithIndex(brep, faceSet, targetPt, dists[dirIdx] * 3.0, out nextPt, out nextFi))
                        continue;

                    // A. Snap-move 검증 (대폭 완화: 1.0×dist 까지 — corner/curvature transition 도달)
                    double snapMove = nextPt.DistanceTo(targetPt);
                    if (snapMove > dists[dirIdx] * 1.0) continue;

                    // B. Walked-distance sanity (대폭 완화: 0.3× ~ 2.0×)
                    double actualWalk = nextPt.DistanceTo(current.Pt);
                    if (actualWalk < dists[dirIdx] * 0.3 || actualWalk > dists[dirIdx] * 2.0) continue;

                    // C. World-space dedup: 기존 cell 중 0.7 × Pitch 안에 있는 것 있으면 skip
                    double dedupDist = Math.Min(info.PitchU, info.PitchV) * 0.7;
                    bool isDup = false;
                    foreach (var existing in placed.Values)
                    {
                        if (existing.Pt.DistanceTo(nextPt) < dedupDist) { isDup = true; break; }
                    }
                    if (isDup) continue;

                    placed[nkey] = new CellPos { Pt = nextPt, FaceIdx = nextFi };
                    queue.Enqueue(new KeyValuePair<int, int>(nki, nkj));
                }
            }

            // === Post-BFS gap-fill: BFS 가 못 도달한 곳을 인접 placed cell 기반으로 채움 ===
            // 여러 패스 수행해서 BFS 가 닿지 못한 corner 와 transition 영역 채움
            bool gapFillProgressed = true;
            int gapFillIter = 0;
            while (gapFillProgressed && gapFillIter < 8)
            {
                gapFillProgressed = false;
                gapFillIter++;
                var snapshotKeys = new List<KeyValuePair<long, CellPos>>(placed);
                foreach (var kvp in snapshotKeys)
                {
                    long key = kvp.Key;
                    int ki = (int)((key >> 20) - 10000);
                    int kj = (int)((key & 0xFFFFF) - 10000);
                    var current = kvp.Value;

                    // 현재 cell 의 local frame
                    var face = brep.Faces[current.FaceIdx];
                    double u, v;
                    if (!((Surface)face).ClosestPoint(current.Pt, out u, out v)) continue;
                    Point3d dummyPtL; Vector3d duL, dvL;
                    if (!EvalDeriv(face, u, v, out dummyPtL, out duL, out dvL)) continue;
                    Vector3d nL = Vector3d.CrossProduct(duL, dvL);
                    if (nL.Length < 1e-9) continue;
                    nL.Unitize();
                    Vector3d Ti_loc = Ti_world - (Ti_world * nL) * nL;
                    if (Ti_loc.Length < 1e-6) continue;
                    Ti_loc.Unitize();
                    Vector3d Tj_loc = Vector3d.CrossProduct(nL, Ti_loc);

                    int[] disG = { +1, -1, 0, 0 };
                    int[] djsG = { 0, 0, +1, -1 };
                    Vector3d[] dirsG = { Ti_loc, -Ti_loc, Tj_loc, -Tj_loc };
                    double[] distsG = { info.PitchU, info.PitchU, info.PitchV, info.PitchV };

                    for (int d = 0; d < 4; d++)
                    {
                        int nki = ki + disG[d], nkj = kj + djsG[d];
                        long nkey = (((long)(nki + 10000)) << 20) | (long)(nkj + 10000);
                        if (placed.ContainsKey(nkey)) continue;
                        if (Math.Abs(nki) > maxLatticeRadius || Math.Abs(nkj) > maxLatticeRadius) continue;

                        Point3d targetG = current.Pt + distsG[d] * dirsG[d];
                        Point3d nextG; int nextFiG;
                        // 더욱 관대한 snap (post-pass)
                        if (!TrySnapToSelectedFacesWithIndex(brep, faceSet, targetG, distsG[d] * 4.0, out nextG, out nextFiG)) continue;
                        double smG = nextG.DistanceTo(targetG);
                        if (smG > distsG[d] * 1.2) continue;
                        double awG = nextG.DistanceTo(current.Pt);
                        if (awG < distsG[d] * 0.3 || awG > distsG[d] * 2.2) continue;
                        double dedupG = Math.Min(info.PitchU, info.PitchV) * 0.7;
                        bool dupG = false;
                        foreach (var ex in placed.Values)
                        {
                            if (ex.Pt.DistanceTo(nextG) < dedupG) { dupG = true; break; }
                        }
                        if (dupG) continue;
                        placed[nkey] = new CellPos { Pt = nextG, FaceIdx = nextFiG };
                        gapFillProgressed = true;
                    }
                }
            }

            // === Direct flat-lattice projection 안전망 ===
            // BFS walking 이 도달 못한 lattice 위치도 flat lattice projection 으로 강제 시도.
            // Lattice bbox 전체 iterate → 빈 위치마다 직접 surface snap.
            // 이게 corner 영역에서 BFS 가 막힌 곳을 채워줌.
            double iMinDirect = double.MaxValue, iMaxDirect = double.MinValue;
            double jMinDirect = double.MaxValue, jMaxDirect = double.MinValue;
            foreach (var corner in sbb.GetCorners())
            {
                Vector3d vc = corner - seedSurf;
                double iv = (vc * Ti_world) / info.PitchU;
                double jv = (vc * Tj_world) / info.PitchV;
                if (iv < iMinDirect) iMinDirect = iv;
                if (iv > iMaxDirect) iMaxDirect = iv;
                if (jv < jMinDirect) jMinDirect = jv;
                if (jv > jMaxDirect) jMaxDirect = jv;
            }
            // Lattice 범위 확장 — 끝 corner 까지 확실히 시도
            int iStartD = (int)Math.Floor(iMinDirect) - 5;
            int iEndD = (int)Math.Ceiling(iMaxDirect) + 5;
            int jStartD = (int)Math.Floor(jMinDirect) - 5;
            int jEndD = (int)Math.Ceiling(jMaxDirect) + 5;

            // Direct snap max: bbox 전체까지 (가장 멀리 휜 surface 도 도달)
            double directSnapMax = Math.Max(Math.Max(info.PitchU, info.PitchV) * 10.0, bboxDiag);
            double directDedup = Math.Min(info.PitchU, info.PitchV) * 0.5;

            for (int kiD = iStartD; kiD <= iEndD; kiD++)
            {
                for (int kjD = jStartD; kjD <= jEndD; kjD++)
                {
                    long keyD = (((long)(kiD + 10000)) << 20) | (long)(kjD + 10000);
                    if (placed.ContainsKey(keyD)) continue;

                    Point3d flatPos = seedSurf + kiD * info.PitchU * Ti_world + kjD * info.PitchV * Tj_world;
                    Point3d snappedD; int fiD;
                    if (!TrySnapToSelectedFacesWithIndex(brep, faceSet, flatPos, directSnapMax, out snappedD, out fiD)) continue;

                    // World-space dedup
                    bool dupD = false;
                    foreach (var ex in placed.Values)
                    {
                        if (ex.Pt.DistanceTo(snappedD) < directDedup) { dupD = true; break; }
                    }
                    if (dupD) continue;

                    placed[keyD] = new CellPos { Pt = snappedD, FaceIdx = fiD };
                }
            }

            // === Hex 배치 (tangent plane shape, no scale) ===
            double snapMaxVertex = Math.Max(info.PitchU, info.PitchV) * 2.0;

            foreach (var kvp in placed)
            {
                Point3d cellCenter3d = kvp.Value.Pt;
                int snapFi = kvp.Value.FaceIdx;
                var snapFace = brep.Faces[snapFi];

                double localU, localV;
                if (!((Surface)snapFace).ClosestPoint(cellCenter3d, out localU, out localV)) continue;
                Point3d dummyPt; Vector3d duVec, dvVec;
                if (!EvalDeriv(snapFace, localU, localV, out dummyPt, out duVec, out dvVec)) continue;
                Vector3d N = Vector3d.CrossProduct(duVec, dvVec);
                if (N.Length < 1e-9) continue;
                N.Unitize();

                // Ti_local = Ti_world projected onto local tangent plane (consistent orientation)
                var Ti_local = Ti_world - (Ti_world * N) * N;
                if (Ti_local.Length < 1e-6) continue;
                Ti_local.Unitize();
                var Tj_local = Vector3d.CrossProduct(N, Ti_local);

                // Vertex 가 선택 면의 UV TRIM 안에 있어야만 통과 (boundary 너머 cell 정확히 reject)
                // 거리 검사가 아닌 trim 검사 → boundary 부근 cell 도 깨끗히 제거
                double vertexSnapMax = Math.Min(info.PitchU, info.PitchV) * 1.5;

                foreach (var pts in cellPts)
                {
                    var mapped = new Point3d[pts.Length];
                    bool allInsideTrim = true;
                    for (int k = 0; k < pts.Length; k++)
                    {
                        double dx = pts[k].X - ucX;
                        double dy = pts[k].Y - ucY;
                        double dxR = dx * cosR - dy * sinR;
                        double dyR = dx * sinR + dy * cosR;
                        Point3d flat = cellCenter3d + dxR * Ti_local + dyR * Tj_local;

                        // Vertex 가 어느 선택 면의 trim Interior 안에 있는지 검사
                        bool vertexFound = false;
                        double bestVertexDist = double.MaxValue;
                        Point3d bestVertexPt = flat;
                        foreach (int vfi in faceSet)
                        {
                            var vf = brep.Faces[vfi];
                            double vU, vV;
                            if (!((Surface)vf).ClosestPoint(flat, out vU, out vV)) continue;
                            var rel = vf.IsPointOnFace(vU, vV);
                            // INTERIOR 만 인정 (BOUNDARY 도 거부 → 사용자가 원하는 "boundary 닿는 cell 제거")
                            if (rel != PointFaceRelation.Interior) continue;
                            var vp = ((Surface)vf).PointAt(vU, vV);
                            double d = vp.DistanceTo(flat);
                            if (d < bestVertexDist && d < vertexSnapMax)
                            {
                                bestVertexDist = d;
                                bestVertexPt = vp;
                                vertexFound = true;
                            }
                        }
                        if (!vertexFound) { allInsideTrim = false; break; }
                        mapped[k] = bestVertexPt;
                    }
                    if (!allInsideTrim) continue; // 어느 vertex 라도 선택 면 trim Interior 밖 → cell 전체 reject
                    var crv = new PolylineCurve(mapped);
                    if (crv.IsValid) result.Add(crv);
                }
            }
            return result;
        }

        private struct CellPos { public Point3d Pt; public int FaceIdx; }

        /// <summary>
        /// "실제 크기" 다면 버전 — 이전 world-space 방식 (참조용 보존).
        /// </summary>
        private static List<Curve> TileConnectedRealSizeFit_StrategyTwo_Disabled(Brep brep, IList<int> faceIndices,
                                                            PatternInfo info, Vector3d refDir, double angleTolRad,
                                                            double rotationDeg = 0)
        {
            var result = new List<Curve>();
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return result;
            if (info == null || !info.Valid || info.UnitCells.Count == 0) return result;
            var faceSet = new HashSet<int>(faceIndices);

            // === Strategy 2 (참조용 보존): 평균 normal world-space flat lattice ===
            // 누적된 케이스 1~4 fix 모두 통합:
            //   - cell 위치 = world-space lattice → surface snap (phase drift 없음)
            //   - hex 모양 = 로컬 tangent plane (단단한 hex 모양 보존)
            //   - 평균 normal anchor (비-coplanar 영역도 좋은 평면 찾음)
            //   - 적응 snap 거리 (좁은/굽은 영역 도달)
            //   - face별 차등 dedup (same-face 5mm / diff-face 0.5mm)
            //   - primary-face vertex snap (cross-face 왜곡 방지)
            // Trade-off: 3면이 sharp 각도로 만나는 케이스에선 면 간 연속성 약간 어긋남 — 현 시점에서 알려진 한계
            Vector3d avgN = Vector3d.Zero;
            Vector3d avgDu = Vector3d.Zero;
            Vector3d sumCenter = Vector3d.Zero;
            int validCount = 0;
            foreach (int fi in faceIndices)
            {
                var face = brep.Faces[fi];
                double fuMin, fuMax, fvMin, fvMax;
                GetFaceUvBox(face, out fuMin, out fuMax, out fvMin, out fvMax);
                double fuc = 0.5 * (fuMin + fuMax);
                double fvc = 0.5 * (fvMin + fvMax);
                Point3d c; Vector3d du, dv;
                if (!EvalDeriv(face, fuc, fvc, out c, out du, out dv)) continue;
                double duL = du.Length, dvL = dv.Length;
                if (duL < 1e-9 || dvL < 1e-9) continue;
                var n = Vector3d.CrossProduct(du, dv);
                if (n.Length < 1e-9) continue;
                n.Unitize();
                avgN += n;
                avgDu += du / duL;
                sumCenter += (Vector3d)c;
                validCount++;
            }
            if (validCount == 0) return result;
            Point3d centroidPt = new Point3d(sumCenter / validCount);
            if (avgN.Length < 1e-6) return result;
            avgN.Unitize();

            var Ti_init = avgDu - (avgDu * avgN) * avgN;
            if (Ti_init.Length < 1e-6)
            {
                if (Math.Abs(avgN.Z) < 0.9) Ti_init = Vector3d.CrossProduct(avgN, Vector3d.ZAxis);
                else Ti_init = Vector3d.CrossProduct(avgN, Vector3d.XAxis);
            }
            Ti_init.Unitize();
            var Tj_init = Vector3d.CrossProduct(avgN, Ti_init);
            Tj_init.Unitize();

            // refDir 정렬 + 결정적 fallback
            bool aligned = false;
            if (refDir.Length > 1e-9)
            {
                var refOnPlane = refDir - (refDir * avgN) * avgN;
                if (refOnPlane.Length > 1e-6)
                {
                    refOnPlane.Unitize();
                    double cosA = Ti_init * refOnPlane;
                    double sinA = Vector3d.CrossProduct(Ti_init, refOnPlane) * avgN;
                    var newTi = cosA * Ti_init + sinA * Tj_init;
                    var newTj = -sinA * Ti_init + cosA * Tj_init;
                    Ti_init = newTi; Tj_init = newTj;
                    aligned = true;
                }
            }
            // 결정적 fallback: refDir 이 avgN 과 평행이면 World 축으로 정렬 (사용자가 "약간 틀어졌다" 느끼지 않도록)
            if (!aligned)
            {
                Vector3d[] worldCandidates = { Vector3d.YAxis, Vector3d.XAxis, Vector3d.ZAxis };
                foreach (var axis in worldCandidates)
                {
                    var proj = axis - (axis * avgN) * avgN;
                    if (proj.Length > 1e-6)
                    {
                        proj.Unitize();
                        Ti_init = proj;
                        Tj_init = Vector3d.CrossProduct(avgN, Ti_init);
                        Tj_init.Unitize();
                        break;
                    }
                }
            }

            double rotRad = rotationDeg * Math.PI / 180.0;
            double cosR = Math.Cos(rotRad), sinR = Math.Sin(rotRad);
            var Ti_world = cosR * Ti_init + sinR * Tj_init;
            var Tj_world = -sinR * Ti_init + cosR * Tj_init;

            Point3d seedCenter;
            int dummyFi;
            if (!TrySnapToSelectedFacesWithIndex(brep, faceSet, centroidPt, double.MaxValue, out seedCenter, out dummyFi))
            {
                var fallFace = brep.Faces[faceIndices[0]];
                double fuMin4, fuMax4, fvMin4, fvMax4;
                GetFaceUvBox(fallFace, out fuMin4, out fuMax4, out fvMin4, out fvMax4);
                seedCenter = ((Surface)fallFace).PointAt(0.5 * (fuMin4 + fuMax4), 0.5 * (fvMin4 + fvMax4));
            }

            BoundingBox sbb = BoundingBox.Empty;
            foreach (int fi in faceIndices)
                sbb.Union(brep.Faces[fi].GetBoundingBox(true));
            double iMinG = double.MaxValue, iMaxG = double.MinValue;
            double jMinG = double.MaxValue, jMaxG = double.MinValue;
            foreach (var corner in sbb.GetCorners())
            {
                Vector3d vc = corner - seedCenter;
                double iv = (vc * Ti_world) / info.PitchU;
                double jv = (vc * Tj_world) / info.PitchV;
                if (iv < iMinG) iMinG = iv;
                if (iv > iMaxG) iMaxG = iv;
                if (jv < jMinG) jMinG = jv;
                if (jv > jMaxG) jMaxG = jv;
            }
            int iStart = (int)Math.Floor(iMinG) - 1;
            int iEnd = (int)Math.Ceiling(iMaxG) + 1;
            int jStart = (int)Math.Floor(jMinG) - 1;
            int jEnd = (int)Math.Ceiling(jMaxG) + 1;

            double chord = Math.Max(info.CellW, info.CellH) / 20.0;
            var cellPts = new List<Point3d[]>();
            foreach (var c in info.UnitCells) cellPts.Add(SampleCurve(c, chord));
            var unitBBox = BoundingBox.Empty;
            foreach (var c in info.UnitCells) unitBBox.Union(c.GetBoundingBox(true));
            double ucX = unitBBox.Center.X, ucY = unitBBox.Center.Y;

            double bboxDiag = sbb.Diagonal.Length;
            // 더 넉넉한 snap 거리: bbox 의 절반까지 허용 (곡면 가장자리에서 cell 누락 방지)
            double snapMaxCenter = Math.Max(Math.Max(info.PitchU, info.PitchV) * 5.0, bboxDiag * 0.5);
            double snapMaxVertex = Math.Max(info.PitchU, info.PitchV) * 3.0;

            var placedCells = new List<KeyValuePair<int, Point3d>>();
            // Same-face dedup = 0.6 × min(PitchU, PitchV)
            //   - 평면 영역: 인접 cell 들 ≥ Pitch 간격 → dedup 안 걸림
            //   - 곡면 영역: cell 들이 곡률로 압축돼서 너무 가까우면 솎아냄
            //   - 0.7 보다 살짝 낮춰 (0.6) 가장자리 cell 들이 더 잘 살아남도록
            double dedupSameFace = Math.Min(info.PitchU, info.PitchV) * 0.6;
            double dedupDiffFace = 0.5;

            for (int ki = iStart; ki <= iEnd; ki++)
            {
                for (int kj = jStart; kj <= jEnd; kj++)
                {
                    Point3d latticePt = seedCenter + ki * info.PitchU * Ti_world + kj * info.PitchV * Tj_world;

                    Point3d cellCenter3d;
                    int snapFi;
                    if (!TrySnapToSelectedFacesWithIndex(brep, faceSet, latticePt, snapMaxCenter, out cellCenter3d, out snapFi)) continue;

                    bool isDuplicate = false;
                    foreach (var p in placedCells)
                    {
                        double thresh = (p.Key == snapFi) ? dedupSameFace : dedupDiffFace;
                        if (p.Value.DistanceTo(cellCenter3d) < thresh) { isDuplicate = true; break; }
                    }
                    if (isDuplicate) continue;

                    var snapFace = brep.Faces[snapFi];
                    double localU, localV;
                    if (!((Surface)snapFace).ClosestPoint(cellCenter3d, out localU, out localV)) continue;
                    Point3d dummyPt; Vector3d duVec, dvVec;
                    if (!EvalDeriv(snapFace, localU, localV, out dummyPt, out duVec, out dvVec)) continue;
                    Vector3d N = Vector3d.CrossProduct(duVec, dvVec);
                    if (N.Length < 1e-9) continue;
                    N.Unitize();

                    var Ti_local = Ti_world - (Ti_world * N) * N;
                    if (Ti_local.Length < 1e-6) continue;
                    Ti_local.Unitize();
                    var Tj_local = Vector3d.CrossProduct(N, Ti_local);

                    // Scale 제거 → hex 항상 자연 크기 (균일성 최우선)
                    // 곡면에서 cells 가 overlap 되는 부분은 dedup 으로 적절히 솎아냄
                    double scaleU = 1.0;
                    double scaleV = 1.0;

                    placedCells.Add(new KeyValuePair<int, Point3d>(snapFi, cellCenter3d));

                    foreach (var pts in cellPts)
                    {
                        var mapped = new Point3d[pts.Length];
                        for (int k = 0; k < pts.Length; k++)
                        {
                            double dx = pts[k].X - ucX;
                            double dy = pts[k].Y - ucY;
                            Point3d flat = cellCenter3d + (dx * scaleU) * Ti_local + (dy * scaleV) * Tj_local;
                            // Vertex: primary face 우선 snap (cross-face 왜곡 방지)
                            double vU, vV;
                            bool placed = false;
                            if (((Surface)snapFace).ClosestPoint(flat, out vU, out vV))
                            {
                                var vSnap = ((Surface)snapFace).PointAt(vU, vV);
                                if (snapFace.IsPointOnFace(vU, vV) != PointFaceRelation.Exterior &&
                                    vSnap.DistanceTo(flat) < snapMaxVertex)
                                {
                                    mapped[k] = vSnap;
                                    placed = true;
                                }
                            }
                            if (!placed)
                            {
                                Point3d snapped;
                                double tightSnap = Math.Max(info.PitchU, info.PitchV) * 0.8;
                                if (TrySnapToSelectedFaces(brep, faceSet, flat, tightSnap, out snapped))
                                    mapped[k] = snapped;
                                else
                                    mapped[k] = flat;
                            }
                        }
                        var crv = new PolylineCurve(mapped);
                        if (crv.IsValid) result.Add(crv);
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
                                                       double scale = 1.0,
                                                       Point3d? patternCenterOverride = null)
        {
            // === PartialFit: 단일 stamp 방식 ===
            // pattern center 를 surface 에 snap → 그 점의 tangent plane 에 pattern 평면 배치
            // UV trim Interior boundary check → 선택 면 밖 cell 자동 제거
            // RealSize 와 동일한 lattice anchor + tangent plane 알고리즘
            var result = new List<Curve>();
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return result;
            if (patternCurves == null || patternCurves.Count == 0) return result;
            var info = PatternAnalyzer.Analyze(patternCurves);
            if (!info.Valid) return result;
            var faceSet = new HashSet<int>(faceIndices);

            // === Lattice anchor + 방향 (RealSize 와 동일) ===
            Vector3d avgN = Vector3d.Zero;
            Vector3d sumCenter = Vector3d.Zero;
            int validCount = 0;
            foreach (int fi in faceIndices)
            {
                var face = brep.Faces[fi];
                double fuMin, fuMax, fvMin, fvMax;
                GetFaceUvBox(face, out fuMin, out fuMax, out fvMin, out fvMax);
                double fuc = 0.5 * (fuMin + fuMax);
                double fvc = 0.5 * (fvMin + fvMax);
                Point3d c; Vector3d du, dv;
                if (!EvalDeriv(face, fuc, fvc, out c, out du, out dv)) continue;
                if (du.Length < 1e-9 || dv.Length < 1e-9) continue;
                var n = Vector3d.CrossProduct(du, dv);
                if (n.Length < 1e-9) continue;
                n.Unitize();
                avgN += n;
                sumCenter += (Vector3d)c;
                validCount++;
            }
            if (validCount == 0) return result;
            Point3d centroidPt = new Point3d(sumCenter / validCount);
            if (avgN.Length < 1e-6) return result;
            avgN.Unitize();
            // avgN 을 World 축으로 snap
            double absX = Math.Abs(avgN.X), absY = Math.Abs(avgN.Y), absZ = Math.Abs(avgN.Z);
            if (absZ > 0.9 && absZ >= absX && absZ >= absY) avgN = new Vector3d(0, 0, avgN.Z > 0 ? 1 : -1);
            else if (absY > 0.9 && absY >= absX && absY >= absZ) avgN = new Vector3d(0, avgN.Y > 0 ? 1 : -1, 0);
            else if (absX > 0.9 && absX >= absY && absX >= absZ) avgN = new Vector3d(avgN.X > 0 ? 1 : -1, 0, 0);

            // Ti, Tj 결정 (refDir → World 축 fallback) — 사용자 회전은 별도 처리
            Vector3d Ti_init = Vector3d.Zero;
            if (refDir.Length > 1e-9)
            {
                var refOnPlane = refDir - (refDir * avgN) * avgN;
                if (refOnPlane.Length > 1e-6) { refOnPlane.Unitize(); Ti_init = refOnPlane; }
            }
            if (Ti_init.Length < 1e-6)
            {
                Vector3d[] axes = { Vector3d.YAxis, Vector3d.XAxis, Vector3d.ZAxis };
                foreach (var axis in axes)
                {
                    var proj = axis - (axis * avgN) * avgN;
                    if (proj.Length > 1e-6) { proj.Unitize(); Ti_init = proj; break; }
                }
            }
            if (Ti_init.Length < 1e-6) return result;
            var Tj_init = Vector3d.CrossProduct(avgN, Ti_init);
            Tj_init.Unitize();

            // seed = centroid → surface
            Point3d seedSurf; int seedFi;
            BoundingBox sbb = BoundingBox.Empty;
            foreach (int fi in faceIndices) sbb.Union(brep.Faces[fi].GetBoundingBox(true));
            double bboxDiag = sbb.Diagonal.Length;
            if (!TrySnapToSelectedFacesWithIndex(brep, faceSet, centroidPt, bboxDiag, out seedSurf, out seedFi))
                return result;

            // === Pattern center 위치 ===
            // override 가 있으면 그대로 사용 (인터랙티브 모드 — cursor 위치 정확 반영)
            // 없으면 기존 방식: seedSurf 에서 (uOffset, vOffset) 이동 후 snap
            Point3d patternCenter; int patternFi;
            if (patternCenterOverride.HasValue)
            {
                patternCenter = patternCenterOverride.Value;
                // 어느 선택 면에 있는지 찾기
                patternFi = -1;
                double bestPcD = double.MaxValue;
                foreach (int fi in faceSet)
                {
                    var f2 = brep.Faces[fi];
                    double u2, v2;
                    if (!((Surface)f2).ClosestPoint(patternCenter, out u2, out v2)) continue;
                    var pt2 = ((Surface)f2).PointAt(u2, v2);
                    if (f2.IsPointOnFace(u2, v2) == PointFaceRelation.Exterior) continue;
                    double d2 = pt2.DistanceTo(patternCenter);
                    if (d2 < bestPcD) { bestPcD = d2; patternFi = fi; }
                }
                if (patternFi < 0) return result;
            }
            else
            {
                Point3d flatPatternCenter = seedSurf + uOffsetMm * Ti_init + vOffsetMm * Tj_init;
                if (!TrySnapToSelectedFacesWithIndex(brep, faceSet, flatPatternCenter, bboxDiag, out patternCenter, out patternFi))
                    return result;
            }

            // === Pattern center 의 local tangent plane ===
            var patternFace = brep.Faces[patternFi];
            double pcU, pcV;
            if (!((Surface)patternFace).ClosestPoint(patternCenter, out pcU, out pcV)) return result;
            Point3d dummyPt2; Vector3d duVec2, dvVec2;
            if (!EvalDeriv(patternFace, pcU, pcV, out dummyPt2, out duVec2, out dvVec2)) return result;
            Vector3d N = Vector3d.CrossProduct(duVec2, dvVec2);
            if (N.Length < 1e-9) return result;
            N.Unitize();
            Vector3d Ti_local = Ti_init - (Ti_init * N) * N;
            if (Ti_local.Length < 1e-6) return result;
            Ti_local.Unitize();
            Vector3d Tj_local = Vector3d.CrossProduct(N, Ti_local);

            // === 각 pattern curve 배치 ===
            double rotRad = rotationDeg * Math.PI / 180.0;
            double cosR = Math.Cos(rotRad), sinR = Math.Sin(rotRad);
            double pCx = 0.5 * (patternBox.Min.X + patternBox.Max.X);
            double pCy = 0.5 * (patternBox.Min.Y + patternBox.Max.Y);
            double chord = Math.Max(info.CellW, info.CellH) / 20.0;
            double vertexSnapMax = Math.Max(info.PitchU, info.PitchV) * 1.5;

            foreach (var c in patternCurves)
            {
                var pts = SampleCurve(c, chord);
                var mapped = new Point3d[pts.Length];
                bool allInsideTrim = true;

                for (int k = 0; k < pts.Length; k++)
                {
                    // 패턴 공간 vertex offset (pattern center 기준), scale 적용
                    double offX = (pts[k].X - pCx) * scale;
                    double offY = (pts[k].Y - pCy) * scale;
                    // 사용자 회전 적용
                    double offRX = offX * cosR - offY * sinR;
                    double offRY = offX * sinR + offY * cosR;
                    // tangent plane 위치
                    Point3d flat = patternCenter + offRX * Ti_local + offRY * Tj_local;

                    // UV trim Interior 검사 (boundary 깨끗히 처리)
                    bool vertexFound = false;
                    double bestVertexDist = double.MaxValue;
                    Point3d bestVertexPt = flat;
                    foreach (int vfi in faceSet)
                    {
                        var vf = brep.Faces[vfi];
                        double vU, vV;
                        if (!((Surface)vf).ClosestPoint(flat, out vU, out vV)) continue;
                        var rel = vf.IsPointOnFace(vU, vV);
                        if (rel != PointFaceRelation.Interior) continue;
                        var vp = ((Surface)vf).PointAt(vU, vV);
                        double d = vp.DistanceTo(flat);
                        if (d < bestVertexDist && d < vertexSnapMax)
                        {
                            bestVertexDist = d;
                            bestVertexPt = vp;
                            vertexFound = true;
                        }
                    }
                    if (!vertexFound) { allInsideTrim = false; break; }
                    mapped[k] = bestVertexPt;
                }
                if (!allInsideTrim) continue;
                var crv = new PolylineCurve(mapped);
                if (crv.IsValid) result.Add(crv);
            }
            return result;
        }

        /// <summary>로컬 arc-length → world 변환비. lattice 작은 offset 의 world 거리를 측정해서 환산.</summary>
        private static double ArcToWorldScale(BrepFace face, FacePhase phase, double vi_c, double vj_c,
                                              double dvi, double dvj, PatternInfo info, Point3d center3d, double pitch)
        {
            double testU, testV;
            if (!LatticeToFaceUV(phase, vi_c + dvi, vj_c + dvj, info, out testU, out testV)) return 1.0;
            // trim 밖이어도 surface extrapolation 의 거리는 의미있으므로 그대로 사용
            var testPt = ((Surface)face).PointAt(testU, testV);
            double worldD = testPt.DistanceTo(center3d);
            double arcD = Math.Sqrt(dvi * dvi + dvj * dvj) * pitch;
            if (arcD < 1e-9) return 1.0;
            double s = worldD / arcD;
            // 안전 범위 (극단적 좌표 변환 방지)
            if (s < 0.1) s = 0.1;
            if (s > 2.0) s = 2.0;
            return s;
        }

        /// <summary>대상 면집합 중 worldPt 와 가장 가까운, trim 내부인 점을 찾고 face index 도 반환.</summary>
        private static bool TrySnapToSelectedFacesWithIndex(Brep brep, HashSet<int> faceSet, Point3d worldPt, double maxDist, out Point3d snapped, out int faceIdx)
        {
            snapped = Point3d.Origin;
            faceIdx = -1;
            double minDist = double.MaxValue;
            foreach (int fi in faceSet)
            {
                var face = brep.Faces[fi];
                double u, v;
                if (!((Surface)face).ClosestPoint(worldPt, out u, out v)) continue;
                var pt = ((Surface)face).PointAt(u, v);
                if (face.IsPointOnFace(u, v) == PointFaceRelation.Exterior) continue;
                double d = pt.DistanceTo(worldPt);
                if (d < minDist) { minDist = d; snapped = pt; faceIdx = fi; }
            }
            return faceIdx >= 0 && minDist <= maxDist;
        }

        /// <summary>대상 면집합 중 worldPt 와 가장 가까운, trim 내부인 점을 찾아 스냅. maxDist 안일 때만 성공.</summary>
        private static bool TrySnapToSelectedFaces(Brep brep, HashSet<int> faceSet, Point3d worldPt, double maxDist, out Point3d snapped)
        {
            snapped = Point3d.Origin;
            double minDist = double.MaxValue;
            bool anyHit = false;
            foreach (int fi in faceSet)
            {
                var face = brep.Faces[fi];
                double u, v;
                if (!((Surface)face).ClosestPoint(worldPt, out u, out v)) continue;
                var pt = ((Surface)face).PointAt(u, v);
                // trim 내부만 인정
                if (face.IsPointOnFace(u, v) == PointFaceRelation.Exterior) continue;
                double d = pt.DistanceTo(worldPt);
                if (d < minDist)
                {
                    minDist = d;
                    snapped = pt;
                    anyHit = true;
                }
            }
            return anyHit && minDist <= maxDist;
        }

        /// <summary>face fi 와 이미 phase 가 있는 임의의 이웃 face index 를 반환. 없으면 -1.</summary>
        private static int FindAnyPhasedNeighbor(Brep brep, int fi, Dictionary<int, FacePhase> phases)
        {
            foreach (int ei in brep.Faces[fi].AdjacentEdges())
            {
                foreach (int nfi in brep.Edges[ei].AdjacentFaces())
                {
                    if (nfi == fi) continue;
                    if (phases.ContainsKey(nfi)) return nfi;
                }
            }
            return -1;
        }

        /// <summary>MakeChildPhase 의 smoothness 요구 없는 버전. 임의 shared edge 로 phase 만듦.</summary>
        private static FacePhase MakeChildPhaseLoose(BrepFace face, BrepFace fromFace, FacePhase fromPhase,
                                                     Brep brep, int fi, int fromFi,
                                                     PatternInfo info, Vector3d refDir)
        {
            int sharedEdgeIdx = -1;
            foreach (int ei in brep.Faces[fi].AdjacentEdges())
            {
                foreach (int nfi in brep.Edges[ei].AdjacentFaces())
                {
                    if (nfi == fromFi) { sharedEdgeIdx = ei; break; }
                }
                if (sharedEdgeIdx >= 0) break;
            }
            if (sharedEdgeIdx < 0) return null;
            var edge = brep.Edges[sharedEdgeIdx];
            var Pe = edge.PointAtNormalizedLength(0.5);

            double uPeF, vPeF;
            if (!((Surface)fromFace).ClosestPoint(Pe, out uPeF, out vPeF)) return null;
            double sFromU = ArcOffsetFromAnchor(fromPhase, true, uPeF) - fromPhase.UAnchorArc;
            double sFromV = ArcOffsetFromAnchor(fromPhase, false, vPeF) - fromPhase.VAnchorArc;
            double iLoc = (sFromU * fromPhase.CosA + sFromV * fromPhase.SinA) / info.PitchU;
            double jLoc = (-sFromU * fromPhase.SinA + sFromV * fromPhase.CosA) / info.PitchV;
            double iAt = fromPhase.IOffset + iLoc;
            double jAt = fromPhase.JOffset + jLoc;

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

        /// <summary>
        /// "한 장 늘려 맞춤" (Stretch) — nU × nV 반복으로 패턴을 영역에 stretch.
        /// RealSize / PartialFit 과 동일한 world-space lattice + tangent plane 알고리즘.
        /// 각 타일 중심을 surface 에 snap → 그 점의 tangent plane 에 패턴 stamp.
        /// UV trim Interior boundary 검사로 영역 boundary 깔끔.
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
            var faceSet = new HashSet<int>(faceIndices);

            // === Lattice anchor (RealSize 와 동일) ===
            Vector3d avgN = Vector3d.Zero;
            Vector3d sumCenter = Vector3d.Zero;
            int validCount = 0;
            foreach (int fi in faceIndices)
            {
                var face = brep.Faces[fi];
                double fuMin, fuMax, fvMin, fvMax;
                GetFaceUvBox(face, out fuMin, out fuMax, out fvMin, out fvMax);
                double fuc = 0.5 * (fuMin + fuMax);
                double fvc = 0.5 * (fvMin + fvMax);
                Point3d c; Vector3d du, dv;
                if (!EvalDeriv(face, fuc, fvc, out c, out du, out dv)) continue;
                if (du.Length < 1e-9 || dv.Length < 1e-9) continue;
                var n = Vector3d.CrossProduct(du, dv);
                if (n.Length < 1e-9) continue;
                n.Unitize();
                avgN += n;
                sumCenter += (Vector3d)c;
                validCount++;
            }
            if (validCount == 0) return result;
            Point3d centroidPt = new Point3d(sumCenter / validCount);
            if (avgN.Length < 1e-6) return result;
            avgN.Unitize();
            double absXn = Math.Abs(avgN.X), absYn = Math.Abs(avgN.Y), absZn = Math.Abs(avgN.Z);
            if (absZn > 0.9 && absZn >= absXn && absZn >= absYn) avgN = new Vector3d(0, 0, avgN.Z > 0 ? 1 : -1);
            else if (absYn > 0.9 && absYn >= absXn && absYn >= absZn) avgN = new Vector3d(0, avgN.Y > 0 ? 1 : -1, 0);
            else if (absXn > 0.9 && absXn >= absYn && absXn >= absZn) avgN = new Vector3d(avgN.X > 0 ? 1 : -1, 0, 0);

            Vector3d Ti_init = Vector3d.Zero;
            if (refDir.Length > 1e-9)
            {
                var refOnPlane = refDir - (refDir * avgN) * avgN;
                if (refOnPlane.Length > 1e-6) { refOnPlane.Unitize(); Ti_init = refOnPlane; }
            }
            if (Ti_init.Length < 1e-6)
            {
                Vector3d[] axes = { Vector3d.YAxis, Vector3d.XAxis, Vector3d.ZAxis };
                foreach (var axis in axes)
                {
                    var proj = axis - (axis * avgN) * avgN;
                    if (proj.Length > 1e-6) { proj.Unitize(); Ti_init = proj; break; }
                }
            }
            if (Ti_init.Length < 1e-6) return result;
            var Tj_init = Vector3d.CrossProduct(avgN, Ti_init);
            Tj_init.Unitize();

            Point3d seedSurf; int seedFi;
            BoundingBox sbb = BoundingBox.Empty;
            foreach (int fi in faceIndices) sbb.Union(brep.Faces[fi].GetBoundingBox(true));
            double bboxDiag = sbb.Diagonal.Length;
            if (!TrySnapToSelectedFacesWithIndex(brep, faceSet, centroidPt, bboxDiag, out seedSurf, out seedFi))
                return result;

            // === 영역 bbox in lattice (mm units) ===
            double iMinMm = double.MaxValue, iMaxMm = double.MinValue;
            double jMinMm = double.MaxValue, jMaxMm = double.MinValue;
            foreach (var corner in sbb.GetCorners())
            {
                Vector3d vc = corner - seedSurf;
                double iv = vc * Ti_init; // mm
                double jv = vc * Tj_init; // mm
                if (iv < iMinMm) iMinMm = iv;
                if (iv > iMaxMm) iMaxMm = iv;
                if (jv < jMinMm) jMinMm = jv;
                if (jv > jMaxMm) jMaxMm = jv;
            }
            if (iMinMm >= iMaxMm || jMinMm >= jMaxMm) return result;

            // 마진 인셋
            if (margin > 1e-9)
            {
                iMinMm += margin; iMaxMm -= margin;
                jMinMm += margin; jMaxMm -= margin;
                if (iMinMm >= iMaxMm || jMinMm >= jMaxMm) return result;
            }

            // === Pattern 정보 ===
            double pw = patternBox.Max.X - patternBox.Min.X;
            double ph2 = patternBox.Max.Y - patternBox.Min.Y;
            if (pw < 1e-9 || ph2 < 1e-9) return result;
            double pCx = 0.5 * (patternBox.Min.X + patternBox.Max.X);
            double pCy = 0.5 * (patternBox.Min.Y + patternBox.Max.Y);
            double rotRad = rotationDeg * Math.PI / 180.0;
            double cosR = Math.Cos(rotRad), sinR = Math.Sin(rotRad);
            double absC = Math.Abs(cosR), absS = Math.Abs(sinR);
            // 회전 후 패턴 bbox (rot 0 이면 pw × ph2)
            double Wrot = pw * absC + ph2 * absS;
            double Hrot = pw * absS + ph2 * absC;

            nU = Math.Max(1, nU);
            nV = Math.Max(1, nV);
            double iSpanMm = iMaxMm - iMinMm;
            double jSpanMm = jMaxMm - jMinMm;

            // 패턴 인접 cell 간격 → 반복 사이 gap
            double gapX = nU > 1 ? EstimateGap(patternCurves, 0) : 0;
            double gapY = nV > 1 ? EstimateGap(patternCurves, 1) : 0;
            double tileWmm = (iSpanMm - (nU - 1) * gapX) / nU;
            double tileHmm = (jSpanMm - (nV - 1) * gapY) / nV;
            if (tileWmm < 1e-9 || tileHmm < 1e-9) return result;

            // 비균일 스케일 (패턴 → 타일)
            double scaleX = tileWmm / Wrot;
            double scaleY = tileHmm / Hrot;

            double chord = Math.Max(info.CellW, info.CellH) / 20.0;
            double vertexSnapMax = Math.Max(info.PitchU, info.PitchV) * 2.0;

            // === 각 타일 (ti, tj) ===
            for (int ti = 0; ti < nU; ti++)
            {
                for (int tj = 0; tj < nV; tj++)
                {
                    // 타일 중심 mm offset from seedSurf
                    double tileCxMm = iMinMm + ti * (tileWmm + gapX) + tileWmm * 0.5;
                    double tileCyMm = jMinMm + tj * (tileHmm + gapY) + tileHmm * 0.5;
                    Point3d flatTile = seedSurf + tileCxMm * Ti_init + tileCyMm * Tj_init;
                    Point3d tileCenter; int tileFi;
                    if (!TrySnapToSelectedFacesWithIndex(brep, faceSet, flatTile, bboxDiag, out tileCenter, out tileFi))
                        continue;

                    // 타일 중심의 tangent plane
                    var tileFace = brep.Faces[tileFi];
                    double tcU, tcV;
                    if (!((Surface)tileFace).ClosestPoint(tileCenter, out tcU, out tcV)) continue;
                    Point3d dumPt; Vector3d duT, dvT;
                    if (!EvalDeriv(tileFace, tcU, tcV, out dumPt, out duT, out dvT)) continue;
                    Vector3d N = Vector3d.CrossProduct(duT, dvT);
                    if (N.Length < 1e-9) continue;
                    N.Unitize();
                    Vector3d Ti_local = Ti_init - (Ti_init * N) * N;
                    if (Ti_local.Length < 1e-6) continue;
                    Ti_local.Unitize();
                    Vector3d Tj_local = Vector3d.CrossProduct(N, Ti_local);

                    // === 각 패턴 커브 ===
                    foreach (var c in patternCurves)
                    {
                        var pts = SampleCurve(c, chord);
                        var mapped = new Point3d[pts.Length];
                        bool allInsideTrim = true;
                        for (int k = 0; k < pts.Length; k++)
                        {
                            double vx = pts[k].X, vy = pts[k].Y;
                            if (flipH) vx = patternBox.Max.X + patternBox.Min.X - vx;
                            if (flipV) vy = patternBox.Max.Y + patternBox.Min.Y - vy;
                            double offX = vx - pCx;
                            double offY = vy - pCy;
                            // 사용자 회전
                            double offRX = offX * cosR - offY * sinR;
                            double offRY = offX * sinR + offY * cosR;
                            // 비균일 스케일 (영역 fit)
                            double offWX = offRX * scaleX;
                            double offWY = offRY * scaleY;
                            // tangent plane 위치
                            Point3d flat = tileCenter + offWX * Ti_local + offWY * Tj_local;

                            // UV trim 검사 — Stretch 는 영역 꽉 채우는 모드이므로 Boundary 도 허용
                            // (Exterior 만 reject — 실제 면 밖으로 나가는 cell 만 제거)
                            bool vertexFound = false;
                            double bestVertexDist = double.MaxValue;
                            Point3d bestVertexPt = flat;
                            foreach (int vfi in faceSet)
                            {
                                var vf = brep.Faces[vfi];
                                double vU, vV;
                                if (!((Surface)vf).ClosestPoint(flat, out vU, out vV)) continue;
                                var rel = vf.IsPointOnFace(vU, vV);
                                if (rel == PointFaceRelation.Exterior) continue;
                                var vp = ((Surface)vf).PointAt(vU, vV);
                                double d = vp.DistanceTo(flat);
                                if (d < bestVertexDist && d < vertexSnapMax)
                                {
                                    bestVertexDist = d;
                                    bestVertexPt = vp;
                                    vertexFound = true;
                                }
                            }
                            if (!vertexFound) { allInsideTrim = false; break; }
                            mapped[k] = bestVertexPt;
                        }
                        if (!allInsideTrim) continue;
                        var crv = new PolylineCurve(mapped);
                        if (crv.IsValid) result.Add(crv);
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
