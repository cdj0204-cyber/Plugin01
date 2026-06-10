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
        /// "실제 크기" 메인 (평행 투영 방식 — 균일 격자 유지):
        ///   - 패턴을 punch 방향(avgN, World 축 snap)에 수직인 평면에 "강체" 균일 격자로 정의.
        ///   - 각 셀의 모든 vertex 를 avgN 방향으로 표면에 평행 투영 → 위에서 본 간격·각도가 완벽 균일.
        ///   - 격자점 1개 → 표면 1점(1:1) → 셀 중복 없음.
        ///   - vertex 가 표면(선택 면 trim) 밖으로 투영되면 ray miss → 그 셀 제거 (외곽선/구멍 자동 클리핑).
        ///   - 투영은 UV 를 안 쓰므로 미러 면도 중심선 양쪽 동일.
        /// </summary>
        // boundaryMode: 0=경계 셀 삭제, 1=경계로 갈수록 축소(페이드), 2=경계에 맞춰 자르기. fadeRings=축소 링 수.
        // margin>0 이면 경계를 그만큼 안쪽으로 인셋한 "가상 경계"를 기준으로 삭제/축소/자르기 적용.
        public static List<Curve> TileConnectedRealSizeFit(Brep brep, IList<int> faceIndices,
                                                            PatternInfo info, Vector3d refDir, double angleTolRad,
                                                            double rotationDeg = 0,
                                                            int boundaryMode = 0, int fadeRings = 2, double margin = 0)
        {
            var result = new List<Curve>();
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return result;
            if (info == null || !info.Valid || info.UnitCells.Count == 0) return result;
            var faceSet = new HashSet<int>(faceIndices);

            // === 투영 방향(avgN) + 평면 격자 방향(Ti/Tj) ===
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
                if (face.OrientationIsReversed) n = -n; // 미러면 normal 복원
                avgN += n;
                sumCenter += (Vector3d)c;
                validCount++;
            }
            if (validCount == 0) return result;
            Point3d centroidPt = new Point3d(sumCenter / validCount);
            if (avgN.Length < 1e-6) return result;
            avgN.Unitize();

            // avgN 을 가까운 World 축으로 snap (tilt 제거 → 축 정렬 패널은 완전 수직 투영)
            double absX = Math.Abs(avgN.X), absY = Math.Abs(avgN.Y), absZ = Math.Abs(avgN.Z);
            if (absZ > 0.9 && absZ >= absX && absZ >= absY) avgN = new Vector3d(0, 0, avgN.Z > 0 ? 1 : -1);
            else if (absY > 0.9 && absY >= absX && absY >= absZ) avgN = new Vector3d(0, avgN.Y > 0 ? 1 : -1, 0);
            else if (absX > 0.9 && absX >= absY && absX >= absZ) avgN = new Vector3d(avgN.X > 0 ? 1 : -1, 0, 0);

            // 평면 내 격자 방향 Ti (refDir → World 축 fallback), Tj = avgN × Ti
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

            // 기본 0/90° 원칙: Ti 를 평면 내에서 가장 가까운 World 축(의 평면 투영)에 정렬.
            // (refDir = 면 du 의 미세한 tilt 제거 → 회전 슬라이더 0 일 때 정확히 수직/수평)
            {
                Vector3d bestAxis = Vector3d.Zero;
                double bestDot = -1;
                foreach (var axis in new[] { Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis })
                {
                    var proj = axis - (axis * avgN) * avgN; // 평면에 투영 (avgN 과 평행한 축은 0 이 됨)
                    if (proj.Length < 1e-6) continue;
                    proj.Unitize();
                    double dot = proj * Ti_init;
                    if (Math.Abs(dot) > bestDot)
                    {
                        bestDot = Math.Abs(dot);
                        bestAxis = (dot >= 0) ? proj : -proj; // 원래 Ti 방향(부호) 유지
                    }
                }
                if (bestAxis.Length > 1e-6) Ti_init = bestAxis;
            }

            var Tj_init = Vector3d.CrossProduct(avgN, Ti_init);
            Tj_init.Unitize();

            double rotRad = rotationDeg * Math.PI / 180.0;
            double cosR = Math.Cos(rotRad), sinR = Math.Sin(rotRad);
            var Ti_world = cosR * Ti_init + sinR * Tj_init;
            var Tj_world = -sinR * Ti_init + cosR * Tj_init;

            // === 격자 anchor: centroid 를 표면에 snap → 그 점을 지나는 평면에 격자 정의 ===
            Point3d seedSurf; int seedFi;
            BoundingBox sbb = BoundingBox.Empty;
            foreach (int fi in faceIndices) sbb.Union(brep.Faces[fi].GetBoundingBox(true));
            double bboxDiag = sbb.Diagonal.Length;
            if (!TrySnapToSelectedFacesWithIndex(brep, faceSet, centroidPt, bboxDiag, out seedSurf, out seedFi))
                seedSurf = centroidPt;
            Point3d planeOrigin = seedSurf; // avgN 성분은 투영으로 소거되므로 평면 위 임의 점이면 충분

            // === 선택 면 mesh (평행 투영 + 외곽선/구멍 클리핑용) ===
            var projMesh = new Mesh();
            Mesh[] faceMeshes = Mesh.CreateFromBrep(brep, MeshingParameters.Default);
            if (faceMeshes != null)
            {
                foreach (int fi in faceSet)
                    if (fi < faceMeshes.Length && faceMeshes[fi] != null) projMesh.Append(faceMeshes[fi]);
            }
            if (projMesh.Faces.Count == 0) return result;
            projMesh.Compact();
            double rayLen = Math.Max(bboxDiag * 2.0, 1.0);

            // === 패턴 단위셀 (평면에 강체로 stamp) ===
            double chord = Math.Max(info.CellW, info.CellH) / 20.0;
            var cellPts = new List<Point3d[]>();
            foreach (var c in info.UnitCells) cellPts.Add(SampleCurve(c, chord));
            var unitBBox = BoundingBox.Empty;
            foreach (var c in info.UnitCells) unitBBox.Union(c.GetBoundingBox(true));
            double ucX = unitBBox.Center.X, ucY = unitBBox.Center.Y;

            // === 격자 index 범위: 선택 면 bbox 를 평면(Ti,Tj)에 투영 ===
            double iMin = double.MaxValue, iMax = double.MinValue;
            double jMin = double.MaxValue, jMax = double.MinValue;
            foreach (var corner in sbb.GetCorners())
            {
                Vector3d vc = corner - planeOrigin;
                double iv = (vc * Ti_world) / info.PitchU;
                double jv = (vc * Tj_world) / info.PitchV;
                if (iv < iMin) iMin = iv;
                if (iv > iMax) iMax = iv;
                if (jv < jMin) jMin = jv;
                if (jv > jMax) jMax = jv;
            }
            int iStart = (int)Math.Floor(iMin) - 1, iEnd = (int)Math.Ceiling(iMax) + 1;
            int jStart = (int)Math.Floor(jMin) - 1, jEnd = (int)Math.Ceiling(jMax) + 1;

            // 경계 loop(2D) — 마진 인셋 또는 클립에 사용. 샘플은 셀 크기의 절반 정도(직선은 정확, 곡선은 충분히 매끄럽고 불리언 빠름).
            double bSample = Math.Max(0.1, Math.Max(info.CellW, info.CellH) * 0.5);
            List<Curve> bLoops = (margin > 1e-9 || boundaryMode == 2)
                ? BuildPlaneBoundaryLoops(brep, faceIndices, planeOrigin, Ti_world, Tj_world, bSample) : null;
            double clipTol = Math.Max(1e-4, Math.Min(info.CellW, info.CellH) * 0.01);
            long Key(int i, int j) => ((long)(i + 100000) << 21) | (long)(j + 100000);

            // === 모드 2(자르기): 빠른 mesh-ray 분류(ring) + 경계 띠만 불리언 클립 ===
            if (boundaryMode == 2)
            {
                var clipLoops = (margin > 1e-9 && bLoops != null && bLoops.Count > 0)
                    ? InsetLoops(bLoops, margin, clipTol) : bLoops;
                double unitW = unitBBox.Max.X - unitBBox.Min.X, unitH = unitBBox.Max.Y - unitBBox.Min.Y;
                double cellRad = 0.6 * Math.Sqrt(unitW * unitW + unitH * unitH);
                double pitchMin = Math.Max(1e-9, Math.Min(info.PitchU, info.PitchV));
                int bandRings = (int)Math.Ceiling((cellRad + Math.Max(0.0, margin)) / pitchMin) + 1;

                // inside(중심 적중) 집합 — ray 1회/셀 (빠른 분류)
                var insideC = new HashSet<long>();
                for (int ki = iStart; ki <= iEnd; ki++)
                    for (int kj = jStart; kj <= jEnd; kj++)
                    {
                        Point3d cc = planeOrigin + ki * info.PitchU * Ti_world + kj * info.PitchV * Tj_world;
                        Point3d hitc;
                        if (ProjectOntoMesh(projMesh, cc, avgN, rayLen, out hitc)) insideC.Add(Key(ki, kj));
                    }

                // 외곽으로부터 ring 인덱스 BFS (ring 1 = 비-inside 와 인접)
                var cring = new Dictionary<long, int>();
                var cq = new Queue<long>();
                foreach (var key in insideC)
                {
                    int ki = (int)((key >> 21) - 100000);
                    int kj = (int)((key & 0x1FFFFF) - 100000);
                    if (!insideC.Contains(Key(ki + 1, kj)) || !insideC.Contains(Key(ki - 1, kj)) ||
                        !insideC.Contains(Key(ki, kj + 1)) || !insideC.Contains(Key(ki, kj - 1)))
                    { cring[key] = 1; cq.Enqueue(key); }
                }
                int[] cddi = { 1, -1, 0, 0 }, cddj = { 0, 0, 1, -1 };
                while (cq.Count > 0)
                {
                    long key = cq.Dequeue();
                    int ki = (int)((key >> 21) - 100000);
                    int kj = (int)((key & 0x1FFFFF) - 100000);
                    int r = cring[key];
                    for (int dd = 0; dd < 4; dd++)
                    {
                        long nk = Key(ki + cddi[dd], kj + cddj[dd]);
                        if (insideC.Contains(nk) && !cring.ContainsKey(nk)) { cring[nk] = r + 1; cq.Enqueue(nk); }
                    }
                }

                // inside 셀: 깊은 내부(ring>band)=그대로, 띠(ring<=band)=클립
                foreach (var key in insideC)
                {
                    int ki = (int)((key >> 21) - 100000);
                    int kj = (int)((key & 0x1FFFFF) - 100000);
                    Point3d cc = planeOrigin + ki * info.PitchU * Ti_world + kj * info.PitchV * Tj_world;
                    bool deep = (cring.ContainsKey(key) ? cring[key] : bandRings + 1) > bandRings;
                    foreach (var pts in cellPts)
                    {
                        if (deep)
                        {
                            var mapped = new Point3d[pts.Length];
                            bool ok = true;
                            for (int k = 0; k < pts.Length; k++)
                            {
                                double dx = pts[k].X - ucX, dy = pts[k].Y - ucY;
                                double dxR = dx * cosR - dy * sinR, dyR = dx * sinR + dy * cosR;
                                Point3d fp = cc + dxR * Ti_world + dyR * Tj_world;
                                Point3d hit;
                                if (!ProjectOntoMesh(projMesh, fp, avgN, rayLen, out hit)) { ok = false; break; }
                                mapped[k] = hit;
                            }
                            if (ok) { var crv = new PolylineCurve(mapped); if (crv.IsValid) result.Add(crv); }
                            else AddClippedCellCurves(result, pts, cc, ucX, ucY, cosR, sinR, Ti_world, Tj_world, avgN, planeOrigin, rayLen, projMesh, clipLoops, chord, clipTol);
                        }
                        else
                            AddClippedCellCurves(result, pts, cc, ucX, ucY, cosR, sinR, Ti_world, Tj_world, avgN, planeOrigin, rayLen, projMesh, clipLoops, chord, clipTol);
                    }
                }

                // 경계 바깥쪽으로 걸친 셀(중심은 밖이지만 inside 와 인접) → 클립해서 경계까지 채움
                for (int ki = iStart; ki <= iEnd; ki++)
                    for (int kj = jStart; kj <= jEnd; kj++)
                    {
                        long key = Key(ki, kj);
                        if (insideC.Contains(key)) continue;
                        if (!(insideC.Contains(Key(ki + 1, kj)) || insideC.Contains(Key(ki - 1, kj)) ||
                              insideC.Contains(Key(ki, kj + 1)) || insideC.Contains(Key(ki, kj - 1)))) continue;
                        Point3d cc = planeOrigin + ki * info.PitchU * Ti_world + kj * info.PitchV * Tj_world;
                        foreach (var pts in cellPts)
                            AddClippedCellCurves(result, pts, cc, ucX, ucY, cosR, sinR, Ti_world, Tj_world, avgN, planeOrigin, rayLen, projMesh, clipLoops, chord, clipTol);
                    }
                return result;
            }

            // === 모드 0(삭제)/1(축소): inside 격자점 (마진 인셋 반영) ===
            var inside = new Dictionary<long, Point3d>();
            for (int ki = iStart; ki <= iEnd; ki++)
                for (int kj = jStart; kj <= jEnd; kj++)
                {
                    Point3d cc = planeOrigin + ki * info.PitchU * Ti_world + kj * info.PitchV * Tj_world;
                    Point3d hitc;
                    if (!ProjectOntoMesh(projMesh, cc, avgN, rayLen, out hitc)) continue;
                    if (margin > 1e-9 && bLoops != null &&
                        MinDistToLoops(To2D(cc, planeOrigin, Ti_world, Tj_world), bLoops) < margin) continue; // 마진 인셋
                    inside[Key(ki, kj)] = cc;
                }

            // 모드 1(축소): 외곽으로부터 링 인덱스 BFS (ring 1 = 경계 접한 셀)
            Dictionary<long, int> ring = null;
            if (boundaryMode == 1)
            {
                ring = new Dictionary<long, int>();
                var q = new Queue<long>();
                foreach (var kv in inside)
                {
                    long key = kv.Key;
                    int ki = (int)((key >> 21) - 100000);
                    int kj = (int)((key & 0x1FFFFF) - 100000);
                    if (!inside.ContainsKey(Key(ki + 1, kj)) || !inside.ContainsKey(Key(ki - 1, kj)) ||
                        !inside.ContainsKey(Key(ki, kj + 1)) || !inside.ContainsKey(Key(ki, kj - 1)))
                    { ring[key] = 1; q.Enqueue(key); }
                }
                int[] ddi = { 1, -1, 0, 0 }, ddj = { 0, 0, 1, -1 };
                while (q.Count > 0)
                {
                    long key = q.Dequeue();
                    int ki = (int)((key >> 21) - 100000);
                    int kj = (int)((key & 0x1FFFFF) - 100000);
                    int r = ring[key];
                    for (int d = 0; d < 4; d++)
                    {
                        long nk = Key(ki + ddi[d], kj + ddj[d]);
                        if (inside.ContainsKey(nk) && !ring.ContainsKey(nk)) { ring[nk] = r + 1; q.Enqueue(nk); }
                    }
                }
            }

            int fadeDenom = Math.Max(1, fadeRings) + 1;
            foreach (var kv in inside)
            {
                Point3d cellCenterPlane = kv.Value;
                double cellScale = 1.0;
                if (boundaryMode == 1)
                {
                    int r = ring.ContainsKey(kv.Key) ? ring[kv.Key] : fadeDenom;
                    cellScale = Math.Min(1.0, r / (double)fadeDenom);
                    if (cellScale < 0.06) continue; // 거의 사라짐 → 생략
                }
                foreach (var pts in cellPts)
                {
                    var mapped = new Point3d[pts.Length];
                    bool allOnSurface = true;
                    for (int k = 0; k < pts.Length; k++)
                    {
                        double dx = (pts[k].X - ucX) * cellScale;
                        double dy = (pts[k].Y - ucY) * cellScale;
                        double dxR = dx * cosR - dy * sinR;
                        double dyR = dx * sinR + dy * cosR;
                        Point3d flatPlanePt = cellCenterPlane + dxR * Ti_world + dyR * Tj_world;
                        Point3d hit;
                        if (!ProjectOntoMesh(projMesh, flatPlanePt, avgN, rayLen, out hit)) { allOnSurface = false; break; }
                        mapped[k] = hit;
                    }
                    if (allOnSurface) { var crv = new PolylineCurve(mapped); if (crv.IsValid) result.Add(crv); }
                }
            }
            return result;
        }

        private static Point3d To2D(Point3d P, Point3d origin, Vector3d Ti, Vector3d Tj)
        {
            Vector3d v = P - origin;
            return new Point3d(v * Ti, v * Tj, 0);
        }

        /// <summary>선택 면의 경계(외곽+구멍) naked edge 를 평면 2D(z=0) 닫힌 loop 으로 투영. sampleChord 로 촘촘히 샘플.</summary>
        private static List<Curve> BuildPlaneBoundaryLoops(Brep brep, IList<int> faceIndices,
                Point3d origin, Vector3d Ti, Vector3d Tj, double sampleChord)
        {
            var loops = new List<Curve>();
            Brep sub = null;
            try { sub = brep.DuplicateSubBrep(faceIndices); } catch { }
            if (sub == null) return loops;
            double sc = Math.Max(1e-4, sampleChord);
            var segs = new List<Curve>();
            foreach (var edge in sub.Edges)
            {
                var adj = edge.AdjacentFaces();
                if (adj == null || adj.Length != 1) continue; // naked edge 만
                var c = edge.DuplicateCurve();
                if (c == null) continue;
                // 균일 샘플 후 공선점 제거 → 직선은 양 끝점만, 곡선은 필요한 만큼 (정확 + 불리언 빠름).
                int n = (int)Math.Ceiling(c.GetLength() / sc);
                if (n < 8) n = 8; if (n > 2000) n = 2000;
                var dom = c.Domain;
                var full = new List<Point3d>(n + 1);
                for (int i = 0; i <= n; i++) full.Add(To2D(c.PointAt(dom.ParameterAt(i / (double)n)), origin, Ti, Tj));
                double simpTol = sc * 0.1;
                var p2 = new List<Point3d> { full[0] };
                for (int i = 1; i < full.Count - 1; i++)
                {
                    Point3d a = p2[p2.Count - 1], b = full[i], cc2 = full[i + 1];
                    Vector3d ac = cc2 - a; double acl = ac.Length;
                    double dist;
                    if (acl < 1e-9) dist = b.DistanceTo(a);
                    else { var cross = Vector3d.CrossProduct(ac, b - a); dist = cross.Length / acl; }
                    if (dist > simpTol) p2.Add(b); // 공선이 아니면 유지
                }
                p2.Add(full[full.Count - 1]);
                var pl = new PolylineCurve(p2);
                if (pl.IsValid) segs.Add(pl);
            }
            var joined = Curve.JoinCurves(segs, Math.Max(0.01, sc));
            if (joined != null)
                foreach (var j in joined) if (j != null && j.IsClosed) loops.Add(j);
            return loops;
        }

        /// <summary>경계에 걸친 셀(2D)을 경계 region(outer ∩ ¬holes)으로 클립 후 표면에 투영해 추가.</summary>
        private static void AddClippedCellCurves(List<Curve> result, Point3d[] pts,
                Point3d cellCenterPlane, double ucX, double ucY, double cosR, double sinR,
                Vector3d Ti, Vector3d Tj, Vector3d avgN, Point3d origin, double rayLen,
                Mesh projMesh, List<Curve> loops, double chord, double tol)
        {
            if (loops == null || loops.Count == 0) return;
            double cu = (cellCenterPlane - origin) * Ti;
            double cv = (cellCenterPlane - origin) * Tj;
            var poly = new List<Point3d>();
            foreach (var p in pts)
            {
                double dx = p.X - ucX, dy = p.Y - ucY;
                double dxR = dx * cosR - dy * sinR;
                double dyR = dx * sinR + dy * cosR;
                poly.Add(new Point3d(cu + dxR, cv + dyR, 0));
            }
            ClipPolyAndProject(result, poly, cellCenterPlane, Ti, Tj, avgN, origin, rayLen, projMesh, loops, chord, tol);
        }

        /// <summary>평면 2D 폴리곤(poly, z=0)을 경계 region(outer ∩ ¬holes)으로 클립 후 표면에 투영해 추가.</summary>
        private static void ClipPolyAndProject(List<Curve> result, List<Point3d> poly, Point3d center3D,
                Vector3d Ti, Vector3d Tj, Vector3d avgN, Point3d origin, double rayLen,
                Mesh projMesh, List<Curve> loops, double chord, double tol)
        {
            if (loops == null || loops.Count == 0 || poly == null || poly.Count < 3) return;
            if (poly[0].DistanceTo(poly[poly.Count - 1]) > 1e-9) poly.Add(poly[0]);
            var cell2D = new PolylineCurve(poly);
            if (!cell2D.IsClosed) return;

            // outer = 최대 면적 loop, 나머지 = 구멍
            Curve outer = null; double bestA = -1;
            var holes = new List<Curve>();
            foreach (var lp in loops)
            {
                double a = LoopArea(lp);
                if (a > bestA) { if (outer != null) holes.Add(outer); bestA = a; outer = lp; }
                else holes.Add(lp);
            }
            if (outer == null) return;

            var pieces = new List<Curve>();
            Curve[] inter = null;
            try { inter = Curve.CreateBooleanIntersection(cell2D, outer, tol); } catch { }
            if (inter != null && inter.Length > 0) pieces.AddRange(inter);
            else
            {
                // 불리언 결과 없음: 모든 꼭짓점이 outer 안일 때만(완전 내부) 원본 추가.
                // 교차 셀인데 불리언 실패한 경우 통째로 추가하면 경계 밖 조각(리저/외부 셀)이 생기므로 버림.
                bool allIn = true;
                foreach (var pt in poly)
                    if (outer.Contains(pt, Plane.WorldXY, tol) != PointContainment.Inside) { allIn = false; break; }
                if (allIn) pieces.Add(cell2D);
                else return;
            }

            // 구멍 빼기: 겹치면 차집합, 차집합 실패/빈 결과면 그 조각은 버림(구멍 안 잔여물 방지).
            foreach (var hole in holes)
            {
                if (pieces.Count == 0) break;
                var hbb = hole.GetBoundingBox(true);
                var next = new List<Curve>();
                foreach (var pc in pieces)
                {
                    var pbb = pc.GetBoundingBox(true);
                    bool overlap = pbb.Min.X <= hbb.Max.X && pbb.Max.X >= hbb.Min.X &&
                                   pbb.Min.Y <= hbb.Max.Y && pbb.Max.Y >= hbb.Min.Y;
                    if (!overlap) { next.Add(pc); continue; } // 구멍과 안 겹침 → 그대로
                    Curve[] diff = null;
                    try { diff = Curve.CreateBooleanDifference(pc, hole, tol); } catch { }
                    if (diff != null && diff.Length > 0) next.AddRange(diff);
                    // diff 실패/빈 결과: 구멍과 겹치는데 못 빼면 버림 (구멍 안 침범 방지)
                }
                pieces = next;
            }

            foreach (var pc in pieces)
            {
                if (pc == null) continue;
                // 클립 결과의 "실제 꼭짓점" 사용(코너 선명). 폴리라인이면 정점, 폴리커브(직선)면 세그먼트 시작점, 아니면 샘플.
                Point3d[] sp = ClipPieceVertices(pc, chord);
                if (sp == null || sp.Length < 3) continue;
                int n = sp.Length;
                var P3 = new Point3d[n];
                var t = new double[n];
                var hasT = new bool[n];
                int hitCount = 0;
                for (int k = 0; k < n; k++)
                {
                    P3[k] = origin + sp[k].X * Ti + sp[k].Y * Tj;
                    Point3d hit;
                    if (ProjectOntoMesh(projMesh, P3[k], avgN, rayLen, out hit))
                    { t[k] = (hit - P3[k]) * avgN; hasT[k] = true; hitCount++; }
                }
                if (hitCount == 0) continue; // 표면에 전혀 안 닿음 → 버림(리저 방지)
                // miss(컷 모서리) 점 높이: 고리(cyclic)를 따라 양쪽 적중점 사이 선형 보간 → 컷 모서리가 깔끔한 직선
                var mapped = new Point3d[n + 1];
                for (int k = 0; k < n; k++)
                {
                    double tk;
                    if (hasT[k]) tk = t[k];
                    else
                    {
                        int fwd = -1, bwd = -1, fdist = 0, bdist = 0;
                        for (int s = 1; s <= n; s++) { int idx = (k + s) % n; if (hasT[idx]) { fwd = idx; fdist = s; break; } }
                        for (int s = 1; s <= n; s++) { int idx = ((k - s) % n + n) % n; if (hasT[idx]) { bwd = idx; bdist = s; break; } }
                        if (fwd < 0 && bwd < 0) tk = 0;
                        else if (fwd < 0) tk = t[bwd];
                        else if (bwd < 0) tk = t[fwd];
                        else { double w = (double)bdist / (bdist + fdist); tk = t[bwd] + (t[fwd] - t[bwd]) * w; }
                    }
                    mapped[k] = P3[k] + tk * avgN; // (u,v) 정확 보존
                }
                mapped[n] = mapped[0]; // 닫기
                var crv = new PolylineCurve(mapped);
                if (crv.IsValid) result.Add(crv);
            }
        }

        /// <summary>클립 결과 곡선의 꼭짓점 배열(닫힘 중복 제거). 폴리라인/직선 폴리커브는 정점 그대로(코너 선명), 그 외는 샘플.</summary>
        private static Point3d[] ClipPieceVertices(Curve pc, double chord)
        {
            Polyline pl;
            if (pc.TryGetPolyline(out pl) && pl != null && pl.Count >= 3)
            {
                int cnt = pl.Count;
                if (cnt > 1 && pl[0].DistanceTo(pl[cnt - 1]) < 1e-9) cnt--; // 닫힘 중복 제거
                var arr = new Point3d[cnt];
                for (int i = 0; i < cnt; i++) arr[i] = pl[i];
                return arr;
            }
            var segs = pc.DuplicateSegments();
            if (segs != null && segs.Length >= 3)
            {
                bool allLine = true;
                foreach (var s in segs) if (s != null && !s.IsLinear(1e-6)) { allLine = false; break; }
                if (allLine)
                {
                    var arr = new Point3d[segs.Length];
                    for (int i = 0; i < segs.Length; i++) arr[i] = segs[i].PointAtStart;
                    return arr;
                }
            }
            return SampleCurve(pc, chord); // 곡선 세그먼트 포함 → 샘플 폴백
        }

        /// <summary>닫힌 2D loop 들을 margin 만큼 안쪽으로 인셋 (outer 축소, hole 확대).</summary>
        private static List<Curve> InsetLoops(List<Curve> loops, double margin, double tol)
        {
            if (loops == null || loops.Count == 0 || margin <= 1e-9) return loops;
            Curve outer = null; double bestA = -1; var holes = new List<Curve>();
            foreach (var lp in loops) { double a = LoopArea(lp); if (a > bestA) { if (outer != null) holes.Add(outer); bestA = a; outer = lp; } else holes.Add(lp); }
            var outList = new List<Curve>();
            outList.Add(OffsetClosedPick(outer, margin, tol, true) ?? outer);
            foreach (var h in holes) outList.Add(OffsetClosedPick(h, margin, tol, false) ?? h);
            return outList;
        }

        // 닫힌 곡선을 ±dist 로 offset 한 뒤 wantSmaller 면 더 작은 면적, 아니면 더 큰 면적 결과를 선택.
        private static Curve OffsetClosedPick(Curve loop, double dist, double tol, bool wantSmaller)
        {
            if (loop == null) return null;
            try
            {
                var pos = loop.Offset(Plane.WorldXY, dist, tol, CurveOffsetCornerStyle.Sharp);
                var neg = loop.Offset(Plane.WorldXY, -dist, tol, CurveOffsetCornerStyle.Sharp);
                Curve cp = JoinFirstClosed(pos), cn = JoinFirstClosed(neg);
                if (cp == null && cn == null) return null;
                if (cp == null) return cn;
                if (cn == null) return cp;
                double ap = LoopArea(cp), an = LoopArea(cn);
                return wantSmaller ? (ap <= an ? cp : cn) : (ap >= an ? cp : cn);
            }
            catch { return null; }
        }

        private static Curve JoinFirstClosed(Curve[] arr)
        {
            if (arr == null || arr.Length == 0) return null;
            var j = Curve.JoinCurves(arr, 0.01);
            if (j != null) foreach (var c in j) if (c != null && c.IsClosed) return c;
            return (arr.Length == 1 && arr[0] != null && arr[0].IsClosed) ? arr[0] : null;
        }

        private static double MinDistToLoops(Point3d uv, List<Curve> loops)
        {
            double md = double.MaxValue;
            if (loops == null) return md;
            foreach (var lp in loops)
            {
                double t;
                if (lp != null && lp.ClosestPoint(uv, out t))
                {
                    double d = lp.PointAt(t).DistanceTo(uv);
                    if (d < md) md = d;
                }
            }
            return md;
        }

        private static bool InRegion(Point3d uv, Curve outer, List<Curve> holes, double tol)
        {
            if (outer == null) return true;
            if (outer.Contains(uv, Plane.WorldXY, tol) != PointContainment.Inside) return false;
            if (holes != null) foreach (var h in holes) if (h != null && h.Contains(uv, Plane.WorldXY, tol) == PointContainment.Inside) return false;
            return true;
        }

        private static double LoopArea(Curve c)
        {
            try { var amp = AreaMassProperties.Compute(c); if (amp != null) return Math.Abs(amp.Area); } catch { }
            var bb = c.GetBoundingBox(true);
            return (bb.Max.X - bb.Min.X) * (bb.Max.Y - bb.Min.Y);
        }

        /// <summary>점 p 를 dir(양/음) 방향 ray 로 mesh 에 투영. 가장 가까운 교차점 반환. 교차 없으면 false(=경계 밖).</summary>
        private static bool ProjectOntoMesh(Mesh mesh, Point3d p, Vector3d dir, double rayLen, out Point3d hit)
        {
            hit = p;
            var line = new Line(p - dir * rayLen, p + dir * rayLen);
            int[] faceIds;
            var pts = Rhino.Geometry.Intersect.Intersection.MeshLine(mesh, line, out faceIds);
            if (pts == null || pts.Length == 0) return false;
            double best = double.MaxValue;
            foreach (var q in pts)
            {
                double d = q.DistanceTo(p);
                if (d < best) { best = d; hit = q; }
            }
            return true;
        }

        /// <summary>
        /// 평행 투영 전략 공통 프레임: 선택 면 평균 normal(미러 보정) → World 축 snap = 투영 방향 avgN.
        /// 평면 내 격자축 Ti/Tj 는 가장 가까운 World 축에 정렬(기본 0/90°). 사용자 회전은 호출측에서 적용.
        /// seed = centroid → 표면 snap (실패 시 centroid).
        /// </summary>
        private static bool ComputeProjectionFrame(Brep brep, IList<int> faceIndices, HashSet<int> faceSet,
                Vector3d refDir,
                out Vector3d avgN, out Vector3d Ti, out Vector3d Tj,
                out Point3d seedSurf, out BoundingBox sbb, out double bboxDiag)
        {
            avgN = Vector3d.ZAxis; Ti = Vector3d.XAxis; Tj = Vector3d.YAxis;
            seedSurf = Point3d.Origin; sbb = BoundingBox.Empty; bboxDiag = 0;

            Vector3d sumN = Vector3d.Zero, sumCenter = Vector3d.Zero;
            int validCount = 0;
            foreach (int fi in faceIndices)
            {
                var face = brep.Faces[fi];
                double fuMin, fuMax, fvMin, fvMax;
                GetFaceUvBox(face, out fuMin, out fuMax, out fvMin, out fvMax);
                Point3d c; Vector3d du, dv;
                if (!EvalDeriv(face, 0.5 * (fuMin + fuMax), 0.5 * (fvMin + fvMax), out c, out du, out dv)) continue;
                if (du.Length < 1e-9 || dv.Length < 1e-9) continue;
                var n = Vector3d.CrossProduct(du, dv);
                if (n.Length < 1e-9) continue;
                n.Unitize();
                if (face.OrientationIsReversed) n = -n; // 미러면 normal 복원
                sumN += n; sumCenter += (Vector3d)c; validCount++;
            }
            if (validCount == 0) return false;
            Point3d centroidPt = new Point3d(sumCenter / validCount);
            if (sumN.Length < 1e-6) return false;
            avgN = sumN; avgN.Unitize();

            double aX = Math.Abs(avgN.X), aY = Math.Abs(avgN.Y), aZ = Math.Abs(avgN.Z);
            if (aZ > 0.9 && aZ >= aX && aZ >= aY) avgN = new Vector3d(0, 0, avgN.Z > 0 ? 1 : -1);
            else if (aY > 0.9 && aY >= aX && aY >= aZ) avgN = new Vector3d(0, avgN.Y > 0 ? 1 : -1, 0);
            else if (aX > 0.9 && aX >= aY && aX >= aZ) avgN = new Vector3d(avgN.X > 0 ? 1 : -1, 0, 0);

            // Ti 후보 (refDir → World 축 fallback)
            Vector3d Ti0 = Vector3d.Zero;
            if (refDir.Length > 1e-9)
            {
                var rp = refDir - (refDir * avgN) * avgN;
                if (rp.Length > 1e-6) { rp.Unitize(); Ti0 = rp; }
            }
            if (Ti0.Length < 1e-6)
            {
                foreach (var axis in new[] { Vector3d.YAxis, Vector3d.XAxis, Vector3d.ZAxis })
                {
                    var pr = axis - (axis * avgN) * avgN;
                    if (pr.Length > 1e-6) { pr.Unitize(); Ti0 = pr; break; }
                }
            }
            if (Ti0.Length < 1e-6) return false;

            // 0/90° 정렬: 평면 내 가장 가까운 World 축
            Vector3d bestAxis = Vector3d.Zero; double bestDot = -1;
            foreach (var axis in new[] { Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis })
            {
                var pr = axis - (axis * avgN) * avgN;
                if (pr.Length < 1e-6) continue;
                pr.Unitize();
                double dot = pr * Ti0;
                if (Math.Abs(dot) > bestDot) { bestDot = Math.Abs(dot); bestAxis = (dot >= 0) ? pr : -pr; }
            }
            if (bestAxis.Length > 1e-6) Ti0 = bestAxis;

            Ti = Ti0;
            Tj = Vector3d.CrossProduct(avgN, Ti); Tj.Unitize();

            foreach (int fi in faceIndices) sbb.Union(brep.Faces[fi].GetBoundingBox(true));
            bboxDiag = sbb.Diagonal.Length;
            int seedFi;
            if (!TrySnapToSelectedFacesWithIndex(brep, faceSet, centroidPt, bboxDiag, out seedSurf, out seedFi))
                seedSurf = centroidPt;
            return true;
        }

        /// <summary>선택 면들을 하나의 mesh 로 (평행 투영 ray 교차 + trim 클리핑용). 실패 시 null.</summary>
        private static Mesh BuildSelectedFacesMesh(Brep brep, HashSet<int> faceSet)
        {
            var projMesh = new Mesh();
            Mesh[] faceMeshes = Mesh.CreateFromBrep(brep, MeshingParameters.Default);
            if (faceMeshes != null)
                foreach (int fi in faceSet)
                    if (fi < faceMeshes.Length && faceMeshes[fi] != null) projMesh.Append(faceMeshes[fi]);
            if (projMesh.Faces.Count == 0) return null;
            projMesh.Compact();
            return projMesh;
        }

        /// <summary>
        /// PartialFit 전략 1 (평행 투영): 패턴 한 묶음을 평면에 강체 배치(offset/회전/scale) 후 avgN 방향 투영.
        /// 표면 밖으로 투영되는 vertex 가 있는 커브는 제거(외곽선/구멍 클리핑). 미러/곡면 무관 균일.
        /// </summary>
        // boundaryMode/fadeRings/margin: RealSize 와 동일 개념의 경계 처리(단, 단일 stamp 라 거리 기반).
        /// <summary>
        /// PartialFit 전략1(평행투영)에서 반복 재계산(인터랙티브)에 재사용할 무거운 사전 계산 결과.
        /// 면 mesh / 투영 프레임 / 경계 loop 은 패턴 위치·회전·크기와 무관하므로 한 번만 만들어 캐시한다.
        /// </summary>
        public class PartialProjContext
        {
            public bool Valid;
            public PatternInfo Info;
            public Vector3d AvgN, Ti, Tj;
            public Point3d SeedSurf;
            public double BboxDiag;
            public Mesh ProjMesh;
            public List<Curve> BLoops;     // 2D 경계 loop (없으면 null)
            public List<Curve> ClipLoops;  // 인셋된 자르기 loop (없으면 BLoops)
            public double ClipTol;
            public double FadeDist;
            public int BoundaryMode;
            public double Margin;
        }

        /// <summary>무거운 사전 계산(면 mesh·프레임·경계 loop)을 한 번 수행. 인터랙티브 드래그 전에 호출해 캐시.</summary>
        public static PartialProjContext BuildPartialProjContext(Brep brep, IList<int> faceIndices,
                IList<Curve> patternCurves, Vector3d refDir,
                int boundaryMode = 0, int fadeRings = 2, double margin = 0)
        {
            var ctx = new PartialProjContext { Valid = false, BoundaryMode = boundaryMode, Margin = margin };
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return ctx;
            if (patternCurves == null || patternCurves.Count == 0) return ctx;
            var info = PatternAnalyzer.Analyze(patternCurves);
            if (!info.Valid) return ctx;
            var faceSet = new HashSet<int>(faceIndices);

            Vector3d avgN, Ti, Tj; Point3d seedSurf; BoundingBox sbb; double bboxDiag;
            if (!ComputeProjectionFrame(brep, faceIndices, faceSet, refDir, out avgN, out Ti, out Tj, out seedSurf, out sbb, out bboxDiag))
                return ctx;
            var projMesh = BuildSelectedFacesMesh(brep, faceSet);
            if (projMesh == null) return ctx;

            double chord = Math.Max(info.CellW, info.CellH) / 20.0;
            Point3d origin = seedSurf;
            List<Curve> bLoops = (boundaryMode == 2 || (boundaryMode == 0 && margin > 1e-9))
                ? BuildPlaneBoundaryLoops(brep, faceIndices, origin, Ti, Tj, chord) : null;
            double clipTol = Math.Max(1e-4, Math.Min(info.CellW, info.CellH) * 0.01);
            List<Curve> clipLoops = (boundaryMode == 2 && margin > 1e-9 && bLoops != null && bLoops.Count > 0)
                ? InsetLoops(bLoops, margin, clipTol) : bLoops;
            double fadeDist = Math.Max(1e-6, Math.Max(1, fadeRings) * Math.Min(info.PitchU, info.PitchV));

            ctx.Info = info; ctx.AvgN = avgN; ctx.Ti = Ti; ctx.Tj = Tj; ctx.SeedSurf = seedSurf;
            ctx.BboxDiag = bboxDiag; ctx.ProjMesh = projMesh; ctx.BLoops = bLoops; ctx.ClipLoops = clipLoops;
            ctx.ClipTol = clipTol; ctx.FadeDist = fadeDist; ctx.Valid = true;
            return ctx;
        }

        public static List<Curve> TileConnectedPartial_Projection(Brep brep, IList<int> faceIndices,
                IList<Curve> patternCurves, BoundingBox patternBox,
                Vector3d refDir, double angleTolRad,
                double uOffsetMm, double vOffsetMm, double rotationDeg,
                double scale = 1.0, Point3d? patternCenterOverride = null,
                int boundaryMode = 0, int fadeRings = 2, double margin = 0)
        {
            var ctx = BuildPartialProjContext(brep, faceIndices, patternCurves, refDir, boundaryMode, fadeRings, margin);
            if (!ctx.Valid) return new List<Curve>();
            return TilePartialProjectionFromContext(ctx, patternCurves, patternBox,
                uOffsetMm, vOffsetMm, rotationDeg, scale, patternCenterOverride);
        }

        /// <summary>캐시된 컨텍스트로 패턴 한 묶음을 배치/투영 (가벼운 per-frame 부분). 인터랙티브 드래그에서 매 프레임 호출.</summary>
        public static List<Curve> TilePartialProjectionFromContext(PartialProjContext ctx,
                IList<Curve> patternCurves, BoundingBox patternBox,
                double uOffsetMm, double vOffsetMm, double rotationDeg,
                double scale = 1.0, Point3d? patternCenterOverride = null)
        {
            var result = new List<Curve>();
            if (ctx == null || !ctx.Valid || patternCurves == null || patternCurves.Count == 0) return result;

            var info = ctx.Info;
            Vector3d avgN = ctx.AvgN, Ti = ctx.Ti, Tj = ctx.Tj;
            Point3d seedSurf = ctx.SeedSurf;
            Mesh projMesh = ctx.ProjMesh;
            int boundaryMode = ctx.BoundaryMode;
            double margin = ctx.Margin;
            List<Curve> bLoops = ctx.BLoops, clipLoops = ctx.ClipLoops;
            double clipTol = ctx.ClipTol, fadeDist = ctx.FadeDist;
            double rayLen = Math.Max(ctx.BboxDiag * 2.0, 1.0);

            // 패턴 중심 (평면 위). override 면 그 점, 아니면 seed + (U,V) offset.
            Point3d planeCenter = patternCenterOverride.HasValue
                ? patternCenterOverride.Value
                : seedSurf + uOffsetMm * Ti + vOffsetMm * Tj;

            double rotRad = rotationDeg * Math.PI / 180.0;
            double cosR = Math.Cos(rotRad), sinR = Math.Sin(rotRad);
            double pCx = 0.5 * (patternBox.Min.X + patternBox.Max.X);
            double pCy = 0.5 * (patternBox.Min.Y + patternBox.Max.Y);
            double chord = Math.Max(info.CellW, info.CellH) / 20.0;
            Point3d origin = seedSurf; // To2D 기준 평면 원점

            // 축소 기준 = 패턴 bbox 가장자리 (패턴 공간)
            double pbMinX = patternBox.Min.X, pbMaxX = patternBox.Max.X;
            double pbMinY = patternBox.Min.Y, pbMaxY = patternBox.Max.Y;

            // 패턴 공간 점(px,py) → 평면 3D. es = 구멍 자체 중심(ccx,ccy) 기준 축소 배율.
            Func<double, double, double, double, double, Point3d> ToPlane3D = (px, py, es, ccx, ccy) =>
            {
                double sx = ccx + (px - ccx) * es;
                double sy = ccy + (py - ccy) * es;
                double offX = (sx - pCx) * scale;
                double offY = (sy - pCy) * scale;
                double offRX = offX * cosR - offY * sinR;
                double offRY = offX * sinR + offY * cosR;
                return planeCenter + offRX * Ti + offRY * Tj;
            };

            foreach (var c in patternCurves)
            {
                var pts = SampleCurve(c, chord);
                if (pts == null || pts.Length < 2) continue;

                double ccx = 0, ccy = 0;
                foreach (var p in pts) { ccx += p.X; ccy += p.Y; }
                ccx /= pts.Length; ccy /= pts.Length;

                Point3d center3D = ToPlane3D(ccx, ccy, 1.0, ccx, ccy);

                double cellScale = 1.0;
                if (boundaryMode == 1) // 축소: 기준 = 패턴이 끝나는 지점(패턴 bbox 가장자리)
                {
                    double dEdge = Math.Min(Math.Min(ccx - pbMinX, pbMaxX - ccx),
                                            Math.Min(ccy - pbMinY, pbMaxY - ccy));
                    cellScale = Math.Min(1.0, Math.Max(0.0, dEdge) / fadeDist);
                    if (cellScale < 0.06) continue;
                }
                else if (boundaryMode == 0 && margin > 1e-9 && bLoops != null) // 삭제 + 마진: 인셋 밖 제거
                {
                    double dEff = MinDistToLoops(To2D(center3D, origin, Ti, Tj), bLoops) - margin;
                    if (dEff <= 0) continue;
                }

                if (boundaryMode == 2) // 자르기: 경계에 맞춰 클립
                {
                    var poly = new List<Point3d>();
                    foreach (var p in pts)
                        poly.Add(To2D(ToPlane3D(p.X, p.Y, 1.0, ccx, ccy), origin, Ti, Tj));
                    ClipPolyAndProject(result, poly, center3D, Ti, Tj, avgN, origin, rayLen, projMesh, clipLoops, chord, clipTol);
                    continue;
                }

                // 삭제/축소: 전체 vertex 투영, 하나라도 면 밖이면 제거
                var mapped = new Point3d[pts.Length];
                bool ok = true;
                for (int k = 0; k < pts.Length; k++)
                {
                    Point3d fp = ToPlane3D(pts[k].X, pts[k].Y, cellScale, ccx, ccy);
                    Point3d hit;
                    if (!ProjectOntoMesh(projMesh, fp, avgN, rayLen, out hit)) { ok = false; break; }
                    mapped[k] = hit;
                }
                if (!ok) continue;
                var crv = new PolylineCurve(mapped);
                if (crv.IsValid) result.Add(crv);
            }
            return result;
        }

        /// <summary>
        /// Stretch 전략 1 (평행 투영): 영역을 nU×nV 로 나눠 비균일 스케일한 패턴을 평면에 배치 후 avgN 방향 투영.
        /// 표면 밖 투영 커브는 제거(외곽선 클리핑). 미러/곡면 무관.
        /// </summary>
        public static List<Curve> TileConnectedStretch_Projection(Brep brep, IList<int> faceIndices,
                IList<Curve> patternCurves, BoundingBox patternBox,
                Vector3d refDir, double angleTolRad,
                int nU = 1, int nV = 1, double margin = 0,
                bool flipH = false, bool flipV = false, double rotationDeg = 0)
        {
            var result = new List<Curve>();
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return result;
            if (patternCurves == null || patternCurves.Count == 0) return result;
            var info = PatternAnalyzer.Analyze(patternCurves);
            if (!info.Valid) return result;
            var faceSet = new HashSet<int>(faceIndices);

            Vector3d avgN, Ti, Tj; Point3d seedSurf; BoundingBox sbb; double bboxDiag;
            if (!ComputeProjectionFrame(brep, faceIndices, faceSet, refDir, out avgN, out Ti, out Tj, out seedSurf, out sbb, out bboxDiag))
                return result;
            var projMesh = BuildSelectedFacesMesh(brep, faceSet);
            if (projMesh == null) return result;
            double rayLen = Math.Max(bboxDiag * 2.0, 1.0);

            // 영역 bbox (평면 mm 좌표)
            double iMinMm = double.MaxValue, iMaxMm = double.MinValue;
            double jMinMm = double.MaxValue, jMaxMm = double.MinValue;
            foreach (var corner in sbb.GetCorners())
            {
                Vector3d vc = corner - seedSurf;
                double iv = vc * Ti, jv = vc * Tj;
                if (iv < iMinMm) iMinMm = iv;
                if (iv > iMaxMm) iMaxMm = iv;
                if (jv < jMinMm) jMinMm = jv;
                if (jv > jMaxMm) jMaxMm = jv;
            }
            if (iMinMm >= iMaxMm || jMinMm >= jMaxMm) return result;
            if (margin > 1e-9)
            {
                iMinMm += margin; iMaxMm -= margin; jMinMm += margin; jMaxMm -= margin;
                if (iMinMm >= iMaxMm || jMinMm >= jMaxMm) return result;
            }

            double pw = patternBox.Max.X - patternBox.Min.X;
            double ph2 = patternBox.Max.Y - patternBox.Min.Y;
            if (pw < 1e-9 || ph2 < 1e-9) return result;
            double pCx = 0.5 * (patternBox.Min.X + patternBox.Max.X);
            double pCy = 0.5 * (patternBox.Min.Y + patternBox.Max.Y);
            double rotRad = rotationDeg * Math.PI / 180.0;
            double cosR = Math.Cos(rotRad), sinR = Math.Sin(rotRad);
            double absC = Math.Abs(cosR), absS = Math.Abs(sinR);
            double Wrot = pw * absC + ph2 * absS;
            double Hrot = pw * absS + ph2 * absC;

            nU = Math.Max(1, nU); nV = Math.Max(1, nV);
            double iSpanMm = iMaxMm - iMinMm, jSpanMm = jMaxMm - jMinMm;
            double gapX = nU > 1 ? EstimateGap(patternCurves, 0) : 0;
            double gapY = nV > 1 ? EstimateGap(patternCurves, 1) : 0;
            double tileWmm = (iSpanMm - (nU - 1) * gapX) / nU;
            double tileHmm = (jSpanMm - (nV - 1) * gapY) / nV;
            if (tileWmm < 1e-9 || tileHmm < 1e-9) return result;
            double scaleX = tileWmm / Wrot, scaleY = tileHmm / Hrot;

            double chord = Math.Max(info.CellW, info.CellH) / 20.0;

            for (int ti = 0; ti < nU; ti++)
            {
                for (int tj = 0; tj < nV; tj++)
                {
                    double tileCxMm = iMinMm + ti * (tileWmm + gapX) + tileWmm * 0.5;
                    double tileCyMm = jMinMm + tj * (tileHmm + gapY) + tileHmm * 0.5;
                    Point3d tileCenterPlane = seedSurf + tileCxMm * Ti + tileCyMm * Tj;

                    foreach (var c in patternCurves)
                    {
                        var pts = SampleCurve(c, chord);
                        var mapped = new Point3d[pts.Length];
                        bool ok = true;
                        for (int k = 0; k < pts.Length; k++)
                        {
                            double vx = pts[k].X, vy = pts[k].Y;
                            if (flipH) vx = patternBox.Max.X + patternBox.Min.X - vx;
                            if (flipV) vy = patternBox.Max.Y + patternBox.Min.Y - vy;
                            double offX = vx - pCx, offY = vy - pCy;
                            double offRX = offX * cosR - offY * sinR;
                            double offRY = offX * sinR + offY * cosR;
                            double offWX = offRX * scaleX, offWY = offRY * scaleY;
                            Point3d flatPlanePt = tileCenterPlane + offWX * Ti + offWY * Tj;
                            Point3d hit;
                            if (!ProjectOntoMesh(projMesh, flatPlanePt, avgN, rayLen, out hit)) { ok = false; break; }
                            mapped[k] = hit;
                        }
                        if (!ok) continue;
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
        private static List<Curve> TileConnectedRealSizeFit_WalkingLegacy(Brep brep, IList<int> faceIndices,
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
                // 미러로 만든 면은 du×dv 가 뒤집혀 있음 → OrientationIsReversed 로 진짜 바깥 방향 복원.
                // (보정 안 하면 미러면 normal 이 합산에서 상쇄돼 avgN 이 망가짐)
                if (face.OrientationIsReversed) n = -n;
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
                if (curFace.OrientationIsReversed) curN = -curN; // 미러면 normal 복원 → Tj_local 부호 일관
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

                    // C. World-space dedup: 기존 cell 중 0.5 × Pitch 안에 있는 것 있으면 skip
                    //    (인접 셀은 최소 1.0×Pitch 떨어지므로 0.5 는 곡률로 접힌 중복만 제거 → 정상 셀 보존)
                    double dedupDist = Math.Min(info.PitchU, info.PitchV) * 0.5;
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
            while (gapFillProgressed && gapFillIter < 40)
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
                    if (face.OrientationIsReversed) nL = -nL; // 미러면 normal 복원 → Tj_loc 부호 일관
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
                        double dedupG = Math.Min(info.PitchU, info.PitchV) * 0.5;
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
            double directDedup = Math.Min(info.PitchU, info.PitchV) * 0.45;

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
                if (snapFace.OrientationIsReversed) N = -N; // 미러면 normal 복원 → Tj_local/hex 방향 일관

                // Ti_local = Ti_world projected onto local tangent plane (consistent orientation)
                var Ti_local = Ti_world - (Ti_world * N) * N;
                if (Ti_local.Length < 1e-6) continue;
                Ti_local.Unitize();
                var Tj_local = Vector3d.CrossProduct(N, Ti_local);

                // Vertex 가 선택 면의 UV TRIM 안에 있어야만 통과 (boundary 너머 cell 정확히 reject)
                // 거리 검사가 아닌 trim 검사 → boundary 부근 cell 도 깨끗히 제거
                double vertexSnapMax = Math.Min(info.PitchU, info.PitchV) * 2.0;
                // 내부 seam(선택된 두 면이 만나는 공유 경계)은 통과시키고, 진짜 바깥 경계(naked edge)만 거부.
                // → 면 사이 이음새에서 셀이 통째로 사라져 생기던 줄 모양 공백 제거.
                // seamEps: vertex 가 해당 면 surface 위에 "닿아" 있다고 볼 거리.
                double seamEps = Math.Min(info.PitchU, info.PitchV) * 0.15;

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

                        // 1) Interior 우선: 어느 선택 면의 trim 내부에 있으면 그 면으로 snap.
                        // 2) 아니면 seam 검사: 두 개 이상의 선택 면 surface 에 동시에 닿아 있으면
                        //    (= 내부 공유 이음새) 통과. 한 면에만 닿으면 바깥 경계 → 거부.
                        bool interiorFound = false;
                        double bestInteriorDist = double.MaxValue;
                        Point3d bestInteriorPt = flat;
                        int onSurfaceCount = 0;
                        double bestSeamDist = double.MaxValue;
                        Point3d bestSeamPt = flat;
                        foreach (int vfi in faceSet)
                        {
                            var vf = brep.Faces[vfi];
                            double vU, vV;
                            if (!((Surface)vf).ClosestPoint(flat, out vU, out vV)) continue;
                            var vp = ((Surface)vf).PointAt(vU, vV);
                            double d = vp.DistanceTo(flat);
                            var rel = vf.IsPointOnFace(vU, vV);
                            if (rel == PointFaceRelation.Interior && d < vertexSnapMax && d < bestInteriorDist)
                            {
                                bestInteriorDist = d;
                                bestInteriorPt = vp;
                                interiorFound = true;
                            }
                            // surface 에 닿아 있는지 (Interior/Boundary 무관, 거리만) → seam 판정용
                            if (d < seamEps)
                            {
                                onSurfaceCount++;
                                if (d < bestSeamDist) { bestSeamDist = d; bestSeamPt = vp; }
                            }
                        }
                        if (interiorFound) mapped[k] = bestInteriorPt;
                        else if (onSurfaceCount >= 2) mapped[k] = bestSeamPt; // 내부 공유 seam → 통과
                        else { allInsideTrim = false; break; }                 // 바깥 경계 밖 → cell 전체 reject
                    }
                    if (!allInsideTrim) continue;
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
        /// <summary>
        /// RealSize 전략 2: 평균 normal world-space flat lattice 투영.
        /// 곡면 walking 대신 평면 격자를 한 번에 surface 로 투영 → snap.
        /// 다면(multi-face) / 평면 위주 / sharp 각도로 만나는 면들에 강함
        /// (전략 1 Surface Walking 이 면 전환에서 drift 하는 케이스의 대안).
        /// </summary>
        public static List<Curve> TileConnectedRealSizeFit_StrategyTwo(Brep brep, IList<int> faceIndices,
                                                            PatternInfo info, Vector3d refDir, double angleTolRad,
                                                            double rotationDeg = 0)
        {
            var result = new List<Curve>();
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return result;
            if (info == null || !info.Valid || info.UnitCells.Count == 0) return result;
            var faceSet = new HashSet<int>(faceIndices);

            // === Strategy 2: 평균 normal world-space flat lattice ===
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

            // 기본 0/90° 원칙: Ti 를 평면 내에서 가장 가까운 World 축(의 평면 투영)에 정렬.
            // (refDir/avgDu 의 미세한 tilt 제거 → 회전 슬라이더 0 일 때 정확히 수직/수평)
            {
                Vector3d bestAxis = Vector3d.Zero;
                double bestDot = -1;
                foreach (var axis in new[] { Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis })
                {
                    var proj = axis - (axis * avgN) * avgN; // 평면에 투영 (avgN 과 평행한 축은 0)
                    if (proj.Length < 1e-6) continue;
                    proj.Unitize();
                    double dot = proj * Ti_init;
                    if (Math.Abs(dot) > bestDot)
                    {
                        bestDot = Math.Abs(dot);
                        bestAxis = (dot >= 0) ? proj : -proj; // 원래 Ti 방향(부호) 유지
                    }
                }
                if (bestAxis.Length > 1e-6)
                {
                    Ti_init = bestAxis;
                    Tj_init = Vector3d.CrossProduct(avgN, Ti_init);
                    Tj_init.Unitize();
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

        /// <summary>패턴 곡선들에서 실제 격자를 재추정: 행별 가로 피치(Pu), 행 간격(Pv),
        /// 인접 행 간 가로 위상 증분(deltaPhase, 벽돌식이면 ≈Pu/2). repCell=대표 셀.</summary>
        private static void AnalyzePatternLattice(IList<Curve> curves, double cellH,
                out double Pu, out double Pv, out double deltaPhase, out Curve repCell)
        {
            Pu = 0; Pv = 0; deltaPhase = 0; repCell = null;
            if (curves == null || curves.Count == 0) return;
            var cen = new List<double[]>();
            foreach (var c in curves) { var b = c.GetBoundingBox(true); cen.Add(new[] { b.Center.X, b.Center.Y }); }
            cen.Sort((a, b) => a[1].CompareTo(b[1]));
            double yTol = Math.Max(1e-6, cellH * 0.6);
            var rows = new List<List<double>>();
            var rowY = new List<double>();
            double lastY = double.NaN;
            foreach (var p in cen)
            {
                if (rows.Count == 0 || p[1] - lastY > yTol) { rows.Add(new List<double>()); rowY.Add(p[1]); }
                rows[rows.Count - 1].Add(p[0]);
                lastY = p[1];
            }
            var vd = new List<double>();
            for (int i = 1; i < rowY.Count; i++) vd.Add(rowY[i] - rowY[i - 1]);
            Pv = Median(vd);
            var ud = new List<double>();
            foreach (var r in rows) { r.Sort(); for (int i = 1; i < r.Count; i++) ud.Add(r[i] - r[i - 1]); }
            Pu = Median(ud);
            if (Pu > 1e-9)
            {
                var pd = new List<double>();
                for (int i = 1; i < rows.Count; i++)
                {
                    if (rows[i].Count == 0 || rows[i - 1].Count == 0) continue;
                    double d = rows[i][0] - rows[i - 1][0];
                    pd.Add(d - Pu * Math.Round(d / Pu)); // [-Pu/2, Pu/2]
                }
                deltaPhase = Median(pd);
            }
            // 대표 셀: 전체 중심에 가장 가까운 곡선을 원점 정렬
            double cx = 0, cy = 0; foreach (var p in cen) { cx += p[0]; cy += p[1]; }
            cx /= cen.Count; cy /= cen.Count;
            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < curves.Count; i++)
            {
                var b = curves[i].GetBoundingBox(true);
                double d = (b.Center.X - cx) * (b.Center.X - cx) + (b.Center.Y - cy) * (b.Center.Y - cy);
                if (d < bestD) { bestD = d; best = i; }
            }
            repCell = curves[best].DuplicateCurve();
            var rb = repCell.GetBoundingBox(true);
            repCell.Translate(-rb.Center.X, -rb.Center.Y, -rb.Center.Z);
        }

        private static double Median(List<double> v)
        {
            if (v == null || v.Count == 0) return 0;
            v.Sort();
            return v[v.Count / 2];
        }

        /// <summary>
        /// Strategy 3 (면별 UV 격자 + 실측 격자 재추정): 패턴의 실제 가로 피치/행 간격/벽돌 엇갈림을
        /// 재계산해, 각 면의 UV 공간에 그 간격으로 셀(대표 도형)을 배치한다. 평면 투영이 겹치는 곡면에서도
        /// 표면을 따라가며 셀 간격·연속성·엇갈림을 그대로 재현. 셀은 원본 곡선 강체 변환(끝단 매끈).
        /// </summary>
        public static List<Curve> TileConnectedRealSizeFit_StrategyThree(Brep brep, IList<int> faceIndices,
                PatternInfo info, IList<Curve> patternCurves, Vector3d refDir, double angleTolRad, double rotationDeg = 0)
        {
            var result = new List<Curve>();
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return result;
            if (info == null || !info.Valid || info.UnitCells.Count == 0) return result;

            // 실제 격자 재추정 (브릭 엇갈림 → PatternAnalyzer 가 가로 피치를 절반으로 보는 문제 보정)
            double Pu, Pv, deltaPhase; Curve repCell;
            AnalyzePatternLattice(patternCurves, info.CellH, out Pu, out Pv, out deltaPhase, out repCell);
            if (Pu <= 1e-6) Pu = Math.Max(1e-6, info.PitchU);
            if (Pv <= 1e-6) Pv = Math.Max(1e-6, info.PitchV);
            if (repCell == null) repCell = info.UnitCells[0];

            double rotRad = rotationDeg * Math.PI / 180.0;
            double cosR = Math.Cos(rotRad), sinR = Math.Sin(rotRad);

            // 셀을 표면에 안착시키기 위해 곡선을 미세 샘플(원점 정렬됨) → 정점마다 UV 오프셋으로 표면점 계산
            double chord = Math.Max(0.2, Math.Min(info.CellW, info.CellH) / 12.0);
            var repPts = SampleCurve(repCell, chord);

            double dedupDist = Math.Min(Pu, Pv) * 0.4;
            var occ = new Dictionary<long, List<Point3d>>();

            // 선택 면 전체를 하나의 sub-brep 으로 합쳐 면 경계를 무시하고 연속 타일링.
            // 시드 링(중간 높이 단면)에서 출발해, 각 셀을 표면을 따라 Pv 만큼 올려/내려(균일 세로간격)
            // 다음 링 곡선을 만들고, 그 곡선을 Pu 간격(+브릭 위상)으로 재샘플 → 셀 수가 둘레에 맞춰 자동 증감.
            var sub = brep.DuplicateSubBrep(faceIndices);
            if (sub == null) return result;
            var sbb = sub.GetBoundingBox(true);
            double secTol = Math.Max(1e-4, Math.Min(Pu, Pv) * 0.02);
            double axX = 0.5 * (sbb.Min.X + sbb.Max.X);
            double axY = 0.5 * (sbb.Min.Y + sbb.Max.Y);
            double bigR = sbb.Diagonal.Length + 10.0;
            double z0 = sbb.Min.Z, z1 = sbb.Max.Z;

            // 점 → sub 표면 투영(면/uv/도함수/법선)
            bool Reproject(Point3d pt, out Point3d P, out BrepFace face, out double u, out double v, out Vector3d du, out Vector3d dv, out Vector3d N)
            {
                P = pt; face = null; u = 0; v = 0; du = Vector3d.Zero; dv = Vector3d.Zero; N = Vector3d.ZAxis;
                Point3d cp; ComponentIndex ci; double s, t; Vector3d nrm;
                if (!sub.ClosestPoint(pt, out cp, out ci, out s, out t, 0.0, out nrm)) return false;
                int fidx = (ci.ComponentIndexType == ComponentIndexType.BrepFace) ? ci.Index : -1;
                if (fidx < 0)
                {
                    double best = double.MaxValue;
                    for (int i = 0; i < sub.Faces.Count; i++)
                    {
                        double uu, vv;
                        if (!((Surface)sub.Faces[i]).ClosestPoint(cp, out uu, out vv)) continue;
                        double d = ((Surface)sub.Faces[i]).PointAt(uu, vv).DistanceTo(cp);
                        if (d < best) { best = d; fidx = i; }
                    }
                    if (fidx < 0) return false;
                }
                face = sub.Faces[fidx];
                if (!((Surface)face).ClosestPoint(cp, out u, out v)) return false;
                Point3d pp;
                if (!EvalDeriv(face, u, v, out pp, out du, out dv)) return false;
                if (du.Length < 1e-9 || dv.Length < 1e-9) return false;
                N = Vector3d.CrossProduct(du, dv); if (N.Length < 1e-9) return false; N.Unitize();
                if (face.OrientationIsReversed) N = -N;
                P = pp; return true;
            }

            // 한 점에 셀 배치(around = 링 진행방향). 정점은 UV 오프셋으로 표면에 안착.
            void PlaceCell(Point3d P, BrepFace face, double u, double v, Vector3d du, Vector3d dv, Vector3d N, Vector3d around)
            {
                double a = du * du, b = du * dv, cc = dv * dv, det = a * cc - b * b;
                if (Math.Abs(det) < 1e-12) return;
                Vector3d ar = around - (around * N) * N; if (ar.Length < 1e-9) return; ar.Unitize();
                Vector3d up = Vector3d.CrossProduct(N, ar); if (up.Length < 1e-9) return; up.Unitize();
                if (up * Vector3d.ZAxis < 0) up = -up;
                Vector3d Xr = cosR * ar + sinR * up;
                Vector3d Yr = -sinR * ar + cosR * up;
                if (!DedupTryAdd(occ, P, dedupDist)) return;
                var mapped = new Point3d[repPts.Length];
                for (int k = 0; k < repPts.Length; k++)
                {
                    Vector3d off = repPts[k].X * Xr + repPts[k].Y * Yr;
                    double e = du * off, f = dv * off;
                    double delU = (e * cc - f * b) / det;
                    double delV = (a * f - b * e) / det;
                    mapped[k] = ((Surface)face).PointAt(u + delU, v + delV);
                }
                var crv = new PolylineCurve(mapped);
                if (crv.IsValid) result.Add(crv);
            }

            // 수평 단면(Z=const)으로 면 경계를 가로지르는 연속 링.
            // 핵심: 각 링의 셀을 "바로 아래 행 셀들의 중간점"에 둠 → 모든 각도에서 정확히 반 셀 엇갈림(브릭).
            //       (절대 +X 기준이 아니라 아래 행 기준이라, 둘레/셀수가 달라도 양쪽 면이 동일하게 브릭이 됨)
            // 간격이 Pu 미만이 될 곳은 셀 제거, 너무 벌어질 곳은 셀 삽입(국소) → 겹침 없음, 아래로 갈수록 자동 증가.

            // 지정 높이의 단면(가장 긴 닫힌 곡선, 반시계 정규화)
            Func<double, Curve> SectionAt = (zz) =>
            {
                Curve[] ss; Point3d[] pp;
                if (!Rhino.Geometry.Intersect.Intersection.BrepPlane(sub, new Plane(new Point3d(0, 0, zz), Vector3d.ZAxis), secTol, out ss, out pp) || ss == null) return null;
                Curve best = null; double bl = 0;
                foreach (var c in ss) if (c != null && c.IsClosed) { double l = c.GetLength(); if (l > bl) { bl = l; best = c; } }
                if (best != null && best.ClosedCurveOrientation(Vector3d.ZAxis) == CurveOrientation.Clockwise) best.Reverse();
                return best;
            };

            // 곡선 C 의 호 위치 목록에 셀 배치 + 그 3D 점들 반환
            Func<Curve, List<double>, List<Point3d>> PlaceArcs = (C, arcsIn) =>
            {
                var ptsOut = new List<Point3d>();
                double L = C.GetLength();
                foreach (double aRaw in arcsIn)
                {
                    double a = ((aRaw % L) + L) % L;
                    double tp; if (!C.LengthParameter(a, out tp)) continue;
                    Point3d raw = C.PointAt(tp); Vector3d tan = C.TangentAt(tp);
                    Point3d P; BrepFace f; double u, v; Vector3d du, dv, N;
                    if (!Reproject(raw, out P, out f, out u, out v, out du, out dv, out N)) continue;
                    ptsOut.Add(P);
                    PlaceCell(P, f, u, v, du, dv, N, tan);
                }
                return ptsOut;
            };

            // 셀 수를 nDes 로 맞춤: 좁은 간격 셀 제거 / 넓은 간격에 셀 삽입(브릭은 대부분 유지, 변경은 국소)
            Action<List<double>, double, int> AdjustCount = (a, L, nDes) =>
            {
                while (a.Count > nDes && a.Count > 1)
                {
                    int c = a.Count, rem = 0; double best = double.MaxValue;
                    for (int i = 0; i < c; i++) { double g = (i + 1 < c ? a[i + 1] : a[0] + L) - a[i]; if (g < best) { best = g; rem = (i + 1) % c; } }
                    a.RemoveAt(rem);
                }
                while (a.Count < nDes && a.Count >= 1)
                {
                    int c = a.Count, ins = 0; double best = -1, pos = 0;
                    for (int i = 0; i < c; i++) { double aa = a[i], bb = (i + 1 < c ? a[i + 1] : a[0] + L); double g = bb - aa; if (g > best) { best = g; ins = i; pos = ((aa + bb) / 2) % L; } }
                    a.Insert(ins + 1, pos);
                }
                a.Sort();
            };

            // 이전 행(prevPts) 기준으로 C 위에 브릭(중간점) 셀 호 위치 생성
            Func<Curve, List<Point3d>, List<double>> BrickArcs = (C, prevPts) =>
            {
                double L = C.GetLength();
                int nMax = Math.Max(1, (int)Math.Floor(L / Pu));          // 간격 ≥ Pu (겹침 금지)
                int nMin = Math.Max(1, (int)Math.Ceiling(L / (Pu * 1.6))); // 간격 ≤ 1.6Pu (아래로 약간 넓어짐 허용 → 코너 삽입 빈도↓)
                if (nMax < nMin) nMax = nMin;
                var arcs = new List<double>();
                foreach (var p in prevPts) { double t; if (C.ClosestPoint(p, out t)) arcs.Add(C.GetLength(new Interval(C.Domain.T0, t))); }
                if (arcs.Count < 2)
                {
                    var fa = new List<double>(); int nf = Math.Min(nMax, Math.Max(nMin, nMax));
                    for (int k = 0; k < nf; k++) fa.Add(k * L / nf);
                    return fa;
                }
                arcs.Sort();
                int m = arcs.Count;
                var mids = new List<double>();
                for (int i = 0; i < m; i++) { double aa = arcs[i]; double bb = (i + 1 < m) ? arcs[i + 1] : arcs[0] + L; mids.Add(((aa + bb) / 2) % L); }
                mids.Sort();
                int nDes = Math.Min(nMax, Math.Max(nMin, m)); // 현재 셀 수를 최대한 유지(브릭 연속), 간격 한도 내에서
                // 삽입(넓어지는 방향)은 링당 1개로 제한 → 코너에서 한 줄에 셀이 우르르 추가/병합되며
                // 엇갈림이 깨지던 현상 방지(국소 분산, 양 코너가 번갈아 한 칸씩 증가).
                // 제거(좁아지는 방향)는 제한하지 않음 → 간격 ≥ Pu(겹침 금지) 유지가 우선.
                if (nDes > m + 1) nDes = m + 1;
                AdjustCount(mids, L, nDes);
                return mids;
            };

            // 시드 링 = 둘레가 '가장 좁은' 단면. 거기서 양방향으로 멀어질수록 면적이 넓어지므로
            // 브릭 진행이 '셀 삽입'만 발생(아래 BrickArcs 에서 링당 1개로 분산) → 코너에서 셀이 한꺼번에
            // 합쳐지며(제거) 엇갈림이 깨지던 문제를 원천 제거. (퍼널 형상은 시드가 자연히 최상단 근처)
            double zSeed = 0.5 * (z0 + z1); Curve seedC = null; double minLen = double.MaxValue;
            for (int i = 1; i < 28; i++)
            {
                double zz = z0 + (z1 - z0) * (i / 28.0);
                var c = SectionAt(zz);
                if (c == null) continue;
                double l = c.GetLength();
                if (l < Pu * 2.0) continue;
                if (l < minLen) { minLen = l; seedC = c; zSeed = zz; }
            }
            if (seedC == null) return result;
            double seedL = seedC.GetLength();
            int n0 = Math.Max(1, (int)Math.Floor(seedL / Pu));
            { int nm = Math.Max(1, (int)Math.Ceiling(seedL / (Pu * 1.6))); if (n0 < nm) n0 = nm; }
            var seedArcs = new List<double>(); for (int k = 0; k < n0; k++) seedArcs.Add(k * seedL / n0);
            var prevRing = PlaceArcs(seedC, seedArcs);

            // ΔZ(경사) 추정: 한 점에서 측정
            Func<Curve, double> SlopeOf = (C) =>
            {
                double tp; C.LengthParameter(0.0, out tp);
                Point3d rp = C.PointAt(tp); Vector3d rt = C.TangentAt(tp);
                Point3d rP; BrepFace rf; double ru, rv; Vector3d rdu, rdv, rN;
                if (!Reproject(rp, out rP, out rf, out ru, out rv, out rdu, out rdv, out rN)) return 1.0;
                Vector3d arrR = rt - (rt * rN) * rN; if (arrR.Length < 1e-9) return 1.0; arrR.Unitize();
                Vector3d upR = Vector3d.CrossProduct(rN, arrR); if (upR.Length < 1e-9) return 1.0; upR.Unitize();
                return Math.Max(0.2, Math.Abs(upR * Vector3d.ZAxis));
            };

            // 위로
            double zc = zSeed; var cur = prevRing; var curC = seedC; int g2 = 0;
            while (g2++ < 4000)
            {
                zc += Pv * SlopeOf(curC);
                if (zc >= z1) break;
                Curve C = SectionAt(zc); if (C == null) break;
                var arcs = BrickArcs(C, cur); var nr = PlaceArcs(C, arcs);
                if (nr.Count < 2) break; cur = nr; curC = C;
            }
            // 아래로
            zc = zSeed; cur = prevRing; curC = seedC; g2 = 0;
            while (g2++ < 4000)
            {
                zc -= Pv * SlopeOf(curC);
                if (zc <= z0) break;
                Curve C = SectionAt(zc); if (C == null) break;
                var arcs = BrickArcs(C, cur); var nr = PlaceArcs(C, arcs);
                if (nr.Count < 2) break; cur = nr; curC = C;
            }
            return result;
        }

        /// <summary>v 고정 행을 따라 uMin→uMax 의 world 호 길이(약 120 분할 적분).</summary>
        private static double RingArcLength(BrepFace face, double v, double uMin, double uMax)
        {
            int seg = 120;
            double du = (uMax - uMin) / seg;
            double len = 0;
            Point3d prev; Vector3d a, b;
            if (!EvalDeriv(face, uMin, v, out prev, out a, out b)) return 0;
            for (int i = 1; i <= seg; i++)
            {
                Point3d cur; Vector3d a2, b2;
                if (!EvalDeriv(face, uMin + i * du, v, out cur, out a2, out b2)) { prev = cur; continue; }
                len += cur.DistanceTo(prev);
                prev = cur;
            }
            return len;
        }

        /// <summary>v 고정 행에서 현재 u 로부터 world 호 길이 arc 만큼 전진한 u 반환(닫힌 면이면 주기 wrap).</summary>
        private static double AdvanceU(BrepFace face, double v, double u, double arc, double uMin, double uMax, bool wrap)
        {
            double remaining = arc; int g = 0;
            double maxStep = Math.Max(1e-9, (uMax - uMin) / 60.0);
            while (remaining > 1e-9 && g++ < 2000)
            {
                double sp = SurfaceSpeed(face, true, v, u);
                if (sp < 1e-9) break;
                double step = remaining / sp;
                if (step > maxStep) step = maxStep;
                u += step;
                remaining -= step * sp;
                if (u > uMax) { if (wrap) u = uMin + (u - uMax); else { u = uMax; break; } }
            }
            return u;
        }

        /// <summary>한 파라미터를 start 에서 양방향으로, 매 스텝 국부 호 길이가 pitch 가 되도록 적응 행진.</summary>
        private static List<double> MarchAdaptive(BrepFace face, bool marchU, double fixedC,
                                                  double start, double pMin, double pMax, double pitch)
        {
            var list = new List<double>();
            if (start >= pMin - 1e-9 && start <= pMax + 1e-9)
                list.Add(Math.Min(pMax, Math.Max(pMin, start)));
            double p = start; int g = 0;
            while (g++ < 5000)
            {
                double sp = SurfaceSpeed(face, marchU, fixedC, p);
                if (sp < 1e-9) break;
                p += pitch / sp;
                if (p > pMax + 1e-9) break;
                list.Add(p);
            }
            p = start; g = 0;
            while (g++ < 5000)
            {
                double sp = SurfaceSpeed(face, marchU, fixedC, p);
                if (sp < 1e-9) break;
                p -= pitch / sp;
                if (p < pMin - 1e-9) break;
                list.Add(p);
            }
            list.Sort();
            return list;
        }

        /// <summary>(marchU?u:v)=p, 나머지=fixedC 지점에서 그 방향 편도함수 길이(=단위 파라미터당 world 거리).</summary>
        private static double SurfaceSpeed(BrepFace face, bool marchU, double fixedC, double p)
        {
            double u = marchU ? p : fixedC;
            double v = marchU ? fixedC : p;
            Point3d pt; Vector3d du, dv;
            if (!EvalDeriv(face, u, v, out pt, out du, out dv)) return 0;
            return marchU ? du.Length : dv.Length;
        }

        /// <summary>공간 해시 거리 기반 중복 제거: p 가 기존 점과 dist 미만이면 false(중복), 아니면 등록 후 true.</summary>
        private static bool DedupTryAdd(Dictionary<long, List<Point3d>> occ, Point3d p, double dist)
        {
            if (dist < 1e-9) dist = 1e-9;
            long bx = (long)Math.Floor(p.X / dist);
            long by = (long)Math.Floor(p.Y / dist);
            long bz = (long)Math.Floor(p.Z / dist);
            double d2 = dist * dist;
            for (int ax = -1; ax <= 1; ax++)
                for (int ay = -1; ay <= 1; ay++)
                    for (int az = -1; az <= 1; az++)
                    {
                        List<Point3d> lst;
                        if (occ.TryGetValue(BucketKey(bx + ax, by + ay, bz + az), out lst))
                            foreach (var q in lst)
                                if ((q - p).SquareLength < d2) return false;
                    }
            long key = BucketKey(bx, by, bz);
            List<Point3d> cell;
            if (!occ.TryGetValue(key, out cell)) { cell = new List<Point3d>(); occ[key] = cell; }
            cell.Add(p);
            return true;
        }

        private static long BucketKey(long x, long y, long z)
        {
            unchecked { return (x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L); }
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
                // 미러로 만든 면은 du×dv 가 뒤집혀 있음 → OrientationIsReversed 로 진짜 바깥 방향 복원.
                // (보정 안 하면 미러면 normal 이 합산에서 상쇄돼 avgN 이 망가짐)
                if (face.OrientationIsReversed) n = -n;
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
                // 미러로 만든 면은 du×dv 가 뒤집혀 있음 → OrientationIsReversed 로 진짜 바깥 방향 복원.
                // (보정 안 하면 미러면 normal 이 합산에서 상쇄돼 avgN 이 망가짐)
                if (face.OrientationIsReversed) n = -n;
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
