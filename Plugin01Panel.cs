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
        private Button _btnSurface; // 선택/해제 토글 버튼
        private Button _btnPattern; // 패턴 커브 직접 선택/해제 토글 버튼
        private Button _btnPreview; // 미리보기/지우기 토글 버튼
        private Button _btnPunchFaces; // 천공 벽면 선택/해제 토글 버튼
        private Button _btnCutterPreview; // 커터 미리보기/지우기 토글 버튼
        private Button _btnInteractive;   // 패턴 위치 조절 (인터랙티브)
        private Button _btnPickPunchCurves; // 천공 커브 직접 선택

        // 인터랙티브 배치로 고정한 위치 + 재계산 함수 (회전° 옵션으로 실시간 갱신용)
        private Func<Point3d?, List<Curve>> _placeRecompute;
        private Point3d? _placeCenter;
        private bool _patternFromDirectSelect = false; // true 면 직접 선택한 커브 사용 중
        private List<Curve> _importedPattern = new List<Curve>(); // SVG 로 불러온 원본 패턴(직접선택 해제 시 복귀)
        private readonly DropDown _ddMode = new DropDown();
        private readonly DropDown _ddStrategy = new DropDown();
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
        private StackLayout _rowStrategy;
        private StackLayout _rowBoundary;
        private readonly DropDown _ddBoundary = new DropDown();
        private readonly NumericStepper _fadeRings = new NumericStepper { Value = 2, MinValue = 1, MaxValue = 10, DecimalPlaces = 0, Width = 55 };
        private StackLayout _rowFlips;
        private StackLayout _rowOpenRatio;
        private readonly CheckBox _openRatioEnable = new CheckBox { Text = "개구율 맞춤" };
        private readonly NumericStepper _openRatioPct = new NumericStepper { Value = 30, MinValue = 0.1, MaxValue = 90, DecimalPlaces = 1, Increment = 1.0, Width = 70 };
        private readonly Label _lblOpenArea = new Label { Text = "  (면 선택 시 개구 면적 표시)" };
        private double _selectedFaceArea = 0; // 선택 면 합산 면적(mm²), 면 선택 시 갱신
        private double _lastPreviewArea = -1; // 마지막 미리보기 패턴 면적(mm²) — PartialFit 표시용

        private StackLayout _rowCounts;

        private List<Curve> _pattern = new List<Curve>();
        private Brep _targetBrep;
        private Guid _targetObjectId = Guid.Empty;       // 천공 대상 (실제 도큐먼트 객체)
        private List<int> _faceIndices = new List<int>();
        private List<Guid> _lastTiledIds = new List<Guid>(); // 마지막 타일링 확정 결과
        private Vector3d _punchDir = Vector3d.ZAxis;     // 관통 방향 (기본 World Z)

        private readonly TilePreviewConduit _preview = new TilePreviewConduit();

        // 단계별 접이식(드롭다운) 섹션 + 완료 인디케이터(초록 ●)
        private Expander _exp1, _exp2, _exp3, _exp4;
        private readonly Label _ind1 = MakeIndicator();
        private readonly Label _ind2 = MakeIndicator();
        private readonly Label _ind3 = MakeIndicator();
        private readonly Label _ind4 = MakeIndicator();

        private static readonly Color DoneColor = Color.FromArgb(40, 180, 70);
        private static readonly Color TodoColor = Color.FromArgb(170, 170, 170);

        private static Label MakeIndicator() =>
            new Label { Text = "●", TextColor = TodoColor, Font = SystemFonts.Bold() };

        // ===== 방식 드롭다운 아이콘 (코드로 직접 그림, 외부 이미지 파일 불필요) =====
        // 아이콘과 텍스트를 "한 비트맵"에 함께 그려 세로 정렬을 완전히 제어한다.
        // (Eto/WPF 항목 템플릿의 텍스트 세로정렬을 외부에서 못 바꾸기 때문)
        private const int ModeIconSize = 28;
        private static readonly Color IconColor = Color.FromArgb(55, 95, 170);   // 파란 외곽/도형
        private static readonly Color ModeTextColor = Color.FromArgb(30, 30, 30);

        private static Eto.Drawing.Font ModeFont()
        {
            try { return new Eto.Drawing.Font("Malgun Gothic", 9f); } // 한글 글리프 보장
            catch { return SystemFonts.Default(); }
        }

        // 아이콘(왼쪽) + 텍스트(오른쪽)를 한 비트맵에 세로 중앙으로 합쳐 그림.
        private static Bitmap MakeModeItem(string text, Action<Graphics> drawIcon)
        {
            var font = ModeFont();
            SizeF ts;
            using (var tmp = new Bitmap(1, 1, PixelFormat.Format32bppRgba))
            using (var gg = new Graphics(tmp))
                ts = gg.MeasureString(font, text);

            int s = ModeIconSize, gap = 6, pad = 2;
            int w = s + gap + (int)Math.Ceiling(ts.Width) + pad;
            int h = s;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgba);
            using (var g = new Graphics(bmp))
            {
                g.AntiAlias = true;
                g.Clear(Colors.Transparent);
                drawIcon(g);                       // 0..s 영역(세로 전체)에 아이콘 → 세로 중앙
                float ty = (h - ts.Height) / 2f;   // 텍스트도 세로 중앙
                g.DrawText(font, ModeTextColor, s + gap, ty, text);
            }
            return bmp;
        }

        // 한 장 늘려 맞춤(Stretch): 4개의 점 + 각 점에서 대각선 바깥으로 향하는 화살표 → 늘어남.
        private static void DrawStretchIcon(Graphics g)
        {
            int s = ModeIconSize;
            var pen = new Pen(IconColor, 1.6f);
            var brush = new SolidBrush(IconColor);
            float c = s / 2f;
            float d = s * 0.16f, r = s * 0.10f;
            float startOff = d + s * 0.07f, tipOff = d + s * 0.27f, barb = s * 0.14f;
            int[] sgn = { -1, 1 };
            foreach (int sx in sgn)
                foreach (int sy in sgn)
                {
                    float dotx = c + sx * d, doty = c + sy * d;
                    g.FillEllipse(brush, dotx - r, doty - r, 2 * r, 2 * r);
                    float ax = c + sx * startOff, ay = c + sy * startOff;
                    float tx = c + sx * tipOff, ty = c + sy * tipOff;
                    g.DrawLine(pen, ax, ay, tx, ty);
                    g.DrawLine(pen, tx, ty, tx - sx * barb, ty);
                    g.DrawLine(pen, tx, ty, tx, ty - sy * barb);
                }
        }

        // 실제 크기-패턴 분석 적용(RealSize): 실제 크기 도형이 면 전체에 격자로 반복(3x3 점).
        private static void DrawTiledIcon(Graphics g)
        {
            int s = ModeIconSize;
            var brush = new SolidBrush(IconColor);
            float m = s * 0.14f, gap = (s - 2 * m) / 3f, r = gap * 0.30f;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    float cx = m + gap * (i + 0.5f), cy = m + gap * (j + 0.5f);
                    g.FillEllipse(brush, cx - r, cy - r, 2 * r, 2 * r);
                }
        }

        // 실제 크기-패턴 부분적용(PartialFit): 면(테두리) 안 좌상단에만 작은 묶음(2x2 점) 배치.
        private static void DrawPartialIcon(Graphics g)
        {
            int s = ModeIconSize;
            var pen = new Pen(IconColor, 1.6f);
            var brush = new SolidBrush(IconColor);
            float fm = s * 0.12f;
            g.DrawRectangle(pen, fm, fm, s - 2 * fm, s - 2 * fm); // 면 테두리
            float start = s * 0.27f, gap = s * 0.21f, r = s * 0.10f;
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    float cx = start + i * gap, cy = start + j * gap;
                    g.FillEllipse(brush, cx - r, cy - r, 2 * r, 2 * r);
                }
        }

        // 단계 완료 표시 + (완료 시) 다음 단계 자동 펼치기
        private void SetStepDone(int step, bool done)
        {
            Label ind = step == 1 ? _ind1 : step == 2 ? _ind2 : step == 3 ? _ind3 : _ind4;
            ind.TextColor = done ? DoneColor : TodoColor;
            if (done)
            {
                var next = step == 1 ? _exp2 : step == 2 ? _exp3 : step == 3 ? _exp4 : null;
                if (next != null && !next.Expanded) next.Expanded = true;
            }
        }

        // 접이식 섹션 헤더: [인디케이터 ●] [굵은 제목]
        private static Control StepHeader(Label indicator, string title) =>
            new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { indicator, new Label { Text = title, Font = SystemFonts.Bold() } }
            };

        // 섹션 본문을 세로 스택으로 묶음
        private static StackLayout StepBody(params Control[] items)
        {
            var s = new StackLayout
            {
                Padding = new Padding(4, 6, 4, 4),
                Spacing = 8,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            foreach (var it in items) s.Items.Add(it);
            return s;
        }

        public Plugin01Panel()
        {
            Title = "Plugin 01 — 패턴 천공";
            ClientSize = new Size(400, 450);
            Topmost = true;
            Maximizable = false;
            Minimizable = false;
            Resizable = true; // 세로는 자유롭게 늘림. 가로는 아래 SizeChanged 에서 400 으로 고정.
            // 가로폭 400px 고정: 사용자가 폭을 바꾸면 즉시 400 으로 되돌림 (세로 높이는 유지)
            SizeChanged += (s, e) =>
            {
                if (ClientSize.Width != 400)
                    ClientSize = new Size(400, ClientSize.Height);
            };

            var btnImport = new Button { Text = "SVG 불러오기" };
            btnImport.Click += OnImport;

            _btnSurface = new Button { Text = "대상 표면 선택" };
            _btnSurface.Click += OnToggleSurface;

            _btnPattern = new Button { Text = "패턴 커브 직접 선택" };
            _btnPattern.Click += OnTogglePattern;

            // 방식 드롭다운: 아이콘+텍스트를 한 비트맵에 합쳐(세로 중앙 정렬 완전 제어) 이미지로만 표시.
            // Text 는 비워 별도 텍스트 렌더(상단 정렬)를 막는다.
            _ddMode.ItemImageBinding = new PropertyBinding<Image>("Image");
            _ddMode.Items.Add(new ImageListItem { Text = "", Image = MakeModeItem("한 장 늘려 맞춤 (Stretch)", DrawStretchIcon) });
            _ddMode.Items.Add(new ImageListItem { Text = "", Image = MakeModeItem("실제 크기 - 패턴 분석 적용 (RealSize)", DrawTiledIcon) });
            _ddMode.Items.Add(new ImageListItem { Text = "", Image = MakeModeItem("실제 크기 - 패턴 부분적용 (PartialFit)", DrawPartialIcon) });
            _ddMode.SelectedIndex = 0;
            _ddMode.SelectedIndexChanged += OnModeChanged;

            // PartialFit 위치 고정 후 회전° 변경 → 같은 위치로 미리보기 실시간 갱신
            _rotDeg.ValueChanged += (s2, e2) =>
            {
                if (_ddMode.SelectedIndex == 2 && _placeRecompute != null
                    && _preview.Curves != null && _preview.Curves.Count > 0)
                {
                    var cur = _placeRecompute(_placeCenter);
                    _preview.Curves = cur;
                    _preview.Enabled = true;
                    UpdatePreviewButtonText();
                    RhinoDoc.ActiveDoc?.Views.Redraw();
                }
            };

            // RealSize 전용 알고리즘(전략) 선택 — 대상 표면에 따라 골라 타일링 성공률을 높임
            _ddStrategy.Items.Add("전략 1: 표면 따라 걷기 (곡면/연속면)");
            _ddStrategy.Items.Add("전략 2: 평면 격자 투영 (다면/평면 위주)");
            _ddStrategy.SelectedIndex = 0;

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
                Orientation = Orientation.Vertical,
                Spacing = 4,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Items =
                {
                    new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Center,
                        Items = { new Label { Text = "U(mm):" }, _uOff, new Label { Text = "V(mm):" }, _vOff } },
                    new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Center,
                        Items = { new Label { Text = "회전°:" }, _rotDeg, new Label { Text = "크기%:" }, _scalePct } }
                },
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

            // 개구율 맞춤: 목표 개구율(%)에 맞게 2D 패턴 구멍 크기를 조절(간격 유지)
            _rowOpenRatio = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { _openRatioEnable, new Label { Text = "목표 개구율(%):" }, _openRatioPct }
            };
            // % 나 사용 여부가 바뀌면 개구 면적(mm²) 표시 갱신
            _openRatioPct.ValueChanged += (s2, e2) => UpdateOpenAreaInfo();
            _openRatioEnable.CheckedChanged += (s2, e2) => UpdateOpenAreaInfo();

            // RealSize 전용 경계(초록선) 처리 방식
            _ddBoundary.Items.Add("경계 셀 삭제");
            _ddBoundary.Items.Add("경계로 갈수록 축소");
            _ddBoundary.Items.Add("경계에 맞춰 자르기");
            _ddBoundary.SelectedIndex = 0;
            _rowBoundary = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new Label { Text = "경계 처리:" },
                    _ddBoundary,
                    new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Center,
                        Items = { new Label { Text = "축소 링:" }, _fadeRings } }
                },
                Visible = false
            };

            _rowStrategy = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items = { new Label { Text = "알고리즘:" }, _ddStrategy },
                Visible = false
            };

            var rowMargin = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { new Label { Text = "외곽선 마진(mm):" }, _margin }
            };

            // 방향/기울기/방위 UI 는 제거됨 (벽면 선택 시 관통 방향 자동 설정). 값은 내부적으로만 사용.
            _tiltDeg.ValueChanged += OnAngleChanged;
            _aziDeg.ValueChanged += OnAngleChanged;
            _btnPickPunchCurves = new Button { Text = "천공 커브 직접 선택 (선택)" };
            _btnPickPunchCurves.Click += OnPickPunchCurves;
            _btnPunchFaces = new Button { Text = "천공 대상 벽면 선택" };
            _btnPunchFaces.Click += OnTogglePunchFaces;
            var rowDraft = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { new Label { Text = "구배각도(°):" }, _draftDeg }
            };
            var rowSafety = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Items = {
                    new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Center,
                        Items = { new Label { Text = "커터 연장 시작단(mm):" }, _safetyStart } },
                    new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Center,
                        Items = { new Label { Text = "끝단(mm):" }, _safetyEnd } }
                }
            };
            _btnCutterPreview = new Button { Text = "커터 미리보기 (Boolean 전)" };
            _btnCutterPreview.Click += OnToggleCutterPreview;
            var btnPunch = new Button { Text = "천공 실행" };
            btnPunch.Click += OnPunch;

            _btnPreview = new Button { Text = "미리보기" };
            _btnPreview.Click += OnTogglePreview;

            _btnInteractive = new Button { Text = "패턴 위치 조절 (인터랙티브)" };
            _btnInteractive.Click += OnInteractivePlace;

            var btnTile = new Button { Text = "타일링 실행 (확정)" };
            btnTile.Click += OnApply;

            Closed += (s, e) => DisableAllPreview();

            _exp1 = new Expander
            {
                Header = StepHeader(_ind1, "1. SVG 패턴"),
                Expanded = true,
                Content = StepBody(btnImport, _lblImport)
            };

            _exp2 = new Expander
            {
                Header = StepHeader(_ind2, "2. 대상 / 패턴"),
                Expanded = false,
                Content = StepBody(_autoConnect, _btnSurface, _lblSurface, _btnPattern, _lblPattern)
            };

            _exp3 = new Expander
            {
                Header = StepHeader(_ind3, "3. 타일링"),
                Expanded = false,
                Content = StepBody(
                    new Label { Text = "방식" }, _ddMode,
                    _rowCounts, _rowFlips, _rowRealSize, _rowBoundary, _rowStrategy, _rowPartial,
                    rowMargin, _rowOpenRatio, _lblOpenArea,
                    _btnPreview, _btnInteractive, btnTile)
            };

            _exp4 = new Expander
            {
                Header = StepHeader(_ind4, "4. 천공"),
                Expanded = false,
                Content = StepBody(
                    _wallOnly, _punchAutoConnect,
                    _btnPunchFaces, _lblPunchFaces,
                    _btnPickPunchCurves, _lblPunchCurves, rowDraft, rowSafety,
                    _btnCutterPreview, btnPunch)
            };

            var contentStack = new StackLayout
            {
                Padding = new Padding(12),
                Spacing = 6,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    _exp1,
                    _exp2,
                    _exp3,
                    _exp4,
                    new Label { Text = " " },
                    _lblStatus
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

            // 네이티브 컨트롤 생성 후: 드롭다운 텍스트 세로중앙 + 버튼 모서리 둥글게
            Shown += (s, e) => { CenterDropDownText(_ddMode); ApplyRoundedCorners(); };
        }

        // 버튼 모서리를 5px 둥글게 (WPF 한정). 호버/클릭 피드백 포함. 실패 시 무시.
        private const string RoundedXaml =
@"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='Button'>
    <Setter Property='Background' Value='#F0F0F0'/>
    <Setter Property='BorderBrush' Value='#ADADAD'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='6,3'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='bd' CornerRadius='5' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' Margin='{TemplateBinding Padding}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='#E5F1FB'/>
              <Setter TargetName='bd' Property='BorderBrush' Value='#3399FF'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='#CCE4F7'/>
              <Setter TargetName='bd' Property='BorderBrush' Value='#2E8AE6'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Foreground' Value='#A0A0A0'/>
              <Setter TargetName='bd' Property='Background' Value='#F5F5F5'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

        // 활성(선택됨/미리보기 중) 버튼은 호버 피드백과 같은 연한 파랑으로 유지
        private static readonly Color ActiveBtnColor = Color.FromArgb(0xE5, 0xF1, 0xFB);
        private static readonly Color IdleBtnColor = Color.FromArgb(0xF0, 0xF0, 0xF0);
        private static void SetButtonActive(Button btn, bool active)
        {
            if (btn != null) btn.BackgroundColor = active ? ActiveBtnColor : IdleBtnColor;
        }

        private void ApplyRoundedCorners()
        {
            try
            {
                var fe = ControlObject as System.Windows.FrameworkElement;
                if (fe == null) return;
                var rd = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(RoundedXaml);
                fe.Resources.MergedDictionaries.Add(rd); // 이후 생성 버튼(팝업 등)에도 적용
                var btnStyle = rd[typeof(System.Windows.Controls.Button)] as System.Windows.Style;
                if (btnStyle != null)
                    fe.Dispatcher.BeginInvoke(new Action(() =>
                        ForEachVisual<System.Windows.Controls.Button>(fe, b => b.Style = btnStyle)),
                        System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch { }
        }

        // 비주얼 트리에서 특정 타입의 모든 자식에 동작 적용
        private static void ForEachVisual<T>(System.Windows.DependencyObject root, Action<T> action)
            where T : System.Windows.DependencyObject
        {
            if (root == null) return;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is T t) action(t);
                ForEachVisual(c, action);
            }
        }

        // WPF DropDown 항목/선택부의 아이콘과 텍스트를 모두 세로 중앙 정렬.
        // (항목 템플릿은 Image+TextBlock 가로 배치라 TextBlock 이 위로 붙음 → 둘 다 Center 로 맞춤)
        // Windows(WPF) 한정 기능이라 다른 플랫폼/예외 시 그냥 무시.
        private static void CenterDropDownText(DropDown dd)
        {
            try
            {
                var cb = dd.ControlObject as System.Windows.Controls.ComboBox;
                if (cb == null) return;

                // 콘텐츠 블록 전체를 세로 중앙에
                cb.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;

                // 항목 템플릿 내부 TextBlock 을 세로 중앙 (아이콘 중간높이에 텍스트 중간높이 맞춤)
                var tbStyle = new System.Windows.Style(typeof(System.Windows.Controls.TextBlock));
                tbStyle.Setters.Add(new System.Windows.Setter(
                    System.Windows.FrameworkElement.VerticalAlignmentProperty,
                    System.Windows.VerticalAlignment.Center));
                cb.Resources[typeof(System.Windows.Controls.TextBlock)] = tbStyle;

                // 아이콘(Image)도 세로 중앙
                var imgStyle = new System.Windows.Style(typeof(System.Windows.Controls.Image));
                imgStyle.Setters.Add(new System.Windows.Setter(
                    System.Windows.FrameworkElement.VerticalAlignmentProperty,
                    System.Windows.VerticalAlignment.Center));
                cb.Resources[typeof(System.Windows.Controls.Image)] = imgStyle;
            }
            catch { }
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
            _rowBoundary.Visible = (m == 1 || m == 2); // 경계 처리 옵션: RealSize / PartialFit
            _rowStrategy.Visible = true; // 세 모드 모두 알고리즘(전략) 선택 제공
            _rowPartial.Visible = (m == 2);
            _rowOpenRatio.Visible = (m == 1); // 개구율 % 는 RealSize 만. Stretch/PartialFit 은 실제 패턴 면적만 표시
            _lastPreviewArea = -1;
            _placeRecompute = null; _placeCenter = null; // 모드 바뀌면 인터랙티브 배치 무효화
            UpdateOpenAreaInfo();
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
            _importedPattern = new List<Curve>(curves); // 직접선택 해제 시 복귀용 원본
            _patternFromDirectSelect = false;
            _lblPattern.Text = "(불러온 SVG 사용)";
            UpdatePatternButtonText();
            _lblImport.Text = $"패턴 {curves.Count}개 로드됨";
            SetStepDone(1, true);
            SetStatus("SVG 불러오기 완료");
        }

        // 표면 선택 여부에 따라 버튼 텍스트를 갱신 (선택됨 → "선택 해제", 미선택 → "선택")
        private void UpdateSurfaceButtonText()
        {
            bool has = _targetBrep != null && _faceIndices != null && _faceIndices.Count > 0;
            if (_btnSurface != null) _btnSurface.Text = has ? "대상 표면 선택 해제" : "대상 표면 선택";
            SetButtonActive(_btnSurface, has);
        }

        // 하나의 버튼으로 선택/해제 토글
        private void OnToggleSurface(object sender, EventArgs e)
        {
            bool has = _targetBrep != null && _faceIndices != null && _faceIndices.Count > 0;
            if (has) OnClearTargetSurface(sender, e);
            else OnPickSurface(sender, e);
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
                UpdateSurfaceButtonText();
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
                SetStepDone(2, _faceIndices.Count > 0);
                UpdateSurfaceButtonText();
                if (_punchFaceIndices == null || _punchFaceIndices.Count == 0) AutoSetPunchDir(_faceIndices);
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
                SetStepDone(2, _faceIndices.Count > 0);
                SetStatus($"표면 선택 완료 — 직접 선택 면 {_faceIndices.Count}개");
            }
            UpdateSurfaceButtonText();
            if (_punchFaceIndices == null || _punchFaceIndices.Count == 0) AutoSetPunchDir(_faceIndices);
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
            RecomputeSelectedFaceArea(); // 선택 면적 갱신 → 개구 면적 표시 업데이트
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

        // 직접선택 여부에 따라 버튼 텍스트 갱신
        private void UpdatePatternButtonText()
        {
            if (_btnPattern != null)
                _btnPattern.Text = _patternFromDirectSelect ? "패턴 커브 선택 해제" : "패턴 커브 직접 선택";
            SetButtonActive(_btnPattern, _patternFromDirectSelect);
        }

        // 하나의 버튼으로 직접선택/해제 토글
        private void OnTogglePattern(object sender, EventArgs e)
        {
            if (_patternFromDirectSelect) ClearDirectPattern();
            else OnPickPattern(sender, e);
        }

        // 직접 선택 해제 → SVG 로 불러온 패턴으로 복귀(없으면 빈 목록)
        private void ClearDirectPattern()
        {
            _patternFromDirectSelect = false;
            _pattern = new List<Curve>(_importedPattern);
            _lblPattern.Text = _importedPattern.Count > 0 ? "(불러온 SVG 사용)" : "(없음)";
            UpdatePatternButtonText();
            SetStepDone(1, _pattern.Count > 0);
            SetStatus("패턴 커브 직접 선택 해제됨");
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
            _patternFromDirectSelect = true;
            _lblPattern.Text = $"패턴 커브 {list.Count}개 직접 선택됨";
            UpdatePatternButtonText();
            SetStepDone(1, true);
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

        // 선택 면들의 합산 면적(mm², trim 반영)을 다시 계산하고 개구 면적 표시 갱신.
        private void RecomputeSelectedFaceArea()
        {
            double a = 0;
            if (_targetBrep != null && _faceIndices != null)
            {
                foreach (int fi in _faceIndices)
                {
                    if (fi < 0 || fi >= _targetBrep.Faces.Count) continue;
                    try
                    {
                        var fb = _targetBrep.Faces[fi].DuplicateFace(false); // 단일 trim 면 → 정확한 면적
                        var amp = AreaMassProperties.Compute(fb);
                        if (amp != null) a += amp.Area;
                    }
                    catch { }
                }
            }
            _selectedFaceArea = a;
            UpdateOpenAreaInfo();
        }

        // 닫힌 커브들의 면적 합(mm²).
        private static double TotalCurveArea(IList<Curve> curves)
        {
            double a = 0;
            if (curves == null) return 0;
            foreach (var c in curves)
            {
                if (c == null || !c.IsClosed) continue;
                var amp = AreaMassProperties.Compute(c);
                if (amp != null) a += Math.Abs(amp.Area);
            }
            return a;
        }

        // 개구 면적(mm²) 라벨 갱신. PartialFit 은 실제 생성 패턴 면적, 그 외는 목표 개구율 × 선택 면적.
        private void UpdateOpenAreaInfo()
        {
            if (_ddMode.SelectedIndex != 1) // Stretch/PartialFit: 현재 미리보기 패턴의 실제 면적
            {
                _lblOpenArea.Text = _lastPreviewArea >= 0
                    ? $"  현재 패턴 면적 {_lastPreviewArea:0.#} mm²"
                    : "  (미리보기하면 패턴 면적 표시)";
                return;
            }
            if (_selectedFaceArea <= 0)
            {
                _lblOpenArea.Text = "  (면 선택 시 개구 면적 표시)";
                return;
            }
            double ratio = Math.Max(0.0, _openRatioPct.Value / 100.0);
            double openArea = ratio * _selectedFaceArea;
            string off = _openRatioEnable.Checked == true ? "" : "  ※개구율 맞춤 꺼짐";
            _lblOpenArea.Text = $"  선택 면적 {_selectedFaceArea:0.#} mm² · 목표 개구 {openArea:0.#} mm²{off}";
        }

        // 개구율 맞춤: 목표 개구율(%)에 맞게 각 구멍을 "자기 중심" 기준으로 2D 스케일(간격 유지).
        // 끄면 원본 그대로 반환. achievedPct = 적용된 목표 개구율(%) (미적용 시 -1).
        // 개구율 = 구멍면적 / 셀면적(pitch 기반, 실패 시 패턴 bbox 기반).
        private List<Curve> ApplyOpenRatio(IList<Curve> src, out double achievedPct)
        {
            achievedPct = -1;
            var passthrough = new List<Curve>(src);
            if (_openRatioEnable.Checked != true) return passthrough;

            // 현재 개구율: pitch 기반(정확) → 실패 시 bbox 기반
            double current = -1;
            var info = PatternAnalyzer.Analyze(src);
            if (info.Valid && info.UnitCells.Count > 0)
            {
                double cellArea = info.PitchU * info.PitchV;
                double unitHole = 0;
                foreach (var uc in info.UnitCells)
                {
                    var a = AreaMassProperties.Compute(uc);
                    if (a != null) unitHole += Math.Abs(a.Area);
                }
                if (cellArea > 1e-9 && unitHole > 1e-12) current = unitHole / cellArea;
            }
            if (current < 0)
            {
                var bb = BoundingBox.Empty; double ha = 0;
                foreach (var c in src)
                {
                    bb.Union(c.GetBoundingBox(true));
                    var a = AreaMassProperties.Compute(c);
                    if (a != null) ha += Math.Abs(a.Area);
                }
                double ba = (bb.Max.X - bb.Min.X) * (bb.Max.Y - bb.Min.Y);
                if (ba > 1e-9 && ha > 1e-12) current = ha / ba;
            }
            if (current < 0) { SetStatus("개구율 계산 실패 (닫힌 커브 패턴인지 확인)"); return passthrough; }

            double target = Math.Max(0.001, _openRatioPct.Value / 100.0);
            double s = Math.Sqrt(target / current);
            if (double.IsNaN(s) || s < 1e-6) return passthrough;

            var scaled = new List<Curve>();
            foreach (var c in src)
            {
                var d = c.DuplicateCurve();
                var cb = d.GetBoundingBox(true);
                d.Transform(Transform.Scale(cb.Center, s));
                scaled.Add(d);
            }
            achievedPct = target * 100.0; // pitch 기반이면 정확히 목표에 수렴
            return scaled;
        }

        // 타일링 결과 커브 계산 (도큐먼트엔 추가하지 않음). 실패 시 null 반환.
        private List<Curve> ComputeTiling()
        {
            if (_targetBrep == null || _faceIndices.Count == 0) { SetStatus("먼저 대상 표면을 선택하세요."); return null; }
            if (_pattern == null || _pattern.Count == 0) { SetStatus("먼저 패턴을 불러오거나 선택하세요."); return null; }

            int m = _ddMode.SelectedIndex;

            // 개구율 맞춤: RealSize 만 적용. Stretch/PartialFit 은 실제 생성 패턴 면적만 표시(개구율 미적용).
            double openAchieved = -1;
            List<Curve> pattern;
            if (m == 1) pattern = ApplyOpenRatio(_pattern, out openAchieved);
            else pattern = new List<Curve>(_pattern);
            string orInfo = openAchieved >= 0 ? $", 개구율~{openAchieved:0.#}%" : "";

            var pBox = BoundingBox.Empty;
            foreach (var c in pattern) pBox.Union(c.GetBoundingBox(true));
            if (!pBox.IsValid) { SetStatus("패턴 경계 계산 실패"); return null; }

            var all = new List<Curve>();

            // 실제 크기 - 패턴 분석 적용 (BFS 위상 전달로 면 간 연속성 유지)
            if (m == 1)
            {
                var info = PatternAnalyzer.Analyze(pattern);
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

                int strat = _ddStrategy.SelectedIndex;
                double marginR = Math.Max(0, _margin.Value);
                try
                {
                    if (strat == 1)
                    {
                        all.AddRange(SurfaceTiler.TileConnectedRealSizeFit_StrategyTwo(_targetBrep, _faceIndices, info, refDirR, angleTolR, _rotDegR.Value));
                        all = ApplyMarginFilter(all); // 전략2 는 종전대로 후처리 마진 필터
                    }
                    else
                    {
                        // 전략1: 경계 처리 모드 + margin(경계 인셋)을 타일러 내부에서 처리 → ApplyMarginFilter 미적용
                        all.AddRange(SurfaceTiler.TileConnectedRealSizeFit(_targetBrep, _faceIndices, info, refDirR, angleTolR, _rotDegR.Value, _ddBoundary.SelectedIndex, (int)_fadeRings.Value, marginR));
                    }
                    string stratName = (strat == 1) ? "전략2(평면격자)" : "전략1(표면걷기)";
                    SetStatus($"분석[{stratName}]: 셀 {info.CellW:0.#}x{info.CellH:0.#}, 회전 {_rotDegR.Value:0.#}° → 셀 {all.Count}개{orInfo}");
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
                    double marginP = Math.Max(0, _margin.Value);
                    if (_ddStrategy.SelectedIndex == 1) // 전략 2 → 접평면 스탬프 (표면 추종, 기존 방식)
                    {
                        all.AddRange(SurfaceTiler.TileConnectedPartial(_targetBrep, _faceIndices, pattern, pBox, refDirP, angleTolP, _uOff.Value, _vOff.Value, _rotDeg.Value, scale));
                        all = ApplyMarginFilter(all); // 전략2 는 후처리 마진 필터
                    }
                    else                                 // 전략 1 → 평행 투영 (경계 처리 + 마진을 타일러 내부 처리)
                        all.AddRange(SurfaceTiler.TileConnectedPartial_Projection(_targetBrep, _faceIndices, pattern, pBox, refDirP, angleTolP, _uOff.Value, _vOff.Value, _rotDeg.Value, scale, null, _ddBoundary.SelectedIndex, (int)_fadeRings.Value, marginP));
                    _lastPreviewArea = TotalCurveArea(all); // 실제 생성 패턴 면적 → 라벨 표시
                    UpdateOpenAreaInfo();
                    SetStatus($"부분 적용[전략{_ddStrategy.SelectedIndex + 1}]: U={_uOff.Value:0.#} V={_vOff.Value:0.#} 회전={_rotDeg.Value:0.#}° 크기={_scalePct.Value:0.#}% → 셀 {all.Count}개, 면적 {_lastPreviewArea:0.#} mm²");
                    return all;
                }
                catch (Exception ex) { SetStatus("배치 실패: " + ex.Message); return null; }
            }

            // Stretch (m == 0): 기본 nU=nV=1, 반복 횟수 옵션으로 조절 가능
            double pw = pBox.Max.X - pBox.Min.X;
            double ph = pBox.Max.Y - pBox.Min.Y;

            int nU = Math.Max(1, (int)_nu.Value);
            int nV = Math.Max(1, (int)_nv.Value);

            long est = (long)pattern.Count * nU * nV * _faceIndices.Count;
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
                if (_ddStrategy.SelectedIndex != 1) // 전략 1 → 평행 투영 (모든 면 공통)
                {
                    Vector3d refDirS = Vector3d.Zero;
                    var seedFaceS = _targetBrep.Faces[_faceIndices[0]];
                    {
                        var sd0 = seedFaceS.Domain(0); var sd1 = seedFaceS.Domain(1);
                        Point3d sp; Vector3d[] sders;
                        if (seedFaceS.Evaluate(sd0.ParameterAt(0.5), sd1.ParameterAt(0.5), 1, out sp, out sders) && sders != null && sders.Length >= 1)
                        { refDirS = sders[0]; refDirS.Unitize(); }
                    }
                    double angleTolS = RhinoDoc.ActiveDoc?.ModelAngleToleranceRadians ?? Rhino.RhinoMath.ToRadians(1);
                    all.AddRange(SurfaceTiler.TileConnectedStretch_Projection(_targetBrep, _faceIndices, pattern, pBox, refDirS, angleTolS, nU, nV, marginMm, _flipH.Checked == true, _flipV.Checked == true, _rotDegS.Value));
                    _lastPreviewArea = TotalCurveArea(all); UpdateOpenAreaInfo();
                    SetStatus($"한 장 늘려 맞춤[전략1(투영), {nU}x{nV}]: 패턴 {pattern.Count}개 → 커브 {all.Count}개, 면적 {_lastPreviewArea:0.#} mm²");
                    return all;
                }

                // 전략 2: 기존 방식 — 같은 바탕 곡면을 공유하는 면들끼리 묶기
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
                    all.AddRange(SurfaceTiler.TileRegion(srf, grp, uReg, vReg, pattern, pBox, nU, nV, chord, marginMm, _flipH.Checked == true, _flipV.Checked == true, _rotDegS.Value));
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
                    all.AddRange(SurfaceTiler.TileConnectedStretch(_targetBrep, _faceIndices, pattern, pBox, refDir, angleTol, nU, nV, marginMm, _flipH.Checked == true, _flipV.Checked == true, _rotDegS.Value));

                    SetStatus($"한 장 늘려 맞춤(다면 연속, {nU}x{nV}): 패턴 {pattern.Count}개 -> 커브 {all.Count}개");
                }
                _lastPreviewArea = TotalCurveArea(all); UpdateOpenAreaInfo();
                return all; // stretch는 마진이 영역 인셋으로 이미 적용됨
            }
            catch (Exception ex) { SetStatus("타일링 실패: " + ex.Message); return null; }
        }

        // 미리보기 커브가 떠 있는지에 따라 버튼 텍스트 갱신
        private void UpdatePreviewButtonText()
        {
            bool shown = _preview.Curves != null && _preview.Curves.Count > 0;
            if (_btnPreview != null) _btnPreview.Text = shown ? "미리보기 지우기" : "미리보기";
            SetButtonActive(_btnPreview, shown);
            SetButtonActive(_btnInteractive, shown); // 미리보기 실행 중엔 위치조절 버튼도 활성 표시
        }

        // 하나의 버튼으로 미리보기/지우기 토글
        private void OnTogglePreview(object sender, EventArgs e)
        {
            bool shown = _preview.Curves != null && _preview.Curves.Count > 0;
            if (shown) OnClearPreview(sender, e);
            else OnPreview(sender, e);
        }

        private void OnPreview(object sender, EventArgs e)
        {
            var tiled = ComputeTiling();
            if (tiled == null) return;
            if (tiled.Count == 0) { SetStatus("생성된 커브가 없습니다."); return; }

            _preview.Curves = tiled;
            _preview.Enabled = true;
            UpdatePreviewButtonText();
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
            UpdatePreviewButtonText();
            UpdateCutterPreviewButtonText();
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
            SetStepDone(3, _lastTiledIds.Count > 0);
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
                // trim 의 2D bbox 사용 (face.Domain 의 untrimmed surface 중심은 trim 밖이거나 singularity 가능)
                double fuMin = face.Domain(0).T0, fuMax = face.Domain(0).T1;
                double fvMin = face.Domain(1).T0, fvMax = face.Domain(1).T1;
                try
                {
                    var c2 = face.OuterLoop?.To2dCurve();
                    if (c2 != null)
                    {
                        var bb2 = c2.GetBoundingBox(true);
                        fuMin = bb2.Min.X; fuMax = bb2.Max.X;
                        fvMin = bb2.Min.Y; fvMax = bb2.Max.Y;
                    }
                }
                catch { }
                double fuc = 0.5 * (fuMin + fuMax);
                double fvc = 0.5 * (fvMin + fvMax);

                Vector3d[] derivs;
                Point3d dummyPt;
                // Evaluate 반환값 체크
                if (!((Surface)face).Evaluate(fuc, fvc, 1, out dummyPt, out derivs)) continue;
                if (derivs == null || derivs.Length < 2) continue;
                Vector3d du = derivs[0]; Vector3d dv = derivs[1];
                if (du.Length < 1e-9 || dv.Length < 1e-9) continue;
                var n = Vector3d.CrossProduct(du, dv);
                if (n.Length < 1e-9) continue;
                n.Unitize();
                avgN += n;
                sumCenter += (Vector3d)dummyPt;
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
            var patternL = new List<Curve>(_pattern); // 인터랙티브=PartialFit: 개구율 미적용(크기조절로 조정)
            BoundingBox pBoxL = BoundingBox.Empty;
            foreach (var pc in patternL) pBoxL.Union(pc.GetBoundingBox(true));
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

            // 전략1(평행투영)은 면 mesh·경계 loop 사전 계산이 무거움 → 드래그 전에 한 번만 만들어 캐시.
            // (GetPoint 가 modal 이라 드래그 중 경계/마진 옵션은 바뀌지 않으므로 여기서 캡처해도 안전)
            int boundaryModeL = _ddBoundary.SelectedIndex;
            int fadeRingsL = (int)_fadeRings.Value;
            double marginCtxL = Math.Max(0, _margin.Value);
            SurfaceTiler.PartialProjContext projCtx =
                (_ddStrategy.SelectedIndex != 1)
                    ? SurfaceTiler.BuildPartialProjContext(_targetBrep, _faceIndices, patternL, refDirL, boundaryModeL, fadeRingsL, marginCtxL)
                    : null;

            Func<Point3d?, List<Curve>> recompute = (Point3d? overrideCenter) =>
            {
                try
                {
                    double scaleL = Math.Max(0.0001, _scalePct.Value / 100.0);
                    List<Curve> res;
                    if (_ddStrategy.SelectedIndex == 1) // 전략 2 → 접평면 스탬프
                    {
                        res = SurfaceTiler.TileConnectedPartial(_targetBrep, _faceIndices, patternL, pBoxL,
                            refDirL, angleTolL, _uOff.Value, _vOff.Value, _rotDeg.Value, scaleL, overrideCenter);
                        res = ApplyMarginFilter(res ?? new List<Curve>());
                    }
                    else                                 // 전략 1 → 평행 투영 (캐시된 컨텍스트 재사용 → 빠름)
                        res = (projCtx != null && projCtx.Valid)
                            ? SurfaceTiler.TilePartialProjectionFromContext(projCtx, patternL, pBoxL,
                                _uOff.Value, _vOff.Value, _rotDeg.Value, scaleL, overrideCenter) ?? new List<Curve>()
                            : new List<Curve>();
                    _lastPreviewArea = TotalCurveArea(res); // 인터랙티브 배치 중 실제 패턴 면적 표시
                    UpdateOpenAreaInfo();
                    return res;
                }
                catch { return new List<Curve>(); }
            };

            // === 위치 조절 (좌클릭 시 위치 고정) ===
            var gp1 = new Rhino.Input.Custom.GetPoint();
            gp1.SetCommandPrompt("패턴 위치 클릭 (좌클릭=위치 고정, Esc 취소). 회전은 회전° 옵션 사용");

            List<Curve> dynPreview = recompute(null);
            Point3d lastSurfacePt = seedSurf; // 표면 위 마지막 커서 지점 (클릭 확정 시 이 점 사용)

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
            // 클릭 점(gp1.Point())은 표면이 아닌 CPlane 위라 투영이 빗나갈 수 있음 →
            // 표면 위 마지막 커서 지점(lastSurfacePt)을 위치로 사용해 미리보기를 그대로 유지.
            // 좌클릭 한 번으로 위치 고정. 회전은 '회전°' 옵션으로 조절.
            _placeRecompute = recompute; // 회전° 변경 시 같은 위치로 재계산하기 위해 보관
            _placeCenter = lastSurfacePt;
            var finalCurves = recompute(lastSurfacePt);
            _preview.Curves = finalCurves;
            _preview.Enabled = true;
            UpdatePreviewButtonText();
            RhinoDoc.ActiveDoc?.Views.Redraw();
            SetStatus($"패턴 위치 고정: U={_uOff.Value:0.0}mm V={_vOff.Value:0.0}mm — 회전은 '회전°' 옵션으로 조절, '타일링 실행'으로 베이크");
        }

        // ============================== 4. 천공 ==============================

        // 선택한 면들의 평균 법선으로 관통 방향을 자동 설정 (벽면 선택 → 그 벽을 수직 관통).
        private void AutoSetPunchDir(IList<int> faceIndices)
        {
            if (_targetBrep == null || faceIndices == null || faceIndices.Count == 0) return;
            Vector3d sumN = Vector3d.Zero;
            int cnt = 0;
            foreach (int fi in faceIndices)
            {
                if (fi < 0 || fi >= _targetBrep.Faces.Count) continue;
                var face = _targetBrep.Faces[fi];
                double uMin = face.Domain(0).T0, uMax = face.Domain(0).T1;
                double vMin = face.Domain(1).T0, vMax = face.Domain(1).T1;
                try
                {
                    var c2 = face.OuterLoop?.To2dCurve();
                    if (c2 != null)
                    {
                        var bb2 = c2.GetBoundingBox(true);
                        uMin = bb2.Min.X; uMax = bb2.Max.X; vMin = bb2.Min.Y; vMax = bb2.Max.Y;
                    }
                }
                catch { }
                Point3d p; Vector3d[] ders;
                if (!((Surface)face).Evaluate(0.5 * (uMin + uMax), 0.5 * (vMin + vMax), 1, out p, out ders)) continue;
                if (ders == null || ders.Length < 2) continue;
                var n = Vector3d.CrossProduct(ders[0], ders[1]);
                if (n.Length < 1e-9) continue;
                n.Unitize();
                if (face.OrientationIsReversed) n = -n; // 미러면 normal 복원
                sumN += n; cnt++;
            }
            if (cnt == 0) return;
            if (!sumN.Unitize()) return;
            _punchDir = sumN;
            SyncAnglesFromDir();
            UpdateDirLabel();
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

        // 천공 벽면 선택 여부에 따라 버튼 텍스트 갱신
        private void UpdatePunchFacesButtonText()
        {
            bool has = _punchFaceIndices != null && _punchFaceIndices.Count > 0;
            if (_btnPunchFaces != null) _btnPunchFaces.Text = has ? "천공 벽면 선택 해제" : "천공 대상 벽면 선택";
            SetButtonActive(_btnPunchFaces, has);
        }

        // 하나의 버튼으로 천공 벽면 선택/해제 토글
        private void OnTogglePunchFaces(object sender, EventArgs e)
        {
            bool has = _punchFaceIndices != null && _punchFaceIndices.Count > 0;
            if (has) OnClearPunchFaces(sender, e);
            else OnPickPunchFaces(sender, e);
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
            AutoSetPunchDir(indices); // 선택한 벽면 법선으로 관통 방향 자동 설정
            UpdatePunchFacesButtonText();
            UpdatePunchOutlinePreview();
        }

        private void OnClearPunchFaces(object sender, EventArgs e)
        {
            _punchFaceIndices = new List<int>();
            _lblPunchFaces.Text = "천공 벽면: (미선택 → 대상 표면과 동일)";
            AutoSetPunchDir(_faceIndices); // 대상 표면 법선으로 방향 복귀 (fallback)
            UpdatePunchFacesButtonText();
            SetStatus("천공 벽면 선택 해제됨");
            UpdatePunchOutlinePreview();
        }

        private void OnClearTargetSurface(object sender, EventArgs e)
        {
            _targetBrep = null;
            _targetObjectId = Guid.Empty;
            _faceIndices = new List<int>();
            _lblSurface.Text = "표면 선택 안됨";
            _placeRecompute = null; _placeCenter = null; // 대상 바뀌면 인터랙티브 배치 무효화
            SetStepDone(2, false);
            UpdateSurfaceButtonText();
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
            SetButtonActive(_btnPickPunchCurves, true);
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

        // 커터 미리보기(brep)가 떠 있는지에 따라 버튼 텍스트 갱신
        private void UpdateCutterPreviewButtonText()
        {
            bool shown = _preview.Breps != null && _preview.Breps.Count > 0;
            if (_btnCutterPreview != null) _btnCutterPreview.Text = shown ? "커터 미리보기 지우기" : "커터 미리보기 (Boolean 전)";
            SetButtonActive(_btnCutterPreview, shown);
        }

        // 하나의 버튼으로 커터 미리보기/지우기 토글
        private void OnToggleCutterPreview(object sender, EventArgs e)
        {
            bool shown = _preview.Breps != null && _preview.Breps.Count > 0;
            if (shown) OnClearPreview(sender, e);
            else OnPreviewCutters(sender, e);
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
            UpdateCutterPreviewButtonText();
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

            SetStepDone(4, true);
            SetStatus($"천공 완료 [{res.Stage}]: 성공 {res.SuccessCount}/{res.CutterCount} (폴백 {res.FallbackCount}, 실패 {res.FailedCount}, 벽없음 {res.NoWallCount}) — 구배 {draft:0.0}°");
        }
    }
}
