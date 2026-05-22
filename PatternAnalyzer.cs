using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace Plugin01
{
    /// <summary>분석된 패턴 규칙: 단위 도형(원점 기준)과 X/Y 간격.</summary>
    public class PatternInfo
    {
        public List<Curve> UnitCells = new List<Curve>(); // 원점 중심으로 이동된 단위 도형
        public double PitchU;  // X(가로) 중심 간격
        public double PitchV;  // Y(세로) 중심 간격
        public double CellW;
        public double CellH;
        public bool Valid;
    }

    /// <summary>
    /// 선택된 패턴 커브들로부터 규칙(단위 도형 형태·크기, 간격)을 추정한다.
    /// 현재 가정: 동일 형태가 격자(행/열)로 반복. 단위 도형은 대표 1개로 본다.
    /// </summary>
    public static class PatternAnalyzer
    {
        public static PatternInfo Analyze(IList<Curve> curves)
        {
            var info = new PatternInfo();
            if (curves == null || curves.Count == 0) return info;

            var centers = new List<Point3d>();
            double sumW = 0, sumH = 0;
            foreach (var c in curves)
            {
                var b = c.GetBoundingBox(true);
                centers.Add(b.Center);
                sumW += b.Max.X - b.Min.X;
                sumH += b.Max.Y - b.Min.Y;
            }
            info.CellW = sumW / curves.Count;
            info.CellH = sumH / curves.Count;

            double tolX = Math.Max(info.CellW * 0.25, 1e-6);
            double tolY = Math.Max(info.CellH * 0.25, 1e-6);
            info.PitchU = MedianSpacing(centers.Select(p => p.X), tolX, info.CellW);
            info.PitchV = MedianSpacing(centers.Select(p => p.Y), tolY, info.CellH);

            // 대표 단위 셀: 전체 중심에 가장 가까운 커브를 원점으로 이동
            var centroid = Centroid(centers);
            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < centers.Count; i++)
            {
                double d = centers[i].DistanceTo(centroid);
                if (d < bestD) { bestD = d; best = i; }
            }
            var unit = curves[best].DuplicateCurve();
            var bb = unit.GetBoundingBox(true);
            unit.Translate(-bb.Center.X, -bb.Center.Y, -bb.Center.Z);
            info.UnitCells.Add(unit);

            info.Valid = info.PitchU > 1e-6 && info.PitchV > 1e-6;
            return info;
        }

        private static Point3d Centroid(List<Point3d> pts)
        {
            double x = 0, y = 0, z = 0;
            foreach (var p in pts) { x += p.X; y += p.Y; z += p.Z; }
            int n = Math.Max(1, pts.Count);
            return new Point3d(x / n, y / n, z / n);
        }

        // 값들을 군집으로 묶고(군집 = 같은 행/열), 군집 중심 간격의 중앙값 반환.
        private static double MedianSpacing(IEnumerable<double> values, double tol, double fallback)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count < 2) return fallback;

            var clusters = new List<double>();
            double sum = sorted[0]; int cnt = 1;
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i] - sorted[i - 1] <= tol) { sum += sorted[i]; cnt++; }
                else { clusters.Add(sum / cnt); sum = sorted[i]; cnt = 1; }
            }
            clusters.Add(sum / cnt);

            if (clusters.Count < 2) return fallback;
            var diffs = new List<double>();
            for (int i = 1; i < clusters.Count; i++) diffs.Add(clusters[i] - clusters[i - 1]);
            diffs.Sort();
            return diffs[diffs.Count / 2];
        }
    }
}
