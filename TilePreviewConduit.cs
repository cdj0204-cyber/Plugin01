using System.Collections.Generic;
using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;

namespace Plugin01
{
    /// <summary>
    /// 타일링/천공 결과를 도큐먼트에 만들기 전, 뷰포트에 임시로 그려 보여주는 미리보기 오버레이.
    /// </summary>
    public class TilePreviewConduit : DisplayConduit
    {
        public List<Curve> Curves { get; set; } = new List<Curve>();
        public Color Color { get; set; } = Color.FromArgb(0, 160, 255);
        public int Thickness { get; set; } = 2;

        // 커터 미리보기용
        public List<Brep> Breps { get; set; } = new List<Brep>();
        public Color BrepEdgeColor { get; set; } = Color.FromArgb(255, 100, 0);
        public Color BrepFillColor { get; set; } = Color.FromArgb(80, 255, 140, 0); // 반투명 주황
        public int BrepEdgeThickness { get; set; } = 1;

        // 선택 면 outline (target surface)
        public List<Curve> TargetOutline { get; set; } = new List<Curve>();
        public Color TargetOutlineColor { get; set; } = Color.FromArgb(60, 200, 100); // 초록
        public int TargetOutlineThickness { get; set; } = 6;

        // 천공 벽면 outline
        public List<Curve> PunchOutline { get; set; } = new List<Curve>();
        public Color PunchOutlineColor { get; set; } = Color.FromArgb(255, 180, 50); // 주황
        public int PunchOutlineThickness { get; set; } = 6;

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            foreach (var c in Curves)
                if (c != null) e.IncludeBoundingBox(c.GetBoundingBox(false));
            foreach (var b in Breps)
                if (b != null) e.IncludeBoundingBox(b.GetBoundingBox(false));
            foreach (var c in TargetOutline)
                if (c != null) e.IncludeBoundingBox(c.GetBoundingBox(false));
            foreach (var c in PunchOutline)
                if (c != null) e.IncludeBoundingBox(c.GetBoundingBox(false));
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            // 선택 면 outline (항상 표시)
            foreach (var c in TargetOutline)
                if (c != null) e.Display.DrawCurve(c, TargetOutlineColor, TargetOutlineThickness);
            foreach (var c in PunchOutline)
                if (c != null) e.Display.DrawCurve(c, PunchOutlineColor, PunchOutlineThickness);

            foreach (var c in Curves)
                if (c != null) e.Display.DrawCurve(c, Color, Thickness);

            if (Breps.Count > 0)
            {
                var mat = new DisplayMaterial(BrepFillColor) { Transparency = 0.6 };
                foreach (var b in Breps)
                {
                    if (b == null) continue;
                    e.Display.DrawBrepShaded(b, mat);
                    e.Display.DrawBrepWires(b, BrepEdgeColor, BrepEdgeThickness);
                }
            }
        }
    }
}
