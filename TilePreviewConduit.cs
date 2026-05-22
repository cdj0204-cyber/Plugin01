using System.Collections.Generic;
using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;

namespace Plugin01
{
    /// <summary>
    /// 타일링 결과를 도큐먼트에 만들기 전, 뷰포트에 임시로 그려 보여주는 미리보기 오버레이.
    /// </summary>
    public class TilePreviewConduit : DisplayConduit
    {
        public List<Curve> Curves { get; set; } = new List<Curve>();
        public Color Color { get; set; } = Color.FromArgb(0, 160, 255);
        public int Thickness { get; set; } = 2;

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            foreach (var c in Curves)
                if (c != null) e.IncludeBoundingBox(c.GetBoundingBox(false));
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            foreach (var c in Curves)
                if (c != null) e.Display.DrawCurve(c, Color, Thickness);
        }
    }
}
