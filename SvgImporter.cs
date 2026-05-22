using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Rhino.Geometry;

namespace Plugin01
{
    /// <summary>
    /// SVG 파일을 읽어 Rhino 커브로 변환합니다.
    /// SVG 좌표계는 Y축이 아래로 향하므로, Rhino 월드 좌표(Y 위)에 맞춰 Y를 반전합니다.
    /// 현재 지원: path, rect, circle, ellipse, line, polyline, polygon.
    /// 아직 미지원(다음 단계): 그룹/요소 transform, viewBox 스케일, 단위 변환.
    /// </summary>
    public static class SvgImporter
    {
        public static List<Curve> Import(string filePath)
        {
            return Import(filePath, out _);
        }

        public static List<Curve> Import(string filePath, out string report)
        {
            var doc = XDocument.Load(filePath);
            var curves = new List<Curve>();
            var census = new Dictionary<string, int>();

            foreach (var el in doc.Descendants())
            {
                string tag = el.Name.LocalName;
                census[tag] = census.TryGetValue(tag, out var n) ? n + 1 : 1;

                switch (tag)
                {
                    case "path":
                        curves.AddRange(ParsePath(Attr(el, "d")));
                        break;
                    case "rect":
                        AddIfNotNull(curves, ParseRect(el));
                        break;
                    case "circle":
                        AddIfNotNull(curves, ParseCircle(el));
                        break;
                    case "ellipse":
                        AddIfNotNull(curves, ParseEllipse(el));
                        break;
                    case "line":
                        AddIfNotNull(curves, ParseLine(el));
                        break;
                    case "polyline":
                        AddIfNotNull(curves, ParsePolyPoints(Attr(el, "points"), false));
                        break;
                    case "polygon":
                        AddIfNotNull(curves, ParsePolyPoints(Attr(el, "points"), true));
                        break;
                }
            }

            report = "SVG 요소 집계: " +
                     (census.Count == 0
                         ? "(없음)"
                         : string.Join(", ", census.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value)));
            return curves;
        }

        // SVG Y축 반전 후 Rhino 점 생성
        private static Point3d Pt(double x, double y) => new Point3d(x, -y, 0.0);

        private static string Attr(XElement el, string name) => el.Attribute(name)?.Value ?? "";

        private static double D(string s)
        {
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v);
            return v;
        }

        private static void AddIfNotNull(List<Curve> list, Curve c)
        {
            if (c != null && c.IsValid) list.Add(c);
        }

        // ---------- 기본 도형 ----------

        private static Curve ParseRect(XElement el)
        {
            double x = D(Attr(el, "x")), y = D(Attr(el, "y"));
            double w = D(Attr(el, "width")), h = D(Attr(el, "height"));
            if (w <= 0 || h <= 0) return null;

            var pts = new[]
            {
                Pt(x, y), Pt(x + w, y), Pt(x + w, y + h), Pt(x, y + h), Pt(x, y)
            };
            return new PolylineCurve(pts);
        }

        private static Curve ParseCircle(XElement el)
        {
            double cx = D(Attr(el, "cx")), cy = D(Attr(el, "cy")), r = D(Attr(el, "r"));
            if (r <= 0) return null;
            return new Circle(new Plane(Pt(cx, cy), Vector3d.ZAxis), r).ToNurbsCurve();
        }

        private static Curve ParseEllipse(XElement el)
        {
            double cx = D(Attr(el, "cx")), cy = D(Attr(el, "cy"));
            double rx = D(Attr(el, "rx")), ry = D(Attr(el, "ry"));
            if (rx <= 0 || ry <= 0) return null;
            return new Ellipse(new Plane(Pt(cx, cy), Vector3d.ZAxis), rx, ry).ToNurbsCurve();
        }

        private static Curve ParseLine(XElement el)
        {
            var a = Pt(D(Attr(el, "x1")), D(Attr(el, "y1")));
            var b = Pt(D(Attr(el, "x2")), D(Attr(el, "y2")));
            if (a.DistanceTo(b) < 1e-9) return null;
            return new LineCurve(a, b);
        }

        private static Curve ParsePolyPoints(string raw, bool closed)
        {
            var nums = Numbers(raw);
            if (nums.Count < 4) return null;

            var pts = new List<Point3d>();
            for (int i = 0; i + 1 < nums.Count; i += 2)
            {
                var p = Pt(nums[i], nums[i + 1]);
                // 연속 중복점 제거 (길이 0 선분 방지)
                if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(p) > 1e-9)
                    pts.Add(p);
            }

            if (pts.Count < 2) return null;

            // 닫힌 도형: 이미 첫=끝이면 그대로, 아니면 첫 점 추가
            if (closed && pts[pts.Count - 1].DistanceTo(pts[0]) > 1e-9)
                pts.Add(pts[0]);

            return new PolylineCurve(pts);
        }

        private static List<double> Numbers(string s)
        {
            var list = new List<double>();
            foreach (Match m in Regex.Matches(s, @"-?\d*\.?\d+(?:[eE][-+]?\d+)?"))
                list.Add(D(m.Value));
            return list;
        }

        // ---------- path 데이터 ----------

        private static IEnumerable<Curve> ParsePath(string d)
        {
            var result = new List<Curve>();
            if (string.IsNullOrWhiteSpace(d)) return result;

            var tokens = Regex.Matches(d, @"([MmLlHhVvCcSsQqTtAaZz])|(-?\d*\.?\d+(?:[eE][-+]?\d+)?)")
                              .Cast<Match>().Select(m => m.Value).ToList();

            int i = 0;
            double curX = 0, curY = 0, startX = 0, startY = 0;
            double lastCx = 0, lastCy = 0; // 직전 큐빅 제어점 (S용)
            double lastQx = 0, lastQy = 0; // 직전 쿼드 제어점 (T용)
            char prevCmd = ' ';
            PolyCurve cur = null;

            Func<string> Next = () => i < tokens.Count ? tokens[i++] : null;
            Func<bool> HasNum = () =>
                i < tokens.Count && !Regex.IsMatch(tokens[i], "^[A-Za-z]$");
            Func<double> Num = () => D(tokens[i++]);

            Action flush = () =>
            {
                if (cur != null && cur.SegmentCount > 0) result.Add(cur);
                cur = null;
            };

            while (i < tokens.Count)
            {
                string t = tokens[i];
                char cmd;
                if (Regex.IsMatch(t, "^[A-Za-z]$")) { cmd = t[0]; i++; }
                else
                {
                    // 암묵적 명령 반복: M 다음은 L, m 다음은 l
                    if (prevCmd == 'M') cmd = 'L';
                    else if (prevCmd == 'm') cmd = 'l';
                    else cmd = prevCmd;
                }

                bool rel = char.IsLower(cmd);
                char up = char.ToUpper(cmd);

                switch (up)
                {
                    case 'M':
                    {
                        double x = Num(), y = Num();
                        if (rel) { x += curX; y += curY; }
                        flush();
                        cur = new PolyCurve();
                        curX = startX = x; curY = startY = y;
                        break;
                    }
                    case 'L':
                    {
                        double x = Num(), y = Num();
                        if (rel) { x += curX; y += curY; }
                        cur?.Append(new Line(Pt(curX, curY), Pt(x, y)));
                        curX = x; curY = y;
                        break;
                    }
                    case 'H':
                    {
                        double x = Num();
                        if (rel) x += curX;
                        cur?.Append(new Line(Pt(curX, curY), Pt(x, curY)));
                        curX = x;
                        break;
                    }
                    case 'V':
                    {
                        double y = Num();
                        if (rel) y += curY;
                        cur?.Append(new Line(Pt(curX, curY), Pt(curX, y)));
                        curY = y;
                        break;
                    }
                    case 'C':
                    {
                        double x1 = Num(), y1 = Num(), x2 = Num(), y2 = Num(), x = Num(), y = Num();
                        if (rel) { x1 += curX; y1 += curY; x2 += curX; y2 += curY; x += curX; y += curY; }
                        AppendCubic(cur, curX, curY, x1, y1, x2, y2, x, y);
                        lastCx = x2; lastCy = y2;
                        curX = x; curY = y;
                        break;
                    }
                    case 'S':
                    {
                        double x2 = Num(), y2 = Num(), x = Num(), y = Num();
                        if (rel) { x2 += curX; y2 += curY; x += curX; y += curY; }
                        double x1, y1;
                        if (prevCmd == 'C' || prevCmd == 'c' || prevCmd == 'S' || prevCmd == 's')
                        { x1 = 2 * curX - lastCx; y1 = 2 * curY - lastCy; }
                        else { x1 = curX; y1 = curY; }
                        AppendCubic(cur, curX, curY, x1, y1, x2, y2, x, y);
                        lastCx = x2; lastCy = y2;
                        curX = x; curY = y;
                        break;
                    }
                    case 'Q':
                    {
                        double x1 = Num(), y1 = Num(), x = Num(), y = Num();
                        if (rel) { x1 += curX; y1 += curY; x += curX; y += curY; }
                        AppendQuad(cur, curX, curY, x1, y1, x, y);
                        lastQx = x1; lastQy = y1;
                        curX = x; curY = y;
                        break;
                    }
                    case 'T':
                    {
                        double x = Num(), y = Num();
                        if (rel) { x += curX; y += curY; }
                        double x1, y1;
                        if (prevCmd == 'Q' || prevCmd == 'q' || prevCmd == 'T' || prevCmd == 't')
                        { x1 = 2 * curX - lastQx; y1 = 2 * curY - lastQy; }
                        else { x1 = curX; y1 = curY; }
                        AppendQuad(cur, curX, curY, x1, y1, x, y);
                        lastQx = x1; lastQy = y1;
                        curX = x; curY = y;
                        break;
                    }
                    case 'A':
                    {
                        double rx = Num(), ry = Num(), rot = Num();
                        double large = Num(), sweep = Num(), x = Num(), y = Num();
                        if (rel) { x += curX; y += curY; }
                        AppendArc(cur, curX, curY, rx, ry, rot, large != 0, sweep != 0, x, y);
                        curX = x; curY = y;
                        break;
                    }
                    case 'Z':
                    {
                        if (cur != null && (Math.Abs(curX - startX) > 1e-9 || Math.Abs(curY - startY) > 1e-9))
                            cur.Append(new Line(Pt(curX, curY), Pt(startX, startY)));
                        curX = startX; curY = startY;
                        break;
                    }
                }

                prevCmd = cmd;
            }

            flush();
            return result;
        }

        private static void AppendCubic(PolyCurve pc, double x0, double y0,
            double x1, double y1, double x2, double y2, double x3, double y3)
        {
            if (pc == null) return;
            var b = new BezierCurve(new[] { Pt(x0, y0), Pt(x1, y1), Pt(x2, y2), Pt(x3, y3) });
            pc.Append(b.ToNurbsCurve());
        }

        private static void AppendQuad(PolyCurve pc, double x0, double y0,
            double x1, double y1, double x2, double y2)
        {
            if (pc == null) return;
            var b = new BezierCurve(new[] { Pt(x0, y0), Pt(x1, y1), Pt(x2, y2) });
            pc.Append(b.ToNurbsCurve());
        }

        // SVG 타원 호(endpoint 표기)를 폴리라인으로 근사. (다음 단계에서 정확한 호로 개선 가능)
        private static void AppendArc(PolyCurve pc, double x0, double y0,
            double rx, double ry, double xRotDeg, bool largeArc, bool sweep, double x, double y)
        {
            if (pc == null) return;
            if (rx <= 0 || ry <= 0 || (Math.Abs(x0 - x) < 1e-12 && Math.Abs(y0 - y) < 1e-12))
            {
                pc.Append(new Line(Pt(x0, y0), Pt(x, y)));
                return;
            }

            double phi = xRotDeg * Math.PI / 180.0;
            double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);

            // 1) 회전 보정된 좌표계로 변환
            double dx2 = (x0 - x) / 2.0, dy2 = (y0 - y) / 2.0;
            double x1p = cosPhi * dx2 + sinPhi * dy2;
            double y1p = -sinPhi * dx2 + cosPhi * dy2;

            // 반지름 보정
            double rxs = rx * rx, rys = ry * ry;
            double lambda = (x1p * x1p) / rxs + (y1p * y1p) / rys;
            if (lambda > 1) { double s = Math.Sqrt(lambda); rx *= s; ry *= s; rxs = rx * rx; rys = ry * ry; }

            // 2) 중심 계산
            double sign = (largeArc != sweep) ? 1.0 : -1.0;
            double num = rxs * rys - rxs * y1p * y1p - rys * x1p * x1p;
            double den = rxs * y1p * y1p + rys * x1p * x1p;
            double co = sign * Math.Sqrt(Math.Max(0.0, num / den));
            double cxp = co * (rx * y1p) / ry;
            double cyp = co * -(ry * x1p) / rx;

            double cx = cosPhi * cxp - sinPhi * cyp + (x0 + x) / 2.0;
            double cy = sinPhi * cxp + cosPhi * cyp + (y0 + y) / 2.0;

            // 3) 시작각/스윕각
            double Angle(double ux, double uy, double vx, double vy)
            {
                double dot = ux * vx + uy * vy;
                double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
                double a = Math.Acos(Math.Min(1, Math.Max(-1, dot / len)));
                if (ux * vy - uy * vx < 0) a = -a;
                return a;
            }

            double theta1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
            double dTheta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
            if (!sweep && dTheta > 0) dTheta -= 2 * Math.PI;
            else if (sweep && dTheta < 0) dTheta += 2 * Math.PI;

            // 4) 샘플링
            int segs = Math.Max(4, (int)Math.Ceiling(Math.Abs(dTheta) / (Math.PI / 18))); // ~10도 간격
            var pts = new List<Point3d>();
            for (int k = 0; k <= segs; k++)
            {
                double th = theta1 + dTheta * k / segs;
                double ex = cosPhi * rx * Math.Cos(th) - sinPhi * ry * Math.Sin(th) + cx;
                double ey = sinPhi * rx * Math.Cos(th) + cosPhi * ry * Math.Sin(th) + cy;
                pts.Add(Pt(ex, ey));
            }
            pc.Append(new PolylineCurve(pts));
        }
    }
}
