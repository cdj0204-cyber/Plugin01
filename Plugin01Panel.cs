using System;
using System.Collections.Generic;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;
using Rhino.Input;

namespace Plugin01
{
    /// <summary>
    /// 단계별 작업을 버튼/드롭다운으로 진행하는 플로팅 창.
    /// 1) SVG 불러오기  2) 대상 표면/패턴 선택  3) 타일링 방식 선택 후 실행.
    /// </summary>
    public class Plugin01Panel : Form
    {
        private readonly Label _lblImport = new Label { Text = "(없음)" };
        private readonly Label _lblSurface = new Label { Text = "(없음)" };
        private readonly Label _lblPattern = new Label { Text = "(불러온 SVG 사용)" };
        private readonly Label _lblStatus = new Label { Text = "" };

        private readonly CheckBox _autoConnect = new CheckBox { Text = "연결면 자동 선택 (끄면 면 직접 다중 선택)", Checked = true };
        private readonly DropDown _ddMode = new DropDown();
        private readonly NumericStepper _nu = new NumericStepper { Value = 10, MinValue = 1, MaxValue = 1000, DecimalPlaces = 0, Width = 60 };
        private readonly NumericStepper _nv = new NumericStepper { Value = 10, MinValue = 1, MaxValue = 1000, DecimalPlaces = 0, Width = 60 };

        private StackLayout _rowCounts;

        private List<Curve> _pattern = new List<Curve>();
        private Brep _targetBrep;
        private List<int> _faceIndices = new List<int>();

        private readonly TilePreviewConduit _preview = new TilePreviewConduit();

        public Plugin01Panel()
        {
            Title = "Plugin 01 — 패턴 천공";
            ClientSize = new Size(320, 440);
            Topmost = true;
            Maximizable = false;
            Minimizable = false;
            Resizable = true;

            var btnImport = new Button { Text = "SVG 불러오기" };
            btnImport.Click += OnImport;

            var btnSurface = new Button { Text = "대상 표면 선택" };
            btnSurface.Click += OnPickSurface;

            var btnPattern = new Button { Text = "패턴 커브 직접 선택(선택)" };
            btnPattern.Click += OnPickPattern;

            _ddMode.Items.Add("한 장 늘려 맞춤 (Stretch)");
            _ddMode.Items.Add("실제 크기 - 패턴 분석 적용 (RealSize)");
            _ddMode.Items.Add("반복 횟수 제어 (RepeatCount)");
            _ddMode.SelectedIndex = 0;
            _ddMode.SelectedIndexChanged += OnModeChanged;

            _rowCounts = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { new Label { Text = "U:" }, _nu, new Label { Text = "V:" }, _nv },
                Visible = false
            };

            var btnPreview = new Button { Text = "미리보기" };
            btnPreview.Click += OnPreview;

            var btnClear = new Button { Text = "미리보기 지우기" };
            btnClear.Click += OnClearPreview;

            var btnTile = new Button { Text = "타일링 실행 (확정)" };
            btnTile.Click += OnApply;

            Closed += (s, e) => DisablePreview();

            Content = new StackLayout
            {
                Padding = new Padding(12),
                Spacing = 8,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    Bold("1. SVG 패턴"),
                    btnImport,
                    _lblImport,
                    new Label { Text = " " },
                    Bold("2. 대상 / 패턴"),
                    _autoConnect,
                    btnSurface,
                    _lblSurface,
                    btnPattern,
                    _lblPattern,
                    new Label { Text = " " },
                    Bold("3. 타일링"),
                    new Label { Text = "방식" },
                    _ddMode,
                    _rowCounts,
                    btnPreview,
                    btnClear,
                    btnTile,
                    new Label { Text = " " },
                    _lblStatus
                }
            };
        }

        private static Label Bold(string text) =>
            new Label { Text = text, Font = SystemFonts.Bold() };

        private void SetStatus(string msg)
        {
            _lblStatus.Text = msg;
            RhinoApp.WriteLine("[Plugin01] " + msg);
        }

        private void OnModeChanged(object sender, EventArgs e)
        {
            _rowCounts.Visible = (_ddMode.SelectedIndex == 2);
        }

        private void OnImport(object sender, EventArgs e)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;

            var ofd = new OpenFileDialog { Title = "패턴 SVG 파일 선택" };
            ofd.Filters.Add(new FileFilter("SVG files", ".svg"));
            if (ofd.ShowDialog(this) != DialogResult.Ok) return;

            List<Curve> curves;
            try { curves = SvgImporter.Import(ofd.FileName); }
            catch (Exception ex) { SetStatus("SVG 파싱 실패: " + ex.Message); return; }

            if (curves.Count == 0) { SetStatus("변환 가능한 도형이 없습니다."); return; }

            // 원점 정렬
            var box = BoundingBox.Empty;
            foreach (var c in curves) box.Union(c.GetBoundingBox(true));
            if (box.IsValid)
            {
                var mv = Transform.Translation(-box.Center.X, -box.Center.Y, 0);
                foreach (var c in curves) c.Transform(mv);
            }

            int gi = doc.Groups.Add("svg_pattern");
            var attr = new ObjectAttributes { Name = "svg_pattern" };
            attr.AddToGroup(gi);
            foreach (var c in curves) doc.Objects.AddCurve(c, attr);
            doc.Views.Redraw();

            _pattern = curves;
            _lblImport.Text = $"패턴 {curves.Count}개 로드됨";
            SetStatus("SVG 불러오기 완료");
        }

        private void OnPickSurface(object sender, EventArgs e)
        {
            bool auto = _autoConnect.Checked == true;

            var go = new GetObject();
            go.SetCommandPrompt(auto ? "대상 면 선택 (연결면 자동 수집)" : "대상 면들 직접 선택 (여러 개)");
            go.GeometryFilter = ObjectType.Surface;
            go.SubObjectSelect = true;
            go.EnablePreSelect(false, true);

            GetResult res = auto ? go.Get() : go.GetMultiple(1, 0);
            if (res != GetResult.Object) { SetStatus("표면 선택 취소"); return; }

            var first = go.Object(0).Face();
            if (first == null || first.Brep == null)
            {
                _targetBrep = null; _faceIndices.Clear();
                _lblSurface.Text = "표면 가져오기 실패";
                SetStatus("표면(BrepFace) 가져오기 실패");
                return;
            }

            _targetBrep = first.Brep.DuplicateBrep();

            if (auto)
            {
                // 클릭한 면에서 탄젠트(G1+)로 이어진 면들을 자동 수집
                double angleTol = RhinoDoc.ActiveDoc?.ModelAngleToleranceRadians ?? RhinoMath.ToRadians(1);
                _faceIndices = FaceGrouping.GrowTangent(_targetBrep, first.FaceIndex, angleTol);
                _lblSurface.Text = $"표면 선택됨 (자동 연결면 {_faceIndices.Count}개)";
                SetStatus($"표면 선택 완료 — 탄젠트 연결면 {_faceIndices.Count}개");
            }
            else
            {
                // 같은 객체(Brep)에서 직접 선택한 면들만 사용
                System.Guid id = go.Object(0).ObjectId;
                var idx = new List<int>();
                for (int i = 0; i < go.ObjectCount; i++)
                {
                    var oref = go.Object(i);
                    if (oref.ObjectId != id) continue; // 다른 객체 면은 무시
                    var f = oref.Face();
                    if (f != null && !idx.Contains(f.FaceIndex)) idx.Add(f.FaceIndex);
                }
                _faceIndices = idx;
                _lblSurface.Text = $"표면 선택됨 (직접 선택 {_faceIndices.Count}개)";
                SetStatus($"표면 선택 완료 — 직접 선택 면 {_faceIndices.Count}개");
            }
        }

        private void OnPickPattern(object sender, EventArgs e)
        {
            var gc = new GetObject();
            gc.SetCommandPrompt("패턴 커브 선택");
            gc.GeometryFilter = ObjectType.Curve;
            gc.EnablePreSelect(false, true);
            if (gc.GetMultiple(1, 0) != GetResult.Object) { SetStatus("패턴 선택 취소"); return; }

            var list = new List<Curve>();
            for (int i = 0; i < gc.ObjectCount; i++)
            {
                var c = gc.Object(i).Curve();
                if (c != null) list.Add(c.DuplicateCurve());
            }
            if (list.Count == 0) { SetStatus("유효한 커브가 없습니다."); return; }

            _pattern = list;
            _lblPattern.Text = $"패턴 커브 {list.Count}개 직접 선택됨";
            SetStatus("패턴 커브 선택 완료");
        }

        // 타일링 결과 커브 계산 (도큐먼트엔 추가하지 않음). 실패 시 null 반환.
        private List<Curve> ComputeTiling()
        {
            if (_targetBrep == null || _faceIndices.Count == 0) { SetStatus("먼저 대상 표면을 선택하세요."); return null; }
            if (_pattern == null || _pattern.Count == 0) { SetStatus("먼저 패턴을 불러오거나 선택하세요."); return null; }

            var pBox = BoundingBox.Empty;
            foreach (var c in _pattern) pBox.Union(c.GetBoundingBox(true));
            if (!pBox.IsValid) { SetStatus("패턴 경계 계산 실패"); return null; }

            int m = _ddMode.SelectedIndex;
            var all = new List<Curve>();

            // 실제 크기 - 패턴 분석 적용 (연결면 전체에 적용)
            if (m == 1)
            {
                var info = PatternAnalyzer.Analyze(_pattern);
                if (!info.Valid)
                {
                    SetStatus("패턴 규칙 분석 실패 (간격을 못 찾음). 격자형 패턴인지 확인하세요.");
                    return null;
                }
                try
                {
                    foreach (int fi in _faceIndices)
                        all.AddRange(SurfaceTiler.TileRealSize(_targetBrep.Faces[fi], info));
                    SetStatus($"분석: 셀 {info.CellW:0.#}x{info.CellH:0.#}, 간격 {info.PitchU:0.#}x{info.PitchV:0.#} / 면 {_faceIndices.Count}개 → 셀 {all.Count}개");
                    return all;
                }
                catch (Exception ex) { SetStatus("배치 실패: " + ex.Message); return null; }
            }

            // Stretch / RepeatCount (UV 기반) — 연결면 각각에 적용
            double pw = pBox.Max.X - pBox.Min.X;
            double ph = pBox.Max.Y - pBox.Min.Y;

            int nU = 1, nV = 1;
            if (m == 2)
            {
                nU = Math.Max(1, (int)_nu.Value);
                nV = Math.Max(1, (int)_nv.Value);
            }

            long est = (long)_pattern.Count * nU * nV * _faceIndices.Count;
            if (est > 30000)
            {
                var r = MessageBox.Show(this, $"커브 약 {est}개가 생성됩니다. 계속할까요?",
                    "확인", MessageBoxButtons.YesNo, MessageBoxType.Question);
                if (r != DialogResult.Yes) return null;
            }

            double chord = Math.Max(pw, ph) / 80.0;
            try
            {
                // 같은 바탕 곡면을 공유하는 면들끼리 묶기
                var groups = new Dictionary<int, List<BrepFace>>();
                foreach (int fi in _faceIndices)
                {
                    var f = _targetBrep.Faces[fi];
                    int si = f.SurfaceIndex;
                    if (!groups.ContainsKey(si)) groups[si] = new List<BrepFace>();
                    groups[si].Add(f);
                }

                if (groups.Count == 1)
                {
                    // 단일 곡면 공유(구 등): 그 곡면의 연속 UV로 한 번에 stretch
                    List<BrepFace> grp = null;
                    foreach (var g in groups.Values) { grp = g; break; }
                    var srf = grp[0].UnderlyingSurface();
                    Interval uReg, vReg;
                    SurfaceTiler.CombinedUvRegion(grp, out uReg, out vReg);
                    all.AddRange(SurfaceTiler.TileRegion(srf, grp, uReg, vReg, _pattern, pBox, nU, nV, chord));
                }
                else
                {
                    // 여러 바탕 곡면(예: 필렛 박스): 전개(unroll)해서 하나의 연속 패턴으로 깐다
                    var draped = SurfaceDraper.Drape(_targetBrep, _faceIndices, _pattern, pBox, nU, nV);
                    if (draped != null)
                    {
                        all.AddRange(draped);
                    }
                    else
                    {
                        // 전개 불가(이중곡면 다면 등): 면별로 폴백
                        SetStatus("전개 불가 형상 — 면별 적용으로 폴백합니다.");
                        foreach (var grp in groups.Values)
                        {
                            var srf = grp[0].UnderlyingSurface();
                            Interval uReg, vReg;
                            SurfaceTiler.CombinedUvRegion(grp, out uReg, out vReg);
                            all.AddRange(SurfaceTiler.TileRegion(srf, grp, uReg, vReg, _pattern, pBox, nU, nV, chord));
                        }
                    }
                }
                return all;
            }
            catch (Exception ex) { SetStatus("타일링 실패: " + ex.Message); return null; }
        }

        private void OnPreview(object sender, EventArgs e)
        {
            var tiled = ComputeTiling();
            if (tiled == null) return;
            if (tiled.Count == 0) { SetStatus("생성된 커브가 없습니다."); return; }

            _preview.Curves = tiled;
            _preview.Enabled = true;
            RhinoDoc.ActiveDoc?.Views.Redraw();
            SetStatus($"미리보기 표시 (확정 전): 커브 {tiled.Count}개");
        }

        private void OnClearPreview(object sender, EventArgs e)
        {
            DisablePreview();
            SetStatus("미리보기 지움");
        }

        private void DisablePreview()
        {
            _preview.Enabled = false;
            _preview.Curves = new List<Curve>();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        private void OnApply(object sender, EventArgs e)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;

            // 미리보기가 켜져 있으면 그걸 그대로 확정, 아니면 새로 계산
            List<Curve> tiled = (_preview.Enabled && _preview.Curves.Count > 0)
                ? _preview.Curves
                : ComputeTiling();

            if (tiled == null) return;
            if (tiled.Count == 0) { SetStatus("생성된 커브가 없습니다."); return; }

            int gi = doc.Groups.Add("tiled_pattern");
            var attr = new ObjectAttributes { Name = "tiled_pattern" };
            attr.AddToGroup(gi);
            foreach (var c in tiled) doc.Objects.AddCurve(c, attr);

            DisablePreview();
            doc.Views.Redraw();
            SetStatus($"타일링 확정: 커브 {tiled.Count}개 생성");
        }
    }
}
