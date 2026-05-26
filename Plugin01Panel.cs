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
        private readonly NumericStepper _nu = new NumericStepper { Value = 1, MinValue = 1, MaxValue = 1000, DecimalPlaces = 0, Width = 60 };
        private readonly NumericStepper _nv = new NumericStepper { Value = 1, MinValue = 1, MaxValue = 1000, DecimalPlaces = 0, Width = 60 };
        private readonly NumericStepper _margin = new NumericStepper { Value = 0, MinValue = 0, DecimalPlaces = 2, Increment = 0.5, Width = 80 };
        private readonly NumericStepper _uOff = new NumericStepper { Value = 0, DecimalPlaces = 2, Increment = 1.0, Width = 70 };
        private readonly NumericStepper _vOff = new NumericStepper { Value = 0, DecimalPlaces = 2, Increment = 1.0, Width = 70 };
        private readonly NumericStepper _rotDeg = new NumericStepper { Value = 0, DecimalPlaces = 1, Increment = 5.0, Width = 70 };
        private readonly NumericStepper _scalePct = new NumericStepper { Value = 100, MinValue = 1, MaxValue = 1000, DecimalPlaces = 1, Increment = 10, Width = 70 };
        private readonly NumericStepper _rotDegR = new NumericStepper { Value = 0, DecimalPlaces = 1, Increment = 5.0, Width = 70 };
        private readonly CheckBox _flipH = new CheckBox { Text = "좌우 반전" };
        private readonly CheckBox _flipV = new CheckBox { Text = "상하 반전" };
        private readonly NumericStepper _rotDegS = new NumericStepper { Value = 0, DecimalPlaces = 1, Increment = 5.0, Width = 70 };
        private StackLayout _rowPartial;
        private StackLayout _rowRealSize;
        private StackLayout _rowFlips;

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
            _ddMode.Items.Add("실제 크기 - 패턴 부분적용 (PartialFit)");
            _ddMode.SelectedIndex = 0;
            _ddMode.SelectedIndexChanged += OnModeChanged;

            _rowCounts = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { new Label { Text = "반복 U:" }, _nu, new Label { Text = "V:" }, _nv },
                Visible = true
            };

            _rowFlips = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { _flipH, _flipV, new Label { Text = "회전°:" }, _rotDegS },
                Visible = true
            };

            _rowPartial = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { new Label { Text = "U(mm):" }, _uOff, new Label { Text = "V(mm):" }, _vOff, new Label { Text = "회전°:" }, _rotDeg, new Label { Text = "크기%:" }, _scalePct },
                Visible = false
            };

            _rowRealSize = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { new Label { Text = "회전°:" }, _rotDegR },
                Visible = false
            };

            var rowMargin = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { new Label { Text = "외곽선 마진(mm):" }, _margin }
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
                    _rowFlips,
                    _rowRealSize,
                    _rowPartial,
                    rowMargin,
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
            int m = _ddMode.SelectedIndex;
            _rowCounts.Visible = (m == 0);
            _rowFlips.Visible = (m == 0);
            _rowRealSize.Visible = (m == 1);
            _rowPartial.Visible = (m == 2);
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

        // 연결면들을 하나의 면으로 보고 외곽선(naked edge) 곡선들 반환
        private static List<Curve> GetOuterBoundaryCurves(Brep brep, IList<int> faceIndices)
        {
            var curves = new List<Curve>();
            if (brep == null || faceIndices == null || faceIndices.Count == 0) return curves;
            var sub = brep.DuplicateSubBrep(faceIndices);
            if (sub == null) return curves;
            foreach (var edge in sub.Edges)
            {
                var adj = edge.AdjacentFaces();
                if (adj != null && adj.Length == 1) // 한 면에만 닿음 = 외곽선
                {
                    var crv = edge.DuplicateCurve();
                    if (crv != null) curves.Add(crv);
                }
            }
            return curves;
        }

        // 셀(폴리라인) 중심을 꼭짓점 평균으로 (곡면에서 바운딩박스 중심이 표면 안쪽으로 들어가는 문제 회피)
        private static Point3d CellCentroid(Curve c)
        {
            var pc = c as PolylineCurve;
            if (pc != null && pc.PointCount > 0)
            {
                double sx = 0, sy = 0, sz = 0;
                int n = pc.PointCount;
                for (int i = 0; i < n; i++)
                {
                    var p = pc.Point(i);
                    sx += p.X; sy += p.Y; sz += p.Z;
                }
                return new Point3d(sx / n, sy / n, sz / n);
            }
            return c.GetBoundingBox(false).Center;
        }

        // 외곽선 마진 필터: 셀 중심이 외곽선들로부터 margin 이상 떨어진 셀만 남김
        private List<Curve> ApplyMarginFilter(List<Curve> curves)
        {
            double margin = Math.Max(0, _margin.Value);
            if (margin <= 1e-9 || curves == null || curves.Count == 0) return curves;
            var bc = GetOuterBoundaryCurves(_targetBrep, _faceIndices);
            RhinoApp.WriteLine($"[Margin] margin={margin:0.##}, boundary curves={bc.Count}, cells before={curves.Count}");
            if (bc.Count == 0) return curves;

            var filtered = new List<Curve>(curves.Count);
            foreach (var c in curves)
            {
                // 셀의 모든 꼭짓점 중 외곽선과의 최소 거리 찾기
                double minD = double.MaxValue;
                var pc = c as PolylineCurve;
                if (pc != null)
                {
                    for (int i = 0; i < pc.PointCount; i++)
                    {
                        var pt = pc.Point(i);
                        foreach (var bcurve in bc)
                        {
                            double t;
                            if (bcurve.ClosestPoint(pt, out t))
                            {
                                double d = bcurve.PointAt(t).DistanceTo(pt);
                                if (d < minD) minD = d;
                            }
                        }
                    }
                }
                else
                {
                    var center = CellCentroid(c);
                    foreach (var bcurve in bc)
                    {
                        double t;
                        if (bcurve.ClosestPoint(center, out t))
                        {
                            double d = bcurve.PointAt(t).DistanceTo(center);
                            if (d < minD) minD = d;
                        }
                    }
                }
                // 셀의 모든 부분이 마진 밖이어야 채택
                if (minD >= margin) filtered.Add(c);
            }
            RhinoApp.WriteLine($"[Margin] cells after={filtered.Count}");
            return filtered;
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

            // 실제 크기 - 패턴 분석 적용 (BFS 위상 전달로 면 간 연속성 유지)
            if (m == 1)
            {
                var info = PatternAnalyzer.Analyze(_pattern);
                if (!info.Valid)
                {
                    SetStatus("패턴 규칙 분석 실패 (간격을 못 찾음). 격자형 패턴인지 확인하세요.");
                    return null;
                }

                // seed 면의 du를 세계 좌표 기준으로 사용
                Vector3d refDirR = Vector3d.Zero;
                var seedFaceR = _targetBrep.Faces[_faceIndices[0]];
                {
                    var sd0 = seedFaceR.Domain(0); var sd1 = seedFaceR.Domain(1);
                    Point3d sp; Vector3d[] sders;
                    if (seedFaceR.Evaluate(sd0.ParameterAt(0.5), sd1.ParameterAt(0.5), 1, out sp, out sders) && sders != null && sders.Length >= 1)
                    {
                        refDirR = sders[0];
                        refDirR.Unitize();
                    }
                }
                double angleTolR = RhinoDoc.ActiveDoc?.ModelAngleToleranceRadians ?? Rhino.RhinoMath.ToRadians(1);

                try
                {
                    all.AddRange(SurfaceTiler.TileConnectedRealSizeFit(_targetBrep, _faceIndices, info, refDirR, angleTolR, _rotDegR.Value));
                    all = ApplyMarginFilter(all);
                    SetStatus($"분석: 셀 {info.CellW:0.#}x{info.CellH:0.#}, 회전 {_rotDegR.Value:0.#}° → 셀 {all.Count}개");
                    return all;
                }
                catch (Exception ex) { SetStatus("배치 실패: " + ex.Message); return null; }
            }

            // 실제 크기 - 패턴 부분적용 (m == 2): 패턴 한 묶음을 자유 배치
            if (m == 2)
            {
                Vector3d refDirP = Vector3d.Zero;
                var seedFaceP = _targetBrep.Faces[_faceIndices[0]];
                {
                    var sd0 = seedFaceP.Domain(0); var sd1 = seedFaceP.Domain(1);
                    Point3d sp; Vector3d[] sders;
                    if (seedFaceP.Evaluate(sd0.ParameterAt(0.5), sd1.ParameterAt(0.5), 1, out sp, out sders) && sders != null && sders.Length >= 1)
                    {
                        refDirP = sders[0];
                        refDirP.Unitize();
                    }
                }
                double angleTolP = RhinoDoc.ActiveDoc?.ModelAngleToleranceRadians ?? Rhino.RhinoMath.ToRadians(1);

                try
                {
                    double scale = Math.Max(0.0001, _scalePct.Value / 100.0);
                    all.AddRange(SurfaceTiler.TileConnectedPartial(_targetBrep, _faceIndices, _pattern, pBox, refDirP, angleTolP, _uOff.Value, _vOff.Value, _rotDeg.Value, scale));
                    all = ApplyMarginFilter(all);
                    SetStatus($"부분 적용: U={_uOff.Value:0.#} V={_vOff.Value:0.#} 회전={_rotDeg.Value:0.#}° 크기={_scalePct.Value:0.#}% → 셀 {all.Count}개");
                    return all;
                }
                catch (Exception ex) { SetStatus("배치 실패: " + ex.Message); return null; }
            }

            // Stretch (m == 0): 기본 nU=nV=1, 반복 횟수 옵션으로 조절 가능
            double pw = pBox.Max.X - pBox.Min.X;
            double ph = pBox.Max.Y - pBox.Min.Y;

            int nU = Math.Max(1, (int)_nu.Value);
            int nV = Math.Max(1, (int)_nv.Value);

            long est = (long)_pattern.Count * nU * nV * _faceIndices.Count;
            if (est > 30000)
            {
                var r = MessageBox.Show(this, $"커브 약 {est}개가 생성됩니다. 계속할까요?",
                    "확인", MessageBoxButtons.YesNo, MessageBoxType.Question);
                if (r != DialogResult.Yes) return null;
            }

            double chord = Math.Max(pw, ph) / 80.0;
            double marginMm = Math.Max(0, _margin.Value);
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
                    all.AddRange(SurfaceTiler.TileRegion(srf, grp, uReg, vReg, _pattern, pBox, nU, nV, chord, marginMm, _flipH.Checked == true, _flipV.Checked == true, _rotDegS.Value));
                }
                else
                {
                    // 여러 바탕 곡면(필렛 박스 등): 패턴 N개를 그대로 영역에 매핑 (BFS 위상 + 격자)
                    Vector3d refDir = Vector3d.Zero;
                    var seedFace = _targetBrep.Faces[_faceIndices[0]];
                    {
                        var sd0 = seedFace.Domain(0); var sd1 = seedFace.Domain(1);
                        Point3d sp; Vector3d[] sders;
                        if (seedFace.Evaluate(sd0.ParameterAt(0.5), sd1.ParameterAt(0.5), 1, out sp, out sders) && sders != null && sders.Length >= 1)
                        {
                            refDir = sders[0];
                            refDir.Unitize();
                        }
                    }

                    double angleTol = RhinoDoc.ActiveDoc?.ModelAngleToleranceRadians ?? Rhino.RhinoMath.ToRadians(1);
                    all.AddRange(SurfaceTiler.TileConnectedStretch(_targetBrep, _faceIndices, _pattern, pBox, refDir, angleTol, nU, nV, marginMm, _flipH.Checked == true, _flipV.Checked == true, _rotDegS.Value));

                    SetStatus($"한 장 늘려 맞춤(다면 연속, {nU}x{nV}): 패턴 {_pattern.Count}개 -> 커브 {all.Count}개");
                }
                return all; // stretch는 마진이 영역 인셋으로 이미 적용됨
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
