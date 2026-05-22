using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace Plugin01
{
    public class TilePatternCommand : Command
    {
        public TilePatternCommand()
        {
            Instance = this;
        }

        public static TilePatternCommand Instance { get; private set; }

        public override string EnglishName => "Plugin01Tile";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // 1) 대상 표면(또는 솔리드 면) 선택
            var gs = new GetObject();
            gs.SetCommandPrompt("타일링할 대상 표면(또는 솔리드 면) 선택");
            gs.GeometryFilter = ObjectType.Surface;
            gs.SubObjectSelect = true;
            gs.EnablePreSelect(false, true);
            if (gs.Get() != GetResult.Object) return Result.Cancel;

            Surface srf = gs.Object(0).Surface();
            if (srf == null)
            {
                RhinoApp.WriteLine("표면을 가져오지 못했습니다.");
                return Result.Failure;
            }

            // 2) 패턴 커브 선택
            var gc = new GetObject();
            gc.SetCommandPrompt("타일링할 패턴 커브 선택");
            gc.GeometryFilter = ObjectType.Curve;
            gc.EnablePreSelect(false, true);
            if (gc.GetMultiple(1, 0) != GetResult.Object) return Result.Cancel;

            var pattern = new List<Curve>();
            var pBox = BoundingBox.Empty;
            for (int i = 0; i < gc.ObjectCount; i++)
            {
                var c = gc.Object(i).Curve();
                if (c == null) continue;
                var dup = c.DuplicateCurve();
                pattern.Add(dup);
                pBox.Union(dup.GetBoundingBox(true));
            }
            if (pattern.Count == 0 || !pBox.IsValid)
            {
                RhinoApp.WriteLine("유효한 패턴 커브가 없습니다.");
                return Result.Failure;
            }

            double pw = pBox.Max.X - pBox.Min.X;
            double ph = pBox.Max.Y - pBox.Min.Y;

            // 3) 타일링 방식 선택 (옵션)
            var go = new GetOption();
            go.SetCommandPrompt("타일링 방식 선택");
            int oStretch = go.AddOption("StretchToFit");   // 한 장 늘려 맞춤
            int oSize    = go.AddOption("RealSize");        // 실제 크기로 반복
            int oCount   = go.AddOption("RepeatCount");     // 반복 횟수 제어
            if (go.Get() != GetResult.Option) return Result.Cancel;
            int chosen = go.OptionIndex();

            int nU = 1, nV = 1;

            if (chosen == oStretch)
            {
                nU = 1; nV = 1;
            }
            else if (chosen == oSize)
            {
                double cell = 10.0;
                if (RhinoGet.GetNumber("패턴 한 칸(타일)의 실제 가로 크기", false, ref cell) != Result.Success)
                    return Result.Cancel;
                if (cell <= 1e-6) { RhinoApp.WriteLine("크기는 0보다 커야 합니다."); return Result.Failure; }

                double sw = 0, sh = 0;
                if (!srf.GetSurfaceSize(out sw, out sh))
                {
                    RhinoApp.WriteLine("표면 크기를 측정하지 못했습니다.");
                    return Result.Failure;
                }
                double tileW = cell;
                double tileH = cell * (ph / pw); // 패턴 비율 유지
                nU = Math.Max(1, (int)Math.Round(sw / tileW));
                nV = Math.Max(1, (int)Math.Round(sh / tileH));
                RhinoApp.WriteLine($"표면 크기 ~{sw:0.#} x {sh:0.#} -> 반복 {nU} x {nV}");
            }
            else if (chosen == oCount)
            {
                int u = 10, v = 10;
                if (RhinoGet.GetInteger("U 방향 반복 횟수", false, ref u, 1, 1000) != Result.Success) return Result.Cancel;
                if (RhinoGet.GetInteger("V 방향 반복 횟수", false, ref v, 1, 1000) != Result.Success) return Result.Cancel;
                nU = Math.Max(1, u); nV = Math.Max(1, v);
            }

            // 4) 생성량 가드 (CPU/응답성 보호)
            long estimate = (long)pattern.Count * nU * nV;
            if (estimate > 30000)
            {
                bool proceed = false;
                if (RhinoGet.GetBool($"커브 약 {estimate}개가 생성됩니다. 계속할까요?", false, "Cancel", "Proceed", ref proceed) != Result.Success)
                    return Result.Cancel;
                if (!proceed) return Result.Cancel;
            }

            // 5) 타일링 실행
            double chord = Math.Max(pw, ph) / 80.0;
            List<Curve> tiled;
            try
            {
                tiled = SurfaceTiler.Tile(srf, pattern, pBox, nU, nV, chord);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("타일링 실패: {0}", ex.Message);
                return Result.Failure;
            }

            if (tiled.Count == 0)
            {
                RhinoApp.WriteLine("생성된 커브가 없습니다.");
                return Result.Nothing;
            }

            // 6) 그룹으로 추가
            int groupIndex = doc.Groups.Add("tiled_pattern");
            var attr = new ObjectAttributes { Name = "tiled_pattern" };
            attr.AddToGroup(groupIndex);

            foreach (var c in tiled)
                doc.Objects.AddCurve(c, attr);

            doc.Views.Redraw();
            RhinoApp.WriteLine($"타일링 완료: {nU} x {nV} 반복, 커브 {tiled.Count}개 생성.");
            return Result.Success;
        }
    }
}
