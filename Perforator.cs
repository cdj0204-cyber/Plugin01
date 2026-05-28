using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace Plugin01
{
    /// <summary>
    /// 한 방향 천공 (선택 벽면만 관통):
    /// - 셀마다 셀 중심을 통과하는 직선과 brep 교차쌍을 구해, "사용자가 선택한 벽면"이 포함된 쌍만 cutter span 에 포함.
    /// - 양쪽 벽면을 둘 다 선택하면 cell 이 두 벽을 동시에 관통.
    /// - 선택 벽면 인덱스가 없으면 셀에 가장 가까운 벽 자동 탐지.
    /// - Cutter 는 벽 두께 + cell direction 범위 + 양쪽 1mm 이상 (Coincident-face 회피).
    /// - 셀 폐 커브를 관통 방향 수직 평면에 투영해 압출 → 프리즘 커터.
    /// - 한 번에 차집합 → 실패 시 하나씩.
    /// </summary>
    public static class Perforator
    {
        /// <summary>Cutter 가 벽면을 넘어 최소로 더 연장되는 거리 (mm). Coincident-face 로 인한 boolean 오류 회피.</summary>
        public const double DefaultSafetyMm = 1.0;

        public class Result
        {
            public Brep[] Breps;
            public int CutterCount;
            public int SuccessCount;
            public int FallbackCount;
            public int FailedCount;
            public int NoWallCount; // 벽 span 을 못 찾은 셀 수
            public string Stage;    // "all-at-once" | "one-at-a-time"
        }

        public class CutterBuildResult
        {
            public List<Brep> Cutters = new List<Brep>();
            public List<Curve> SourceCurves = new List<Curve>();
            public int FallbackCount;
            public int FailedCount;
            public int NoWallCount;
        }

        /// <summary>커터 솔리드만 빌드 (boolean 차집합은 하지 않음). 미리보기/검증용.</summary>
        public static CutterBuildResult BuildCutters(
            Brep target,
            IList<Curve> punchCurves,
            Vector3d direction,
            double tolerance,
            bool wallOnly = true,
            IList<int> punchFaceIndices = null,
            double safetyStartMm = DefaultSafetyMm,
            double safetyEndMm = DefaultSafetyMm,
            double draftAngleDeg = 0.0)
        {
            var res = new CutterBuildResult();
            if (target == null || punchCurves == null || punchCurves.Count == 0) return res;
            if (!direction.Unitize()) return res;

            var bb = target.GetBoundingBox(true);
            double diag = bb.Diagonal.Length;

            double fullExtent = Math.Abs(direction * (bb.Max - bb.Min));
            if (fullExtent < 1e-6) fullExtent = diag;
            double fullLen = Math.Max(fullExtent, diag) + Math.Max(diag * 0.5, 10.0);
            var fullBasePt = bb.Center - direction * (fullLen * 0.5);

            var punchFaceSet = (punchFaceIndices != null && punchFaceIndices.Count > 0)
                ? new HashSet<int>(punchFaceIndices) : null;

            double safetyStart = Math.Max(safetyStartMm, tolerance * 5);
            double safetyEnd = Math.Max(safetyEndMm, tolerance * 5);

            foreach (var c in punchCurves)
            {
                if (c == null || !c.IsClosed) { res.FailedCount++; continue; }

                Point3d basePt;
                double len;
                if (wallOnly)
                {
                    var cellCenter = c.GetBoundingBox(true).Center;
                    double cellTMin, cellTMax;
                    GetCellDirectionExtent(c, cellCenter, direction, out cellTMin, out cellTMax);

                    bool spanOk;
                    if (punchFaceSet != null)
                        spanOk = TryFindSpanFromSelectedFaces(target, cellCenter, direction, tolerance, diag, punchFaceSet, cellTMin, cellTMax, safetyStart, safetyEnd, out basePt, out len);
                    else
                        spanOk = TryFindNearestWallSpan(target, cellCenter, direction, tolerance, diag, cellTMin, cellTMax, safetyStart, safetyEnd, out basePt, out len);

                    if (!spanOk) { res.NoWallCount++; continue; }
                }
                else
                {
                    basePt = fullBasePt;
                    len = fullLen;
                }

                var basePlane = new Plane(basePt, direction);
                // 셀이 cutter 안에서 몇 mm 깊이에 있는지 (basePt 기준)
                double cellAnchorDepth = (c.GetBoundingBox(true).Center - basePt) * direction;

                var cutter = TryProjectExtrude(c, basePlane, len, cellAnchorDepth, draftAngleDeg, tolerance);
                bool isFallback = false;
                if (cutter == null)
                {
                    cutter = CylinderFallback(c, basePlane, len);
                    isFallback = cutter != null;
                }
                if (cutter != null)
                {
                    res.Cutters.Add(cutter);
                    res.SourceCurves.Add(c);
                    if (isFallback) res.FallbackCount++;
                }
                else res.FailedCount++;
            }
            return res;
        }

        public static Result Punch(
            Brep target,
            IList<Curve> punchCurves,
            Vector3d direction,
            double tolerance,
            bool wallOnly = true,
            IList<int> punchFaceIndices = null,
            double safetyStartMm = DefaultSafetyMm,
            double safetyEndMm = DefaultSafetyMm,
            double draftAngleDeg = 0.0)
        {
            var res = new Result();
            if (target == null || punchCurves == null || punchCurves.Count == 0) return res;

            var built = BuildCutters(target, punchCurves, direction, tolerance, wallOnly, punchFaceIndices, safetyStartMm, safetyEndMm, draftAngleDeg);
            res.FailedCount = built.FailedCount;
            res.FallbackCount = built.FallbackCount;
            res.NoWallCount = built.NoWallCount;
            res.CutterCount = built.Cutters.Count;

            if (built.Cutters.Count == 0)
            {
                res.Breps = new[] { target };
                return res;
            }

            // 한 번에 차집합
            var allAtOnce = Brep.CreateBooleanDifference(new[] { target }, built.Cutters, tolerance);
            if (allAtOnce != null && allAtOnce.Length > 0 && allAtOnce[0] != null)
            {
                res.Breps = allAtOnce;
                res.SuccessCount = built.Cutters.Count;
                res.Stage = "all-at-once";
                return res;
            }

            // 하나씩 + 큰 tol 재시도
            res.Stage = "one-at-a-time";
            Brep current = target;
            for (int i = 0; i < built.Cutters.Count; i++)
            {
                var cutter = built.Cutters[i];
                var partial = Brep.CreateBooleanDifference(new[] { current }, new[] { cutter }, tolerance);
                if (IsValidResult(partial))
                {
                    current = partial[0];
                    res.SuccessCount++;
                    continue;
                }
                var p2 = Brep.CreateBooleanDifference(new[] { current }, new[] { cutter }, tolerance * 10.0);
                if (IsValidResult(p2))
                {
                    current = p2[0];
                    res.SuccessCount++;
                    continue;
                }
                res.FailedCount++;
            }
            res.Breps = new[] { current };
            return res;
        }

        /// <summary>셀 3D 커브 점들의 direction 축 좌표(cellCenter 기준) 범위 계산.</summary>
        private static void GetCellDirectionExtent(Curve c, Point3d cellCenter, Vector3d direction, out double cellTMin, out double cellTMax)
        {
            cellTMin = double.MaxValue;
            cellTMax = double.MinValue;
            var pts = new List<Point3d>();
            Polyline polyline;
            if (c.TryGetPolyline(out polyline) && polyline != null)
            {
                foreach (var p in polyline) pts.Add(p);
            }
            else
            {
                int n = 64;
                for (int i = 0; i <= n; i++)
                {
                    double t = c.Domain.ParameterAt(i / (double)n);
                    pts.Add(c.PointAt(t));
                }
            }
            foreach (var p in pts)
            {
                double td = (p - cellCenter) * direction;
                if (td < cellTMin) cellTMin = td;
                if (td > cellTMax) cellTMax = td;
            }
            if (cellTMin > cellTMax) { cellTMin = 0; cellTMax = 0; }
        }

        /// <summary>사용자가 선택한 벽면들이 포함된 모든 (enter, exit) 쌍의 합집합으로 span 결정.</summary>
        private static bool TryFindSpanFromSelectedFaces(
            Brep target, Point3d cellCenter, Vector3d direction, double tol, double bboxDiag,
            HashSet<int> selectedFaces, double cellTMin, double cellTMax,
            double safetyStart, double safetyEnd,
            out Point3d basePt, out double len)
        {
            basePt = Point3d.Origin;
            len = 0;

            Point3d[] hits;
            if (!RaycastBrep(target, cellCenter, direction, tol, bboxDiag, out hits)) return false;

            var ts = new double[hits.Length];
            var faces = new int[hits.Length];
            for (int i = 0; i < hits.Length; i++)
            {
                ts[i] = (hits[i] - cellCenter) * direction;
                faces[i] = FindFaceForPoint(target, hits[i], tol);
            }
            Array.Sort(ts, faces);

            double globalTMin = double.MaxValue, globalTMax = double.MinValue;
            bool found = false;
            for (int i = 0; i + 1 < ts.Length; i += 2)
            {
                bool sel = (faces[i] >= 0 && selectedFaces.Contains(faces[i])) ||
                           (faces[i + 1] >= 0 && selectedFaces.Contains(faces[i + 1]));
                if (!sel) continue;
                if (ts[i] < globalTMin) globalTMin = ts[i];
                if (ts[i + 1] > globalTMax) globalTMax = ts[i + 1];
                found = true;
            }
            if (!found) return false;

            double thickness = globalTMax - globalTMin;
            if (thickness < tol) return false;

            // cell 범위 + 양쪽 safety (사용자 지정값, 클램프 없음 — 사용자가 명시적으로 벽 선택했으므로)
            double tMin = Math.Min(globalTMin, cellTMin) - safetyStart;
            double tMax = Math.Max(globalTMax, cellTMax) + safetyEnd;

            basePt = cellCenter + direction * tMin;
            len = tMax - tMin;
            return true;
        }

        /// <summary>선택 벽면 정보가 없을 때: 셀 중심에 가장 가까운 (enter, exit) 쌍.</summary>
        private static bool TryFindNearestWallSpan(
            Brep target, Point3d cellCenter, Vector3d direction, double tol, double bboxDiag,
            double cellTMin, double cellTMax,
            double safetyStart, double safetyEnd,
            out Point3d basePt, out double len)
        {
            basePt = Point3d.Origin;
            len = 0;

            Point3d[] hits;
            if (!RaycastBrep(target, cellCenter, direction, tol, bboxDiag, out hits)) return false;

            var ts = new List<double>(hits.Length);
            foreach (var p in hits) ts.Add((p - cellCenter) * direction);
            ts.Sort();

            int bestPair = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i + 1 < ts.Count; i += 2)
            {
                double mid = (ts[i] + ts[i + 1]) * 0.5;
                double d = Math.Abs(mid);
                if (d < bestDist) { bestDist = d; bestPair = i; }
            }
            if (bestPair < 0) return false;

            double t1 = ts[bestPair];
            double t2 = ts[bestPair + 1];
            double thickness = t2 - t1;
            if (thickness < tol) return false;

            // 자동 벽 탐지 모드 — 사용자가 옆 벽을 보호하길 원할 가능성이 크므로 옆 벽까지 거리의 49% 로 클램프
            double localSafetyStart = safetyStart;
            double localSafetyEnd = safetyEnd;
            double frontGap = (bestPair - 1 >= 0) ? (t1 - ts[bestPair - 1]) : double.MaxValue;
            double backGap = (bestPair + 2 < ts.Count) ? (ts[bestPair + 2] - t2) : double.MaxValue;
            if (frontGap < double.MaxValue) localSafetyStart = Math.Min(localSafetyStart, frontGap * 0.49);
            if (backGap < double.MaxValue) localSafetyEnd = Math.Min(localSafetyEnd, backGap * 0.49);

            double tMin = Math.Min(t1, cellTMin) - localSafetyStart;
            double tMax = Math.Max(t2, cellTMax) + localSafetyEnd;

            basePt = cellCenter + direction * tMin;
            len = tMax - tMin;
            return true;
        }

        private static bool RaycastBrep(Brep target, Point3d cellCenter, Vector3d direction, double tol, double bboxDiag, out Point3d[] hits)
        {
            double half = bboxDiag * 1.5 + 10.0;
            var startPt = cellCenter - direction * half;
            var endPt = cellCenter + direction * half;
            var lineCurve = new LineCurve(startPt, endPt);
            Curve[] overlapCurves;
            bool ok = Rhino.Geometry.Intersect.Intersection.CurveBrep(lineCurve, target, tol, out overlapCurves, out hits);
            return ok && hits != null && hits.Length >= 2;
        }

        private static int FindFaceForPoint(Brep brep, Point3d pt, double tol)
        {
            double threshold = Math.Max(tol * 100, 0.01);
            double minDist = double.MaxValue;
            int bestFace = -1;
            for (int i = 0; i < brep.Faces.Count; i++)
            {
                double u, v;
                if (!brep.Faces[i].ClosestPoint(pt, out u, out v)) continue;
                var fp = brep.Faces[i].PointAt(u, v);
                double d = fp.DistanceTo(pt);
                if (d < minDist) { minDist = d; bestFace = i; }
            }
            return (minDist < threshold) ? bestFace : -1;
        }

        private static bool IsValidResult(Brep[] partial)
        {
            return partial != null && partial.Length > 0 && partial[0] != null && partial[0].IsValid;
        }

        private static Brep TryProjectExtrude(Curve c, Plane basePlane, double extLen, double cellAnchorDepth, double draftAngleDeg, double tol)
        {
            var projected = Curve.ProjectToPlane(c, basePlane);
            if (projected == null || !projected.IsClosed) return null;

            var selfIx = Rhino.Geometry.Intersect.Intersection.CurveSelf(projected, tol);
            if (selfIx != null && selfIx.Count > 0) return null;

            var orient = projected.ClosedCurveOrientation(basePlane);
            if (orient == CurveOrientation.Clockwise) projected.Reverse();
            else if (orient == CurveOrientation.Undefined) return null;

            var direction = basePlane.Normal;

            // draft 가 0 이면 기존 straight extrusion 경로
            if (Math.Abs(draftAngleDeg) < 1e-6)
            {
                return BuildStraightPrism(projected, direction, extLen, tol);
            }

            // draft 가 0 이 아닐 때: 양 끝 단면을 offset 으로 만든 후 loft
            double tan = Math.Tan(draftAngleDeg * Math.PI / 180.0);
            // Rhino CCW closed curve: positive offset = inside (shrink), negative offset = outside (expand)
            // offsetDistance(depth d) = (d - cellAnchorDepth) * tan
            //   depth=0 (bottom): d=0 < cellAnchorDepth → negative → outside → EXPAND
            //   depth=extLen (top): d>cellAnchorDepth (보통) → positive → inside → SHRINK
            double offBottom = (0 - cellAnchorDepth) * tan;
            double offTop = (extLen - cellAnchorDepth) * tan;

            Curve bottomCurve = SafeOffset(projected, basePlane, offBottom, tol);
            Curve topCurve = SafeOffset(projected, basePlane, offTop, tol);
            if (bottomCurve == null || topCurve == null)
            {
                // offset 실패 (보통 너무 줄여서 self-intersect) → straight 로 폴백
                return BuildStraightPrism(projected, direction, extLen, tol);
            }
            topCurve.Translate(direction * extLen);

            // Loft 옆면
            var loft = Brep.CreateFromLoft(new[] { bottomCurve, topCurve }, Point3d.Unset, Point3d.Unset, LoftType.Straight, false);
            if (loft == null || loft.Length == 0) return BuildStraightPrism(projected, direction, extLen, tol);
            var sideBrep = loft[0];

            // 캡
            var capBottom = Brep.CreatePlanarBreps(new[] { bottomCurve }, tol);
            var capTop = Brep.CreatePlanarBreps(new[] { topCurve }, tol);
            if (capBottom == null || capBottom.Length == 0) return null;
            if (capTop == null || capTop.Length == 0) return null;

            var pieces = new List<Brep> { sideBrep };
            pieces.AddRange(capBottom);
            pieces.AddRange(capTop);
            var joined = Brep.JoinBreps(pieces.ToArray(), tol);
            if (joined == null || joined.Length == 0) return null;
            var solid = joined[0];
            if (!solid.IsValid) return null;
            if (!solid.IsSolid)
            {
                var rejoin = Brep.JoinBreps(new[] { solid }, tol);
                if (rejoin != null && rejoin.Length > 0) solid = rejoin[0];
            }
            return (solid != null && solid.IsValid) ? solid : null;
        }

        private static Brep BuildStraightPrism(Curve projected, Vector3d direction, double extLen, double tol)
        {
            var sideSurf = Surface.CreateExtrusion(projected, direction * extLen);
            if (sideSurf == null) return null;
            var sideBrep = sideSurf.ToBrep();
            if (sideBrep == null) return null;

            var capCurves = new List<Curve> { projected };
            var topCurve = projected.DuplicateCurve();
            topCurve.Translate(direction * extLen);
            capCurves.Add(topCurve);
            var caps = Brep.CreatePlanarBreps(capCurves, tol);
            if (caps == null || caps.Length < 2) return null;

            var pieces = new List<Brep> { sideBrep };
            pieces.AddRange(caps);
            var joined = Brep.JoinBreps(pieces.ToArray(), tol);
            if (joined == null || joined.Length == 0) return null;
            var solid = joined[0];
            if (!solid.IsValid) return null;
            if (!solid.IsSolid)
            {
                var rejoin = Brep.JoinBreps(new[] { solid }, tol);
                if (rejoin != null && rejoin.Length > 0) solid = rejoin[0];
            }
            return (solid != null && solid.IsValid) ? solid : null;
        }

        /// <summary>거리 0 근처면 원본 복제. Offset 결과가 여러 개면 가장 긴 것 선택.</summary>
        private static Curve SafeOffset(Curve c, Plane plane, double distance, double tol)
        {
            if (Math.Abs(distance) < tol * 0.5) return c.DuplicateCurve();
            Curve[] result;
            try
            {
                result = c.Offset(plane, distance, tol, CurveOffsetCornerStyle.Sharp);
            }
            catch { return null; }
            if (result == null || result.Length == 0) return null;
            Curve best = result[0];
            double bestLen = best.GetLength();
            for (int i = 1; i < result.Length; i++)
            {
                double l = result[i].GetLength();
                if (l > bestLen) { best = result[i]; bestLen = l; }
            }
            // 결과가 닫혀있지 않거나 self-intersect 면 무효
            if (!best.IsClosed) return null;
            var selfIx = Rhino.Geometry.Intersect.Intersection.CurveSelf(best, tol);
            if (selfIx != null && selfIx.Count > 0) return null;
            return best;
        }

        private static Brep CylinderFallback(Curve c, Plane basePlane, double extLen)
        {
            var bb = c.GetBoundingBox(true);
            double r = bb.Diagonal.Length * 0.5;
            if (r < 1e-6) return null;
            var projCenter = basePlane.ClosestPoint(bb.Center);
            var circlePlane = new Plane(projCenter, basePlane.Normal);
            var circle = new Circle(circlePlane, r);
            var cyl = new Cylinder(circle, extLen);
            return cyl.ToBrep(true, true);
        }
    }
}
