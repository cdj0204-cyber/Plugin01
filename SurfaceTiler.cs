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
                                             int nU, int nV, double sampleChord)
        {
            var result = new List<Curve>();
            if (srf == null || pattern == null || pattern.Count == 0) return result;

            double pw = patternBox.Max.X - patternBox.Min.X;
            double ph = patternBox.Max.Y - patternBox.Min.Y;
            if (pw <= 1e-9 || ph <= 1e-9) return result;

            nU = Math.Max(1, nU);
            nV = Math.Max(1, nV);

            // 영역이 닫힌 방향의 전체 도메인을 덮을 때만 솔기 간격 보정
            var ud = srf.Domain(0);
            var vd = srf.Domain(1);
            bool fullU = Math.Abs(uReg.Length - ud.Length) < 1e-4 * Math.Max(1.0, ud.Length);
            bool fullV = Math.Abs(vReg.Length - vd.Length) < 1e-4 * Math.Max(1.0, vd.Length);
            double Wx = pw, Wy = ph;
            if (srf.IsClosed(0) && fullU) Wx = pw + EstimateGap(pattern, 0);
            if (srf.IsClosed(1) && fullV) Wy = ph + EstimateGap(pattern, 1);

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
                            double fx = (pts[k].X - patternBox.Min.X) / Wx;
                            double fy = (pts[k].Y - patternBox.Min.Y) / Wy;
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
        public static List<Curve> TileRealSize(BrepFace face, PatternInfo info)
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

                    foreach (var pts in cellPts)
                    {
                        var mapped = new Point3d[pts.Length];
                        for (int k = 0; k < pts.Length; k++)
                        {
                            double uu = u + pts[k].X / lu;
                            double vv = v + pts[k].Y / lv;
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
    }
}
