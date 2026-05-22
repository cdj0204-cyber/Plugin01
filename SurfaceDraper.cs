using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace Plugin01
{
    /// <summary>
    /// 여러 면(서로 다른 바탕 곡면)으로 이루어진 전개 가능한 연결 영역에,
    /// 패턴을 "하나로 연속되게" 깐다. Unroll(전개)로 평면 좌표를 만들고,
    /// 평면에서 패턴을 매핑한 뒤 3D로 되돌린다.
    /// 전개가 불가능하면 null 반환(상위에서 다른 방식으로 폴백).
    /// </summary>
    public static class SurfaceDraper
    {
        private const int GridRes = 28; // 면당 격자 해상도

        private class FaceGrid
        {
            public int N;                 // (N+1) x (N+1) 격자
            public Point3d[,] P3;         // 3D
            public Point2d[,] F2;         // 전개 평면 좌표
            public BoundingBox FlatBox;   // 평면 바운딩(빠른 후보 제외용)
        }

        public static List<Curve> Drape(Brep brep, IList<int> faceIndices,
                                        IList<Curve> pattern, BoundingBox pBox, int nU, int nV)
        {
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return null;

            var sub = brep.DuplicateSubBrep(faceIndices);
            if (sub == null || sub.Faces.Count == 0) return null;

            // 각 면에서 (u,v) 격자 -> 3D 점 수집 (Unroll에 따라갈 점들)
            var grids = new List<FaceGrid>();
            var follow = new List<Point3d>();
            foreach (var face in sub.Faces)
            {
                Interval uReg, vReg;
                FaceUv(face, out uReg, out vReg);
                var g = new FaceGrid { N = GridRes, P3 = new Point3d[GridRes + 1, GridRes + 1], F2 = new Point2d[GridRes + 1, GridRes + 1] };
                for (int a = 0; a <= GridRes; a++)
                {
                    double u = uReg.ParameterAt((double)a / GridRes);
                    for (int b = 0; b <= GridRes; b++)
                    {
                        double v = vReg.ParameterAt((double)b / GridRes);
                        var p = face.PointAt(u, v);
                        g.P3[a, b] = p;
                        follow.Add(p);
                    }
                }
                grids.Add(g);
            }

            // 전개
            Point3d[] flat;
            try
            {
                var unroller = new Unroller(sub) { ExplodeOutput = false, AbsoluteTolerance = 0.01, RelativeTolerance = 0.01 };
                unroller.AddFollowingGeometry(follow);
                Curve[] fc; Point3d[] fp; TextDot[] fd;
                unroller.PerformUnroll(out fc, out fp, out fd);
                flat = fp;
            }
            catch { return null; }

            if (flat == null || flat.Length != follow.Count) return null;

            // 전개 평면 좌표를 격자에 되채우고 전체 평면 바운딩 계산
            int idx = 0;
            var flatAll = BoundingBox.Empty;
            foreach (var g in grids)
            {
                var fb2 = BoundingBox.Empty;
                for (int a = 0; a <= g.N; a++)
                    for (int b = 0; b <= g.N; b++)
                    {
                        var fp = flat[idx++];
                        g.F2[a, b] = new Point2d(fp.X, fp.Y);
                        var p3 = new Point3d(fp.X, fp.Y, 0);
                        fb2.Union(p3); flatAll.Union(p3);
                    }
                g.FlatBox = fb2;
            }
            if (!flatAll.IsValid) return null;

            double fw = flatAll.Max.X - flatAll.Min.X;
            double fh = flatAll.Max.Y - flatAll.Min.Y;
            if (fw < 1e-9 || fh < 1e-9) return null;

            double pw = pBox.Max.X - pBox.Min.X;
            double ph = pBox.Max.Y - pBox.Min.Y;
            if (pw < 1e-9 || ph < 1e-9) return null;

            nU = Math.Max(1, nU); nV = Math.Max(1, nV);
            double chord = Math.Max(pw, ph) / 80.0;

            var result = new List<Curve>();
            for (int i = 0; i < nU; i++)
            {
                for (int j = 0; j < nV; j++)
                {
                    foreach (var c in pattern)
                    {
                        var pts = Sample(c, chord);
                        var mapped = new List<Point3d>(pts.Length);
                        bool ok = true;
                        foreach (var p in pts)
                        {
                            double fx = (p.X - pBox.Min.X) / pw; // 0..1
                            double fy = (p.Y - pBox.Min.Y) / ph;
                            // 전개 평면상의 목표 좌표
                            double X = flatAll.Min.X + (i + fx) / nU * fw;
                            double Y = flatAll.Min.Y + (j + fy) / nV * fh;
                            Point3d p3;
                            if (!FlatTo3d(grids, new Point2d(X, Y), out p3)) { ok = false; break; }
                            mapped.Add(p3);
                        }
                        if (!ok || mapped.Count < 2) continue;
                        var crv = new PolylineCurve(mapped);
                        if (crv.IsValid) result.Add(crv);
                    }
                }
            }

            return result;
        }

        // 전개 평면 좌표 -> 원래 3D (격자 삼각형 보간)
        private static bool FlatTo3d(List<FaceGrid> grids, Point2d q, out Point3d outP)
        {
            outP = Point3d.Origin;
            foreach (var g in grids)
            {
                if (q.X < g.FlatBox.Min.X - 1e-6 || q.X > g.FlatBox.Max.X + 1e-6 ||
                    q.Y < g.FlatBox.Min.Y - 1e-6 || q.Y > g.FlatBox.Max.Y + 1e-6) continue;

                for (int a = 0; a < g.N; a++)
                {
                    for (int b = 0; b < g.N; b++)
                    {
                        // 셀의 두 삼각형
                        if (TryTri(g.F2[a, b], g.F2[a + 1, b], g.F2[a + 1, b + 1],
                                   g.P3[a, b], g.P3[a + 1, b], g.P3[a + 1, b + 1], q, out outP)) return true;
                        if (TryTri(g.F2[a, b], g.F2[a + 1, b + 1], g.F2[a, b + 1],
                                   g.P3[a, b], g.P3[a + 1, b + 1], g.P3[a, b + 1], q, out outP)) return true;
                    }
                }
            }
            return false;
        }

        private static bool TryTri(Point2d a, Point2d b, Point2d c,
                                   Point3d A, Point3d B, Point3d C, Point2d q, out Point3d outP)
        {
            outP = Point3d.Origin;
            double det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (Math.Abs(det) < 1e-12) return false;
            double w1 = ((b.Y - c.Y) * (q.X - c.X) + (c.X - b.X) * (q.Y - c.Y)) / det;
            double w2 = ((c.Y - a.Y) * (q.X - c.X) + (a.X - c.X) * (q.Y - c.Y)) / det;
            double w3 = 1 - w1 - w2;
            const double e = -1e-6;
            if (w1 < e || w2 < e || w3 < e) return false;
            outP = new Point3d(
                w1 * A.X + w2 * B.X + w3 * C.X,
                w1 * A.Y + w2 * B.Y + w3 * C.Y,
                w1 * A.Z + w2 * B.Z + w3 * C.Z);
            return true;
        }

        private static void FaceUv(BrepFace face, out Interval uReg, out Interval vReg)
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
            uReg = new Interval(a, b);
            vReg = new Interval(c, d);
        }

        private static Point3d[] Sample(Curve c, double chord)
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
            if (c.IsClosed && pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) > 1e-9)
                pts.Add(pts[0]);
            return pts.ToArray();
        }
    }
}
