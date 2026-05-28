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
        private readonly Label _lblPunchDir = new Label { Text = "방향: World Z (기본)" };
        private readonly Label _lblPunchCurves = new Label { Text = "(마지막 타일링 결과 사용)" };
        private readonly CheckBox _wallOnly = new CheckBox { Text = "선택 벽면만 천공 (반대편 벽 보호)", Checked = true };
        private readonly CheckBox _punchAutoConnect = new CheckBox { Text = "천공 벽면 연결면 자동 (끄면 면 직접 다중 선택)", Checked = true };
        private readonly Label _lblPunchFaces = new Label { Text = "천공 벽면: (미선택 → 대상 표면과 동일)" };
        private List<int> _punchFaceIndices = new List<int>();
        private readonly NumericStepper _safetyStart = new NumericStepper { Value = 1.0, MinValue = 0.0, MaxValue = 1000, DecimalPlaces = 1, Increment = 0.1, Width = 80 };
        private readonly NumericStepper _safetyEnd = new NumericStepper { Value = 1.0, MinValue = 0.0, MaxValue = 1000, DecimalPlaces = 1, Increment = 0.1, Width = 80 };
        private readonly NumericStepper _draftDeg = new NumericStepper { Value = 0.0, MinValue = -30.0, MaxValue = 30.0, DecimalPlaces = 1, Increment = 0.1, Width = 80 };
        private readonly NumericStepper _tiltDeg = new NumericStepper { Value = 0, MinValue = 0, MaxValue = 180, DecimalPlaces = 2, Increment = 1.0, Width = 80 };
        private readonly NumericStepper _aziDeg = new NumericStepper { Value = 0, MinValue = -360, MaxValue = 360, DecimalPlaces = 2, Increment = 5.0, Width = 80 };
        private bool _suppressAngleEvent = false;
        private List<Curve> _manualPunchCurves = null; // null이면 _lastTiledIds 사용

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
        private Guid _targetObjectId = Guid.Empty;       // 천공 대상 (실제 도큐먼트 객체)
        private List<int> _faceIndices = new List<int>();
        private List<Guid> _lastTiledIds = new List<Guid>(); // 마지막 타일링 확정 결과
        private Vector3d _punchDir = Vector3d.ZAxis;     // 관통 방향 (기본 World Z)

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

            var btnClearSurface = new Button { Text = "대상 표면 선택 해제" };
            btnClearSurface.Click += OnClearTargetSurface;

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

            var btnPickDir = new Button { Text = "관통 방향 지정" };
            btnPickDir.Click += OnPickDirection;

            _tiltDeg.ValueChanged += OnAngleChanged;
            _aziDeg.ValueChanged += OnAngleChanged;
            var rowAngles = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { new Label { Text = "기울기°(Z):" }, _tiltDeg, new Label { Text = "방위°(XY):" }, _aziDeg }
            };
            var btnPickPunchCurves = new Button { Text = "천공 커브 직접 선택 (선택)" };
            btnPickPunchCurves.Click += OnPickPunchCurves;
            var btnPickPunchFaces = new Button { Text = "천공 대상 벽면 선택" };
            btnPickPunchFaces.Click += OnPickPunchFaces;
            var btnClearPunchFaces = new Button { Text = "천공 벽면 선택 해제" };
            btnClearPunchFaces.Click += OnClearPunchFaces;
            var rowDraft = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = {
                    new Label { Text = "구배각도(°):" }, _draftDeg,
                    new Label { Text = "  (0=직선, +값=−방향단 넓음/+방향단 좁음, −값=반대)" }
                }
            };
            var rowSafety = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = {
                    new Label { Text = "커터 연장 시작단(mm):" }, _safetyStart,
                    new Label { Text = "끝단(mm):" }, _safetyEnd
                }
            };
            var btnPreviewCutters = new Button { Text = "커터 미리보기 (Boolean 전)" };
            btnPreviewCutters.Click += OnPreviewCutters;
            var btnClearCutterPreview = new Button { Text = "커터 미리보기 지우기" };
            btnClearCutterPreview.Click += OnClearPreview;
            var btnPunch = new Button { Text = "천공 실행" };
            btnPunch.Click += OnPunch;

            var btnPreview = new Button { Text = "미리보기" };
            btnPreview.Click += OnPreview;

            var btnInteractivePlace = new Button { Text = "패턴 위치 조절 (인터랙티브)" };
            btnInteractivePlace.Click += OnInteractivePlace;

            var btnClear = new Button { Text = "미리보기 지우기" };
            btnClear.Click += OnClearPreview;

            var btnTile = new Button { Text = "타일링 실행 (확정)" };
            btnTile.Click += OnApply;

            Closed += (s, e) => DisableAllPreview();

            var contentStack = new StackLayout
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
                    btnClearSurface,
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
                    btnInteractivePlace,
                    btnTile,
                    new Label { Text = " " },
                    _lblStatus,
                    new Label { Text = " " },
                    Bold("4. 천공"),
                    btnPickDir,
                    _lblPunchDir,
                    rowAngles,
                    _wallOnly,
                    _punchAutoConnect,
                    btnPickPunchFaces,
                    btnClearPunchFaces,
                    _lblPunchFaces,
                    btnPickPunchCurves,
                    _lblPunchCurves,
                    rowDraft,
                    rowSafety,
                    btnPreviewCutters,
                    btnClearCutterPreview,
                    btnPunch
                }
            };

            // 세로 스크롤 가능한 컨테이너로 감쌈 (창이 길어져도 모든 옵션 접근 가능)
            Content = new Scrollable
            {
                Border = BorderType.None,
                ExpandContentWidth = true,
                ExpandContentHeight = false,
                Content = contentStack
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
            _targetObjectId = go.Object(0).ObjectId;

            if (auto)
            {
                // 클릭한 면에서 탄젠트(G1+)로 이어진 면들을 자동 수집
                double angleTol = RhinoDoc.ActiveDoc?.ModelAngleToleranceRadians ?? RhinoMath.ToRadians(1);
                _faceIndices = FaceGrouping.GrowTangent(_targetBrep, first.FaceIndex, angleTol);
                _lblSurface.Text = $"표면 선택됨 (자동 연결면 {_faceIndices.Count}개)";
                SetStatus($"표면 선택 완료 — 탄젠트 연결면 {_faceIndices.Count}개");
                // outline 도 즉시 업데이트 (else 분기는 함수 끝에서 함)
                UpdateTargetOutlinePreview();
                return;
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
            UpdateTargetOutlinePreview();
        }

        private void UpdateTargetOutlinePreview()
        {
            if (_targetBrep == null || _faceIndices == null || _faceIndices.Count == 0)
            {
                _preview.TargetOutline = new List<Curve>();
            }
            else
            {
                try { _preview.TargetOutline = GetOuterBoundaryCurves(_targetBrep, _faceIndices) ?? new List<Curve>(); }
                catch { _preview.TargetOutline = new List<Curve>(); }
            }
            _preview.Enabled = true;
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        private void UpdatePunchOutlinePreview()
        {
            if (_targetBrep == null || _punchFaceIndices == null || _punchFaceIndices.Count == 0)
            {
                _preview.PunchOutline = new List<Curve>();
            }
            else
            {
                try { _preview.PunchOutline = GetOuterBoundaryCurves(_targetBrep, _punchFaceIndices) ?? new List<Curve>(); }
                catch { _preview.PunchOutline = new List<Curve>(); }
            }
            _preview.Enabled = true;
            RhinoDoc.ActiveDoc?.Views.Redraw();
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
            _preview.Curves = new List<Curve>();
            _preview.Breps = new List<Brep>();
            // outline 은 보존: 선택 면이 살아 있는 동안 항상 보이게
            _preview.Enabled = (_preview.TargetOutline.Count > 0 || _preview.PunchOutline.Count > 0);
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        // 모든 미리보기 + outline 도 함께 끔 (창 닫을 때)
        private void DisableAllPreview()
        {
            _preview.Curves = new List<Curve>();
            _preview.Breps = new List<Brep>();
            _preview.TargetOutline = new List<Curve>();
            _preview.PunchOutline = new List<Curve>();
            _preview.Enabled = false;
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
            _lastTiledIds.Clear();
            foreach (var c in tiled)
            {
                var id = doc.Objects.AddCurve(c, attr);
                if (id != Guid.Empty) _lastTiledIds.Add(id);
            }

            DisablePreview();
            doc.Views.Redraw();
            SetStatus($"타일링 확정: 커브 {tiled.Count}개 생성");
        }

        // PartialFit 의 lattice anchor (seedSurf, Ti, Tj) 계산 — 인터랙티브 위치 조절에서 사용
        private bool ComputeLatticeAnchorForInteractive(out Point3d seedSurf, out Vector3d Ti_init, out Vector3d Tj_init)
        {
            seedSurf = Point3d.Origin;
            Ti_init = Vector3d.Zero;
            Tj_init = Vector3d.Zero;
            if (_targetBrep == null || _faceIndices == null || _faceIndices.Count == 0) return false;

            Vector3d avgN = Vector3d.Zero;
            Vector3d sumCenter = Vector3d.Zero;
            int validCount = 0;
            foreach (int fi in _faceIndices)
            {
                if (fi < 0 || fi >= _targetBrep.Faces.Count) continue;
                var face = _targetBrep.Faces[fi];
                var dom = face.Domain(0);
                var dom2 = face.Domain(1);
                double fuc = 0.5 * (dom.Min + dom.Max);
                double fvc = 0.5 * (dom2.Min + dom2.Max);
                Point3d c = ((Surface)face).PointAt(fuc, fvc);
                Vector3d du, dv;
                Point3d dummyPt;
                ((Surface)face).Evaluate(fuc, fvc, 1, out dummyPt, out var derivs);
                if (derivs == null || derivs.Length < 2) continue;
                du = derivs[0]; dv = derivs[1];
                if (du.Length < 1e-9 || dv.Length < 1e-9) continue;
                var n = Vector3d.CrossProduct(du, dv);
                if (n.Length < 1e-9) continue;
                n.Unitize();
                avgN += n;
                sumCenter += (Vector3d)c;
                validCount++;
            }
            if (validCount == 0) return false;
            Point3d centroidPt = new Point3d(sumCenter / validCount);
            if (avgN.Length < 1e-6) return false;
            avgN.Unitize();

            // avgN World 축 snap
            double absX = Math.Abs(avgN.X), absY = Math.Abs(avgN.Y), absZ = Math.Abs(avgN.Z);
            if (absZ > 0.9 && absZ >= absX && absZ >= absY) avgN = new Vector3d(0, 0, avgN.Z > 0 ? 1 : -1);
            else if (absY > 0.9 && absY >= absX && absY >= absZ) avgN = new Vector3d(0, avgN.Y > 0 ? 1 : -1, 0);
            else if (absX > 0.9 && absX >= absY && absX >= absZ) avgN = new Vector3d(avgN.X > 0 ? 1 : -1, 0, 0);

            // Ti, Tj 결정 — World Y 축 fallback
            Vector3d[] axes = { Vector3d.YAxis, Vector3d.XAxis, Vector3d.ZAxis };
            foreach (var axis in axes)
            {
                var proj = axis - (axis * avgN) * avgN;
                if (proj.Length > 1e-6) { proj.Unitize(); Ti_init = proj; break; }
            }
            if (Ti_init.Length < 1e-6) return false;
            Tj_init = Vector3d.CrossProduct(avgN, Ti_init);
            Tj_init.Unitize();

            // seedSurf = centroid 를 brep 에 snap
            Point3d closest;
            double s, t;
            ComponentIndex ci;
            Vector3d nrm;
            if (!_targetBrep.ClosestPoint(centroidPt, out closest, out ci, out s, out t, double.MaxValue, out nrm))
                return false;
            seedSurf = closest;
            return true;
        }

        // 카메라 → cursor 방향 ray 로 brep 표면 교차해서 화면 앞쪽 점 반환
        private Point3d? HitBrepFromCursor(Rhino.Input.Custom.GetPointMouseEventArgs em)
        {
            if (_targetBrep == null) return null;
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return null;
            var vp = em.Viewport;
            if (vp == null) return null;
            Point3d camera = vp.CameraLocation;
            Vector3d rayDir = em.Point - camera;
            if (rayDir.Length < 1e-9) return null;
            rayDir.Unitize();
            double farDist = _targetBrep.GetBoundingBox(true).Diagonal.Length * 10.0 + 1000.0;
            var rayLine = new Line(camera, camera + rayDir * farDist);
            try
            {
                var lineCurve = new LineCurve(rayLine);
                Curve[] overlap;
                Point3d[] hits;
                if (Rhino.Geometry.Intersect.Intersection.CurveBrep(lineCurve, _targetBrep,
                    doc.ModelAbsoluteTolerance, out overlap, out hits) && hits != null && hits.Length > 0)
                {
                    // 카메라에 가장 가까운 hit (화면 앞쪽)
                    Point3d best = hits[0];
                    double minD = best.DistanceTo(camera);
                    for (int i = 1; i < hits.Length; i++)
                    {
                        double d = hits[i].DistanceTo(camera);
                        if (d < minD) { minD = d; best = hits[i]; }
                    }
                    return best;
                }
            }
            catch { }
            // 실패시 ClosestPoint fallback
            Point3d cp; ComponentIndex ci; double s, t; Vector3d nn;
            if (_targetBrep.ClosestPoint(em.Point, out cp, out ci, out s, out t, double.MaxValue, out nn))
                return cp;
            return null;
        }

        private void OnInteractivePlace(object sender, EventArgs e)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            if (_ddMode.SelectedIndex != 2)
            {
                SetStatus("PartialFit 모드에서만 사용 가능합니다."); return;
            }
            if (_targetBrep == null || _faceIndices == null || _faceIndices.Count == 0)
            {
                SetStatus("먼저 대상 표면을 선택하세요."); return;
            }

            Point3d seedSurf;
            Vector3d Ti_init, Tj_init;
            if (!ComputeLatticeAnchorForInteractive(out seedSurf, out Ti_init, out Tj_init))
            {
                SetStatus("Lattice anchor 계산 실패"); return;
            }

            // 인터랙티브 모드 전용 recompute: cursor 위치를 patternCenter override 로 직접 전달
            // (slider 의 uOff/vOff 는 디스플레이용으로만 업데이트 — 실제 위치는 cursor 가 결정)
            if (_pattern == null || _pattern.Count == 0)
            {
                SetStatus("먼저 패턴을 불러오세요"); return;
            }
            BoundingBox pBoxL = BoundingBox.Empty;
            foreach (var pc in _pattern) pBoxL.Union(pc.GetBoundingBox(true));
            Vector3d refDirL = Vector3d.Zero;
            {
                var seedFaceL = _targetBrep.Faces[_faceIndices[0]];
                var sd0 = seedFaceL.Domain(0); var sd1 = seedFaceL.Domain(1);
                Point3d sp; Vector3d[] sders;
                if (seedFaceL.Evaluate(sd0.ParameterAt(0.5), sd1.ParameterAt(0.5), 1, out sp, out sders) && sders != null && sders.Length >= 1)
                {
                    refDirL = sders[0];
                    refDirL.Unitize();
                }
            }
            double angleTolL = RhinoDoc.ActiveDoc?.ModelAngleToleranceRadians ?? Rhino.RhinoMath.ToRadians(1);

            Func<Point3d?, List<Curve>> recompute = (Point3d? overrideCenter) =>
            {
                try
                {
                    double scaleL = Math.Max(0.0001, _scalePct.Value / 100.0);
                    var res = SurfaceTiler.TileConnectedPartial(_targetBrep, _faceIndices, _pattern, pBoxL,
                        refDirL, angleTolL, _uOff.Value, _vOff.Value, _rotDeg.Value, scaleL, overrideCenter);
                    res = ApplyMarginFilter(res ?? new List<Curve>());
                    return res;
                }
                catch { return new List<Curve>(); }
            };

            // === Phase 1: 위치 조절 ===
            var gp1 = new Rhino.Input.Custom.GetPoint();
            gp1.SetCommandPrompt("패턴 위치 클릭 (표면 위, Esc 취소)");

            List<Curve> dynPreview = recompute(null);
            Point3d lastSurfacePt = seedSurf;
            DateTime phase1ClickTime = DateTime.MinValue;
            DateTime phase2ClickTime = DateTime.MinValue;

            gp1.MouseDown += (sm, em) => { phase1ClickTime = DateTime.Now; };
            gp1.MouseMove += (sm, em) =>
            {
                Point3d? surfacePt = HitBrepFromCursor(em);
                if (!surfacePt.HasValue) return;
                lastSurfacePt = surfacePt.Value;
                Vector3d off = surfacePt.Value - seedSurf;
                double newU = off * Ti_init;
                double newV = off * Tj_init;
                _uOff.Value = newU;
                _vOff.Value = newV;
                // cursor 위치를 override 로 전달 → pattern center 가 cursor 와 정확히 일치
                dynPreview = recompute(surfacePt);
            };
            gp1.DynamicDraw += (sd, ed) =>
            {
                foreach (var c in dynPreview)
                    if (c != null) ed.Display.DrawCurve(c, System.Drawing.Color.FromArgb(0, 160, 255), 2);
            };

            var result1 = gp1.Get();
            if (result1 != Rhino.Input.GetResult.Point)
            {
                SetStatus("패턴 위치 조절 취소"); return;
            }
            var posPt = gp1.Point();

            // === Phase 2: 회전 조절 ===
            var gp2 = new Rhino.Input.Custom.GetPoint();
            gp2.SetCommandPrompt("회전 방향 클릭 (Enter 로 회전 유지하며 확정)");
            gp2.SetBasePoint(posPt, true);
            gp2.AcceptNothing(true);

            // 회전 기준 — posPt 의 로컬 tangent 평면 위 Ti, Tj 방향
            Point3d brepCp; double bs, bt; ComponentIndex bci; Vector3d bn;
            _targetBrep.ClosestPoint(posPt, out brepCp, out bci, out bs, out bt, double.MaxValue, out bn);
            // local tangent plane normal = surface normal at posPt
            Vector3d nLoc = bn;
            if (nLoc.Length < 1e-9) nLoc = Vector3d.CrossProduct(Ti_init, Tj_init);
            nLoc.Unitize();
            Vector3d Ti_loc = Ti_init - (Ti_init * nLoc) * nLoc;
            if (Ti_loc.Length < 1e-6) Ti_loc = Ti_init;
            Ti_loc.Unitize();
            Vector3d Tj_loc = Vector3d.CrossProduct(nLoc, Ti_loc);

            List<Curve> dynPreview2 = recompute(posPt);

            gp2.MouseDown += (sm, em) => { phase2ClickTime = DateTime.Now; };
            gp2.MouseMove += (sm, em) =>
            {
                Point3d aimPt = HitBrepFromCursor(em) ?? em.Point;
                Vector3d dir = aimPt - posPt;
                double dTi = dir * Ti_loc;
                double dTj = dir * Tj_loc;
                if (Math.Abs(dTi) < 1e-9 && Math.Abs(dTj) < 1e-9) return;
                double newRot = Math.Atan2(dTj, dTi) * 180.0 / Math.PI;
                _rotDeg.Value = newRot;
                // posPt 를 override 로 유지 (위치 고정, 회전만 변경)
                dynPreview2 = recompute(posPt);
            };
            gp2.DynamicDraw += (sd, ed) =>
            {
                foreach (var c in dynPreview2)
                    if (c != null) ed.Display.DrawCurve(c, System.Drawing.Color.FromArgb(0, 160, 255), 2);
                // 회전 기준선 (현재 방향) 시각화
                ed.Display.DrawLine(posPt, posPt + Ti_loc * 20, System.Drawing.Color.Red, 2);
            };

            var result2 = gp2.Get();

            // 최종 preview — cursor 위치 override 직접 사용 (slider snap 거치지 않음 → 인터랙티브 결과 그대로 유지)
            var finalCurves = recompute(posPt);
            _preview.Curves = finalCurves;
            _preview.Enabled = true;
            RhinoDoc.ActiveDoc?.Views.Redraw();

            // 더블클릭 감지: phase1 클릭 ↔ phase2 클릭 시간 차이 < 500ms 면 즉시 베이크
            bool wasDoubleClick = phase1ClickTime != DateTime.MinValue
                && phase2ClickTime != DateTime.MinValue
                && (phase2ClickTime - phase1ClickTime).TotalMilliseconds < 500;

            if (wasDoubleClick)
            {
                // 미리보기 그대로 베이크
                int gi = RhinoDoc.ActiveDoc.Groups.Add("tiled_pattern");
                var attr = new ObjectAttributes { Name = "tiled_pattern" };
                attr.AddToGroup(gi);
                _lastTiledIds.Clear();
                foreach (var c in finalCurves)
                {
                    var id = RhinoDoc.ActiveDoc.Objects.AddCurve(c, attr);
                    if (id != Guid.Empty) _lastTiledIds.Add(id);
                }
                DisablePreview();
                RhinoDoc.ActiveDoc.Views.Redraw();
                SetStatus($"더블클릭 → 즉시 베이크: {finalCurves.Count}개 cell. U={_uOff.Value:0.0} V={_vOff.Value:0.0} 회전={_rotDeg.Value:0.0}°");
            }
            else
            {
                SetStatus($"패턴 위치 조절 완료: U={_uOff.Value:0.0}mm V={_vOff.Value:0.0}mm 회전={_rotDeg.Value:0.0}° — 타일링 실행 으로 베이크");
            }
        }

        // ============================== 4. 천공 ==============================

        private void OnPickDirection(object sender, EventArgs e)
        {
            // 첫 점
            var gp1 = new Rhino.Input.Custom.GetPoint();
            gp1.SetCommandPrompt("관통 방향 시작점");
            if (gp1.Get() != GetResult.Point) { SetStatus("방향 지정 취소"); return; }
            Point3d p1 = gp1.Point();

            // 끝점: 시작점에서 커서까지 러버밴드 라인 표시
            var gp2 = new Rhino.Input.Custom.GetPoint();
            gp2.SetCommandPrompt("관통 방향 끝점");
            gp2.DrawLineFromPoint(p1, true);
            if (gp2.Get() != GetResult.Point) { SetStatus("방향 지정 취소"); return; }
            Point3d p2 = gp2.Point();

            var v = p2 - p1;
            if (!v.Unitize()) { SetStatus("방향 길이가 0입니다"); return; }
            _punchDir = v;
            SyncAnglesFromDir();
            UpdateDirLabel();
            SetStatus("관통 방향 설정 완료");
        }

        private void UpdateDirLabel()
        {
            _lblPunchDir.Text = $"방향: ({_punchDir.X:0.##}, {_punchDir.Y:0.##}, {_punchDir.Z:0.##})";
        }

        // 현재 _punchDir 로부터 기울기/방위 값을 갱신 (이벤트 억제)
        private void SyncAnglesFromDir()
        {
            double tilt = Math.Acos(Math.Max(-1.0, Math.Min(1.0, _punchDir.Z))) * 180.0 / Math.PI;
            double azi = Math.Atan2(_punchDir.Y, _punchDir.X) * 180.0 / Math.PI;
            _suppressAngleEvent = true;
            _tiltDeg.Value = tilt;
            _aziDeg.Value = azi;
            _suppressAngleEvent = false;
        }

        private void OnAngleChanged(object sender, EventArgs e)
        {
            if (_suppressAngleEvent) return;
            double tiltRad = _tiltDeg.Value * Math.PI / 180.0;
            double aziRad = _aziDeg.Value * Math.PI / 180.0;
            double sinT = Math.Sin(tiltRad), cosT = Math.Cos(tiltRad);
            var v = new Vector3d(sinT * Math.Cos(aziRad), sinT * Math.Sin(aziRad), cosT);
            if (v.Unitize())
            {
                _punchDir = v;
                UpdateDirLabel();
                SetStatus($"방향 갱신: 기울기 {_tiltDeg.Value:0.##}° 방위 {_aziDeg.Value:0.##}°");
            }
        }

        private void OnPickPunchFaces(object sender, EventArgs e)
        {
            if (_targetBrep == null)
            {
                SetStatus("먼저 '대상 표면 선택'을 해주세요.");
                return;
            }
            bool auto = _punchAutoConnect.Checked == true;

            var go = new GetObject();
            go.SetCommandPrompt(auto ? "천공 대상 벽면 선택 (연결면 자동 수집)" : "천공 대상 벽면들 직접 선택 (여러 개)");
            go.GeometryFilter = ObjectType.Surface;
            go.SubObjectSelect = true;
            go.EnablePreSelect(false, true);

            GetResult res = auto ? go.Get() : go.GetMultiple(1, 0);
            if (res != GetResult.Object) { SetStatus("천공 벽면 선택 취소"); return; }

            // 같은 brep 객체에서 선택해야 함
            if (go.Object(0).ObjectId != _targetObjectId)
            {
                SetStatus("대상 표면과 같은 객체에서 선택해야 합니다.");
                return;
            }

            var indices = new List<int>();
            if (auto)
            {
                var first = go.Object(0).Face();
                if (first == null) { SetStatus("면 가져오기 실패"); return; }
                double angleTol = RhinoDoc.ActiveDoc?.ModelAngleToleranceRadians ?? RhinoMath.ToRadians(1);
                indices = FaceGrouping.GrowTangent(_targetBrep, first.FaceIndex, angleTol);
                _lblPunchFaces.Text = $"천공 벽면: 자동 연결면 {indices.Count}개";
                SetStatus($"천공 벽면 {indices.Count}개 (자동 연결)");
            }
            else
            {
                for (int i = 0; i < go.ObjectCount; i++)
                {
                    var oref = go.Object(i);
                    if (oref.ObjectId != _targetObjectId) continue;
                    var f = oref.Face();
                    if (f != null && !indices.Contains(f.FaceIndex)) indices.Add(f.FaceIndex);
                }
                _lblPunchFaces.Text = $"천공 벽면: 직접 선택 {indices.Count}개";
                SetStatus($"천공 벽면 {indices.Count}개 (직접 선택)");
            }
            _punchFaceIndices = indices;
            UpdatePunchOutlinePreview();
        }

        private void OnClearPunchFaces(object sender, EventArgs e)
        {
            _punchFaceIndices = new List<int>();
            _lblPunchFaces.Text = "천공 벽면: (미선택 → 대상 표면과 동일)";
            SetStatus("천공 벽면 선택 해제됨");
            UpdatePunchOutlinePreview();
        }

        private void OnClearTargetSurface(object sender, EventArgs e)
        {
            _targetBrep = null;
            _targetObjectId = Guid.Empty;
            _faceIndices = new List<int>();
            _lblSurface.Text = "표면 선택 안됨";
            SetStatus("대상 표면 선택 해제됨");
            UpdateTargetOutlinePreview();
            // 천공 벽면이 대상 표면을 fallback 으로 쓰므로 그것도 함께 무효화 (선택된 게 없으니)
            UpdatePunchOutlinePreview();
        }

        private void OnPickPunchCurves(object sender, EventArgs e)
        {
            var gc = new GetObject();
            gc.SetCommandPrompt("천공에 사용할 폐곡선 선택");
            gc.GeometryFilter = ObjectType.Curve;
            gc.EnablePreSelect(false, true);
            if (gc.GetMultiple(1, 0) != GetResult.Object) { SetStatus("천공 커브 선택 취소"); return; }

            var list = new List<Curve>();
            for (int i = 0; i < gc.ObjectCount; i++)
            {
                var c = gc.Object(i).Curve();
                if (c != null && c.IsClosed) list.Add(c.DuplicateCurve());
            }
            if (list.Count == 0) { SetStatus("선택된 폐곡선이 없습니다"); return; }
            _manualPunchCurves = list;
            _lblPunchCurves.Text = $"직접 선택: {list.Count}개 (사용)";
            SetStatus($"천공 커브 {list.Count}개 직접 선택됨");
        }

        // 천공 입력(대상 brep + 커브들 + 옵션) 준비. 성공하면 true, 실패하면 상태에 메시지 남기고 false.
        private bool TryGetPunchInputs(out Brep targetBrep, out List<Curve> punchCurves, out List<int> punchFaces, out double tol, out bool wallOnly)
        {
            targetBrep = null; punchCurves = null; punchFaces = null; tol = 0; wallOnly = true;
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return false;
            if (_targetBrep == null || _targetObjectId == Guid.Empty)
            {
                SetStatus("먼저 대상 표면(솔리드 면)을 선택하세요."); return false;
            }

            punchCurves = _manualPunchCurves;
            if (punchCurves == null || punchCurves.Count == 0)
            {
                punchCurves = new List<Curve>();
                foreach (var id in _lastTiledIds)
                {
                    var obj = doc.Objects.FindId(id);
                    if (obj == null) continue;
                    var cr = obj.Geometry as Curve;
                    if (cr != null && cr.IsClosed) punchCurves.Add(cr.DuplicateCurve());
                }
            }
            if (punchCurves.Count == 0)
            {
                SetStatus("천공할 커브가 없습니다. 타일링 실행을 먼저 하거나 직접 선택하세요.");
                return false;
            }

            var targetObj = doc.Objects.FindId(_targetObjectId);
            if (targetObj == null) { SetStatus("대상 솔리드를 찾을 수 없습니다. 다시 선택하세요."); return false; }
            targetBrep = (targetObj.Geometry as Brep)?.DuplicateBrep();
            if (targetBrep == null) { SetStatus("대상이 솔리드(브렙)가 아닙니다."); return false; }

            tol = doc.ModelAbsoluteTolerance;
            wallOnly = _wallOnly.Checked ?? true;
            punchFaces = (_punchFaceIndices != null && _punchFaceIndices.Count > 0)
                ? _punchFaceIndices : _faceIndices;
            return true;
        }

        private void OnPreviewCutters(object sender, EventArgs e)
        {
            Brep targetBrep; List<Curve> punchCurves; List<int> punchFaces; double tol; bool wallOnly;
            if (!TryGetPunchInputs(out targetBrep, out punchCurves, out punchFaces, out tol, out wallOnly)) return;

            double safS = _safetyStart.Value;
            double safE = _safetyEnd.Value;
            double draft = _draftDeg.Value;

            Perforator.CutterBuildResult built;
            try
            {
                built = Perforator.BuildCutters(targetBrep, punchCurves, _punchDir, tol, wallOnly, punchFaces, safS, safE, draft);
            }
            catch (Exception ex) { SetStatus("커터 빌드 실패: " + ex.Message); return; }

            if (built.Cutters.Count == 0)
            {
                SetStatus($"커터 0개 (실패 {built.FailedCount}, 벽없음 {built.NoWallCount})");
                return;
            }
            _preview.Curves = new List<Curve>();
            _preview.Breps = built.Cutters;
            _preview.Enabled = true;
            RhinoDoc.ActiveDoc?.Views.Redraw();
            SetStatus($"커터 미리보기: {built.Cutters.Count}개 (폴백 {built.FallbackCount}, 실패 {built.FailedCount}, 벽없음 {built.NoWallCount}) — 시작 {safS:0.0}mm / 끝 {safE:0.0}mm / 구배 {draft:0.0}°");
        }

        private void OnPunch(object sender, EventArgs e)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;

            Brep targetBrep; List<Curve> punchCurves; List<int> punchFaces; double tol; bool wallOnly;
            if (!TryGetPunchInputs(out targetBrep, out punchCurves, out punchFaces, out tol, out wallOnly)) return;

            double safS = _safetyStart.Value;
            double safE = _safetyEnd.Value;
            double draft = _draftDeg.Value;
            Perforator.Result res;
            try
            {
                res = Perforator.Punch(targetBrep, punchCurves, _punchDir, tol, wallOnly, punchFaces, safS, safE, draft);
            }
            catch (Exception ex)
            {
                SetStatus("천공 실패: " + ex.Message);
                return;
            }

            if (res == null || res.Breps == null || res.Breps.Length == 0)
            {
                SetStatus("불리언 차집합 실패. 방향/커브 위치를 확인하세요.");
                return;
            }

            // 결과를 도큐먼트에 추가하고 원본은 숨김
            int gi = doc.Groups.Add("perforated");
            var attr = new ObjectAttributes { Name = "perforated" };
            attr.AddToGroup(gi);
            foreach (var b in res.Breps) doc.Objects.AddBrep(b, attr);
            doc.Objects.Hide(_targetObjectId, true);
            doc.Views.Redraw();

            SetStatus($"천공 완료 [{res.Stage}]: 성공 {res.SuccessCount}/{res.CutterCount} (폴백 {res.FallbackCount}, 실패 {res.FailedCount}, 벽없음 {res.NoWallCount}) — 구배 {draft:0.0}°");
        }
    }
}
