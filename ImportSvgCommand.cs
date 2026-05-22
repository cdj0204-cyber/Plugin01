using System;
using System.IO;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Plugin01
{
    public class ImportSvgCommand : Command
    {
        public ImportSvgCommand()
        {
            Instance = this;
        }

        public static ImportSvgCommand Instance { get; private set; }

        public override string EnglishName => "Plugin01ImportSvg";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var dialog = new Rhino.UI.OpenFileDialog
            {
                Title = "패턴 SVG 파일 선택",
                Filter = "SVG files (*.svg)|*.svg"
            };

            if (!dialog.ShowOpenDialog())
                return Result.Cancel;

            string path = dialog.FileName;
            if (!File.Exists(path))
            {
                RhinoApp.WriteLine("파일을 찾을 수 없습니다: {0}", path);
                return Result.Failure;
            }

            System.Collections.Generic.List<Curve> curves;
            string report;
            try
            {
                curves = SvgImporter.Import(path, out report);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("SVG 파싱 실패: {0}", ex.Message);
                return Result.Failure;
            }

            RhinoApp.WriteLine(report);

            if (curves.Count == 0)
            {
                RhinoApp.WriteLine("SVG에서 변환 가능한 도형을 찾지 못했습니다. " +
                    "(요소가 transform/use/defs 안에 있거나, 미지원 요소일 수 있습니다.)");
                return Result.Nothing;
            }

            // 원점 정렬: 전체 바운딩박스 중심을 월드 원점(0,0)으로 이동
            var bbox = BoundingBox.Empty;
            foreach (var c in curves)
                bbox.Union(c.GetBoundingBox(true));

            if (bbox.IsValid)
            {
                var center = bbox.Center;
                var move = Transform.Translation(-center.X, -center.Y, 0.0);
                foreach (var c in curves)
                    c.Transform(move);
            }

            // 가져온 커브를 하나의 그룹으로 묶어 추가
            string groupName = Path.GetFileNameWithoutExtension(path) + "_svg";
            int groupIndex = doc.Groups.Add(groupName);

            var attr = new ObjectAttributes { Name = groupName };
            attr.AddToGroup(groupIndex);

            foreach (var c in curves)
                doc.Objects.AddCurve(c, attr);

            doc.Views.Redraw();

            RhinoApp.WriteLine("SVG 가져오기 완료: 커브 {0}개 추가됨.", curves.Count);
            if (bbox.IsValid)
            {
                var d = bbox.Diagonal;
                RhinoApp.WriteLine("패턴 크기(폭 x 높이): {0:0.##} x {1:0.##} {2}",
                    d.X, d.Y, doc.ModelUnitSystem);
            }

            return Result.Success;
        }
    }
}
