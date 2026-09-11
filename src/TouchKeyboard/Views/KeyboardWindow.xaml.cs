using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using TouchKeyboard.Diagnostics;
using TouchKeyboard.Input;
using TouchKeyboard.Interop;
using TouchKeyboard.Layout;
using TouchKeyboard.Settings;
using Windows.Graphics.Imaging;
using WinRT.Interop;

namespace TouchKeyboard.Views;

/// <summary>
/// レイアウト定義から生成し、AppBar で画面下部にドッキングする。
/// 描画とタップの受付のみを担い、入力ロジックは Input 層に閉じる。
/// </summary>
public sealed partial class KeyboardWindow : Window
{
    private readonly KeyDispatcher _dispatcher = new();
    private readonly List<KeyButton> _buttons = [];
    private readonly AppSettings _settings;

    /// <summary>押下中のキー。二重送出の防止と、終了時の取りこぼし防止を兼ねる。</summary>
    private readonly HashSet<KeyButton> _pressed = [];

    private Theme _theme = Theme.FromSystem();
    private WindowBackdrop? _backdrop;
    private LayoutDefinition? _layout;
    private DockManager? _dock;
    private nint _hwnd;
    private bool _isShown;

    // 高さドラッグの状態
    private bool _isResizing;
    private double _resizeStartScreenY;
    private double _resizeStartHeightDip;

    public nint Handle => _hwnd;

    public KeyboardWindow(AppSettings settings)
    {
        _settings = settings;

        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);

        // 既定のタイトルバーと枠を消す。残すと中身が押し下げられ、閉じるボタンも二重になる。
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        // タスクバーの一覧にも Alt+Tab にも出さない。
        AppWindow.IsShownInSwitchers = false;

        // 絶対制約 2。タップのたびに入力先のフォーカスが外れるのを防ぐ。
        //
        // プレゼンタの設定より後に行うこと。SetBorderAndTitleBar や IsAlwaysOnTop は
        // 内部でウィンドウスタイルを書き換えるため、先に付けると消される。
        EnsureNoActivate();


        _dispatcher.Modifiers.Changed += (_, _) => RefreshAllKeys();
        _dispatcher.Modifiers.Changed += (_, _) => CheckDebugScreenshotCombo();
        _dispatcher.Sent += (_, message) => Log(message, isError: false);
        _dispatcher.SendFailed += (_, ex) => Log($"送出失敗: {ex.Message}", isError: true);

        ApplyBackdrop();

        // 低電力モードの入切で Acrylic⇔Mica を切り替え直す（ApplyBackdrop 参照）。
        // 発火元のスレッドは UI スレッドとは限らないため、ディスパッチャへ渡す。
        PowerMode.Changed += OnEnergySaverStatusChanged;

        // 外周の 1px の枠を消す。画面端に接するため右下に線として残って見える。
        // 実測では SetBorderAndTitleBar(hasBorder: false) の後も WS_DLGFRAME が残っていた。
        WindowStyles.RemoveFrame(_hwnd);
        Theme.HideBorder(_hwnd);

        Title = "TouchKeyboard";

        WireGrip();
        WireEdges();
        WireTitleBarDrag();

        // パネルの外を触ったら閉じる。軽い打ち消しとして扱う。
        PanelScrim.PointerPressed += (_, e) =>
        {
            ClosePanel();
            e.Handled = true;
        };
        HideButton.Click += (_, _) => HideKeyboard();

        // 盤の高さが変わるたびに、その大きさで読める配列かを見直す。
        KeyRoot.SizeChanged += (_, _) => ReconsiderLayout();

        // 自分の面を触った時刻を控える。フォーカスの判定で、入力先に対する
        // 操作と区別するために使う。キーが処理した後でも拾えるよう、
        // 処理済みの通知も受け取る。
        //
        // 押した瞬間だけでは足りない。指を置いたまま繰り返し入力している間も
        // 生の入力（Raw Input）は届き続けるため、触れている間ずっと控え直す。
        // 動きは触れている間だけ数える。マウスを乗せただけで数えると、
        // そのまま別の窓をクリックした操作まで自分の操作に見えてしまう。
        var note = new PointerEventHandler((_, _) => LastOwnPointerAt = Environment.TickCount64);

        var noteWhileTouching = new PointerEventHandler((_, e) =>
        {
            if (e.Pointer.IsInContact) LastOwnPointerAt = Environment.TickCount64;
        });

        Shell.AddHandler(UIElement.PointerPressedEvent, note, handledEventsToo: true);
        Shell.AddHandler(UIElement.PointerReleasedEvent, note, handledEventsToo: true);
        Shell.AddHandler(UIElement.PointerMovedEvent, noteWhileTouching, handledEventsToo: true);

        LoadLayout();
        ApplyTheme();

        _dock = new DockManager(_hwnd);

        // 起動直後は隠しておく。表示は App が設定に応じて決める。
        AppWindow.Hide();
    }

    // ------------------------------------------------------------------
    // レイアウトの読み込みと生成
    // ------------------------------------------------------------------

    /// <summary>
    /// 行の高さの下限。DIP。これを下回る行があれば代わりの配列に切り替える。
    ///
    /// 字はキーの高さの半分を上限とするので（KeyButton）、28 で 14 になる。
    /// 他の段が 16 で出ているところに 14 の段が混ざる大きさで、
    /// 読めなくなるより手前、細さが目に付き始める辺りに置いてある。
    ///
    /// クラシックのファンクション段は全体の 1/11 の高さなので、
    /// キー段が 308 を下回ると切り替わる。
    /// </summary>
    private const double MinRowHeightDip = 28;

    /// <summary>
    /// 戻すときに求める余分。DIP。
    /// 同じ値で行き来させると、境目でつまみを動かすたびに配列が入れ替わる。
    /// </summary>
    private const double RowHeightHysteresisDip = 4;

    /// <summary>設定で選ばれている配列。細くて切り替えている間も、こちらは保持する。</summary>
    private LayoutDefinition? _selected;

    /// <summary>いま実際に出している配列のファイル名。</summary>
    private string? _activeLayoutFile;

    private void LoadLayout()
    {
        try
        {
            _selected = LayoutLoader.Load(LayoutLoader.PathOf(_settings.LayoutFile));

            var file = EffectiveLayoutFile(_selected);

            _layout = string.Equals(file, _settings.LayoutFile, StringComparison.OrdinalIgnoreCase)
                ? _selected
                : LayoutLoader.Load(LayoutLoader.PathOf(file));

            _activeLayoutFile = file;
        }
        catch (Exception ex)
        {
            // 選んだ配列が読めないだけなら既定に戻す。
            // ここで諦めるとキーが 1 つも出ず、設定を直す手立ても無くなる。
            Log($"レイアウト読み込み失敗: {ex.Message}", isError: true);

            if (_settings.LayoutFile == LayoutLoader.DefaultFileName) return;

            try
            {
                _layout = _selected = LayoutLoader.LoadDefault();
                _settings.LayoutFile = _activeLayoutFile = LayoutLoader.DefaultFileName;
            }
            catch (Exception fallback)
            {
                Log($"レイアウト読み込み失敗: {fallback.Message}", isError: true);
                return;
            }
        }

        Title = $"TouchKeyboard — {_layout.Name}";
        Populate(KeyRoot, _layout);

        // かなを刻む配列かどうかで、IME を見るかが変わる。
        if (_isShown) StartStateWatch();
    }

    /// <summary>
    /// 実際に出す配列のファイル名を決める。
    ///
    /// 選ばれた配列に他より低い行があると、盤を小さくしたときにその行だけ先に読めなくなる。
    /// 字を大きくしても行に収まらないので、その段を落とした派生に差し替える。
    /// クラシックならモダンに当たる。設定は書き換えないので、大きくすれば戻る。
    /// </summary>
    private string EffectiveLayoutFile(LayoutDefinition selected)
    {
        if (selected.CompactAlternative is not { Length: > 0 } alternative)
        {
            return _settings.LayoutFile;
        }

        // まだ配置されていない。大きさが決まった時点で ReconsiderLayout が呼び直す。
        var body = KeyRoot.ActualHeight;
        if (body <= 0) return _activeLayoutFile ?? _settings.LayoutFile;

        var units = selected.TotalHeightUnits;
        if (units <= 0) return _settings.LayoutFile;

        var shortest = body * (selected.ShortestRowUnits / units);

        var onAlternative = string.Equals(
            _activeLayoutFile, alternative, StringComparison.OrdinalIgnoreCase);

        var threshold = onAlternative
            ? MinRowHeightDip + RowHeightHysteresisDip
            : MinRowHeightDip;

        return shortest < threshold ? alternative : _settings.LayoutFile;
    }

    /// <summary>
    /// 大きさが変わったときに、出す配列を見直す。
    /// 変わらなければ何もしない。組み直しは配列が入れ替わるときだけ。
    /// </summary>
    private void ReconsiderLayout()
    {
        if (_selected is null) return;

        var file = EffectiveLayoutFile(_selected);
        if (string.Equals(file, _activeLayoutFile, StringComparison.OrdinalIgnoreCase)) return;

        // 並びが変わるのでラッチは持ち越さない。差し替え前に押さえていた
        // 修飾キーが、新しい面のどれに当たるか決められない。
        _pressed.Clear();
        _dispatcher.Modifiers.Clear();

        LoadLayout();
        RefreshAllKeys();
    }

    /// <summary>
    /// 使う配列を差し替える。
    ///
    /// キーの並びが変わるため組み直す。ラッチは持ち越さない。
    /// 差し替えの前に押さえていた修飾キーが、新しい面のどれに当たるか決められない。
    /// </summary>
    public void SetLayout(string fileName)
    {
        if (string.Equals(_settings.LayoutFile, fileName, StringComparison.OrdinalIgnoreCase)) return;

        _settings.LayoutFile = fileName;
        _settings.Save();

        _pressed.Clear();
        _dispatcher.Modifiers.Clear();

        LoadLayout();
        RefreshAllKeys();

        // 配列ごとに要る高さが違う。その配列で最後に使った大きさに戻す。
        if (_isShown) ApplyPlacement();
    }

    /// <summary>
    /// 定義を任意のグリッドへ流し込む。本体とテンキーのパネルで共用する。
    /// </summary>
    private void Populate(Grid target, LayoutDefinition layout)
    {
        target.Children.Clear();
        target.RowDefinitions.Clear();

        // 控えは本体の盤のぶんだけ。テンキーを組むときに捨ててしまわないようにする。
        if (ReferenceEquals(target, KeyRoot)) _fnRows.Clear();

        // 行の高さは定義の倍率で配分する。ファンクション段だけ低くする、といった指定ができる。
        // overlayPrevious の行は新しい行を消費せず、直前と同じ位置に重ねる。
        var placements = layout.RowPlacements();

        // 結合するキーを行をまたいで対応付けるため、置いた位置ごとに占有範囲を控える。
        var spans = new List<List<KeySpan>>();

        for (var i = 0; i < layout.Rows.Count; i++)
        {
            var definition = layout.Rows[i];

            // 重ねる行は行定義を増やさない。高さは重なる先の行が決める。
            if (target.RowDefinitions.Count <= placements[i])
            {
                target.RowDefinitions.Add(new RowDefinition
                {
                    Height = new GridLength(definition.Height, GridUnitType.Star),
                });
            }

            // 重ねた行は同じ位置のキーとして扱う。結合の判定も同じ並びで行う。
            while (spans.Count <= placements[i]) spans.Add([]);

            var row = BuildRow(definition, layout, spans[placements[i]]);
            Grid.SetRow(row, placements[i]);

            // またがる行は下の行にも重なる。重なる位置は相手側がスペーサーで空けてある。
            // 後から追加した行が上に描かれるので、差し込むキーは後ろに置くこと。
            if (definition.RowSpan > 1)
            {
                Grid.SetRowSpan(row, definition.RowSpan);
            }

            target.Children.Add(row);
        }

        LinkMergedKeys(spans);

        // 中央に置く配列では端の列を結び付けない。基準も押し出しも要らない。
        if (!layout.CenterLabels) LinkEdgeKeys(spans);
    }

    /// <summary>
    /// 端の列で字の位置を縦に揃える。
    ///
    /// 基準は列の中で最も狭いキー。自分のキーの中央に字を置き、
    /// 同じ列の残りをその位置へ合わせる。
    /// クラシックでもモダンでも、左は Ctrl、右は → になる。
    /// </summary>
    private static void LinkEdgeKeys(List<List<KeySpan>> rows)
    {
        var buttons = rows.SelectMany(row => row).Select(span => span.Button).ToList();

        LinkEdge(buttons, RowEdge.Left);
        LinkEdge(buttons, RowEdge.Right);
    }

    private static void LinkEdge(List<KeyButton> buttons, RowEdge edge)
    {
        var column = buttons.Where(button => button.Edge == edge).ToList();
        if (column.Count == 0) return;

        // 最も狭いキーを基準にする。そこで中央に置ければ、より広いキーは
        // 同じ位置まで必ず押し出せる。広いキーを基準にすると狭いキーが届かない。
        // 同じ幅が並ぶときは上の行のものを採る。
        var anchor = column.MinBy(button => button.Definition.Width)!;
        anchor.IsEdgeAnchor = true;

        foreach (var button in column) button.FollowEdge(anchor);
    }

    /// <summary>キーが行内で占める横方向の範囲。結合相手を探すために使う。</summary>
    private readonly record struct KeySpan(KeyButton Button, double Start, double End)
    {
        public double Width => End - Start;
    }

    /// <summary>
    /// 上下に隣り合う結合指定のキーを対応付ける。
    ///
    /// 幅が違えば L 字になる。JIS の Enter は上段が左へ 0.25 ユニット張り出すため、
    /// どちら側がどれだけ露出するかをここで算出して各キーに渡す。
    /// </summary>
    private static void LinkMergedKeys(List<List<KeySpan>> rows)
    {
        for (var r = 0; r + 1 < rows.Count; r++)
        {
            foreach (var upper in rows[r].Where(s => s.Button.Definition.MergeDown))
            {
                foreach (var lower in rows[r + 1].Where(s => s.Button.Definition.MergeUp))
                {
                    // 横に重なっていなければ、たまたま同じ指定を持つ別のキー。
                    var overlap = Math.Min(upper.End, lower.End) - Math.Max(upper.Start, lower.Start);
                    if (overlap <= 0.001) continue;

                    upper.Button.MergeWith(
                        lower.Button, ExposedLeft(upper, lower), ExposedRight(upper, lower));

                    lower.Button.MergeWith(
                        upper.Button, ExposedLeft(lower, upper), ExposedRight(lower, upper));
                }
            }
        }
    }

    /// <summary>自分の左端が相手より外に出ている割合。</summary>
    private static double ExposedLeft(KeySpan self, KeySpan other) =>
        Math.Max(0, other.Start - self.Start) / self.Width;

    /// <summary>自分の右端が相手より外に出ている割合。</summary>
    private static double ExposedRight(KeySpan self, KeySpan other) =>
        Math.Max(0, self.End - other.End) / self.Width;

    /// <summary>
    /// Fn 段で幅の変わる行。列と、そこに置いたキーを控える。
    ///
    /// 組み直さずに幅だけを差し替えるために持つ。組み直すと、Fn を押さえたままの
    /// キー自身が作り直され、指を離した通知が届かずに押しっぱなしのまま残る。
    /// </summary>
    private readonly List<List<(ColumnDefinition Column, KeyButton Button)>> _fnRows = [];

    private Grid BuildRow(KeyRow row, LayoutDefinition layout, List<KeySpan> spans)
    {
        var grid = new Grid();
        var rowUnits = layout.RowUnits;
        var cells = new List<(ColumnDefinition Column, KeyButton Button)>();

        var used = 0.0;
        for (var i = 0; i < row.Keys.Count; i++)
        {
            var def = row.Keys[i];

            var column = new ColumnDefinition
            {
                Width = new GridLength(def.Width, GridUnitType.Star),
            };

            grid.ColumnDefinitions.Add(column);

            var button = BuildKey(def, row.Height, layout.StackedLabels);
            cells.Add((column, button));

            // 併記するキーの寄せ方に使う。中心が行の右半分にあるかで決める。
            button.OnRightHalf = used + (def.Width / 2) >= rowUnits / 2;

            // 盤の端に接するキーは、その端の向きへ字を寄せる。
            // 位置で判定する。隙間が置かれた行では、キーの並び順と端は一致しない。
            button.Edge =
                layout.CenterLabels ? RowEdge.None :
                used < 0.001 ? RowEdge.Left :
                used + def.Width > rowUnits - 0.001 ? RowEdge.Right :
                RowEdge.None;

            button.CenterFace = layout.CenterLabels;

            Grid.SetColumn(button, i);
            grid.Children.Add(button);

            if (!def.Spacer) spans.Add(new KeySpan(button, used, used + def.Width));
            used += def.Width;
        }

        // Fn 段で構成の変わる行だけ控える。他の行は幅を触らない。
        if (cells.Any(cell => cell.Button.Definition.FnHidden)) _fnRows.Add(cells);

        // 行の幅が rowUnits に満たない分は右端の隙間にする。
        // これで行ごとのキー数が違っても縦の位置が揃う。
        var remainder = rowUnits - used;
        if (remainder > 0.001)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(remainder, GridUnitType.Star),
            });
        }

        return grid;
    }

    private KeyButton BuildKey(KeyDefinition def, double rowHeight, bool stacked = false)
    {
        var button = new KeyButton(def, _theme, rowHeight, stacked);

        if (!def.Spacer)
        {
            var (delay, interval) = KeyRepeat.Resolve(
                _settings.RepeatDelayMs, _settings.RepeatIntervalMs);
            button.SetRepeatTiming(delay, interval);

            // 送出は Click で行う。RepeatButton は押下時に 1 回、
            // その後 Delay を置いて Interval ごとに Click を上げる。
            button.Click += Key_Click;
            button.Released += Key_Released;

            _buttons.Add(button);
        }

        return button;
    }

    // ------------------------------------------------------------------
    // テーマ
    // ------------------------------------------------------------------

    /// <summary>
    /// 設定の素材でウィンドウの背景を敷き直す。設定画面からも呼ぶ。
    ///
    /// 素材を差し替えるにはコントローラを作り直す必要がある。
    /// 同じウィンドウに 2 つ登録すると後から入れたほうだけが効き、前のが残る。
    /// </summary>
    public void ApplyBackdrop()
    {
        _backdrop?.Dispose();
        _backdrop = new WindowBackdrop(this, _theme.IsDark, _settings.Backdrop);

        // 素材を適用しない間（低電力モード中など）は Shell 自体が透明になり、
        // 下の画面が透けてしまう。単色で塗って埋める。
        Shell.Background = _backdrop.IsApplied ? null : _theme.PanelBackground;

        if (!_backdrop.IsApplied)
        {
            Log($"背景の素材を適用できません: {_backdrop.Status}", isError: true);
        }

    }

    private void ApplyTheme()
    {
        LogText.Foreground = _theme.SecondaryForeground;
        GripBar.Background = _theme.SecondaryForeground;

        // タイトルバーは本体よりわずかに沈ませ、下辺に境界線を引く。
        // 素材の上に薄く重ねるだけにして、キーの見え方を変えない。
        TitleBarFill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
            _theme.IsDark ? (byte)0x24 : (byte)0x14,
            _theme.IsDark ? (byte)0x00 : (byte)0xFF,
            _theme.IsDark ? (byte)0x00 : (byte)0xFF,
            _theme.IsDark ? (byte)0x00 : (byte)0xFF));
        TitleBarLine.BorderBrush = _theme.KeyBorder;

        // パネルが覆っていない部分。ContentDialog と同じく、下を沈めて手前を際立たせる。
        // 触ったときに閉じる面でもあるため、透明ではなく塗りを持たせる必要がある。
        // 濃さは引き出し量に応じて Opacity で変える。
        PanelScrim.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(_theme.IsDark ? (byte)0x99 : (byte)0x66, 0, 0, 0));

        if (Root is FrameworkElement root)
        {
            root.RequestedTheme = _theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        }

        _backdrop?.SetTheme(_theme.IsDark);

        // 低電力モード中など素材を適用していない間は、ライト／ダーク切り替えでも
        // フォールバックの単色を塗り直す必要がある。
        if (_backdrop is { IsApplied: false })
        {
            Shell.Background = _theme.PanelBackground;
        }

        foreach (var button in _buttons)
        {
            button.ApplyTheme(_theme);
        }

        RefreshAllKeys();
    }

    /// <summary>
    /// OS のライト／ダーク切り替えとアクセントカラーの変更に追従する。App から呼ぶ。
    /// </summary>
    public void RefreshTheme()
    {
        var updated = Theme.FromSystem();

        // 明暗が同じでもアクセントだけ変わることがある。灯りの色に使っている。
        if (updated.IsDark == _theme.IsDark && updated.AccentColor == _theme.AccentColor) return;

        _theme = updated;
        ApplyTheme();
    }

    // ------------------------------------------------------------------
    // ドッキング
    // ------------------------------------------------------------------

    /// <summary>ドッキングして作業領域を確保する。</summary>
    private void Dock()
    {
        _dock?.Dock(_settings.HeightDip, _settings.MonitorDeviceName, _settings.CoverTaskbar);

        // 上限はモニタ依存なので、ドッキング先が確定してから保存値も丸める。
        if (_dock?.CurrentMonitor is { } monitor)
        {
            _settings.HeightDip = AppSettings.ClampHeight(_settings.HeightDip, monitor.HeightDip);
        }

        // 画面端に接しているならタイトルバーは要らない。高さ調整のグリップだけ残す。
        // 表示の切り替えと終了はタスクトレイから行う。
        SetTitleBarVisible(IsFloating);
    }

    /// <summary>
    /// フローティング専用のタイトルバーを出し入れする。
    ///
    /// ドッキング中は出さない。移動も閉じる操作も要らず、その分をキーに使える。
    /// </summary>
    private void SetTitleBarVisible(bool visible)
    {
        TitleBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 状態とエラーの行を出し入れする。普段は畳んでおき、エラーのときだけ開く。
    /// </summary>
    private void SetStatusVisible(bool visible)
    {
        HeaderBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        HeaderRow.Height = visible ? new GridLength(24) : new GridLength(0);
    }

    /// <summary>タスクバーを覆うかどうかを切り替える。</summary>
    public void SetCoverTaskbar(bool cover)
    {
        _settings.CoverTaskbar = cover;
        _settings.Save();

        if (_isShown) Dock();
    }

    /// <summary>
    /// WS_EX_NOACTIVATE を確実に付ける。
    ///
    /// AppWindow の操作でウィンドウスタイルが書き換えられることがあるため、
    /// 表示のたびに付け直す。ここが外れると入力先のフォーカスを奪ってしまい、
    /// このアプリの前提が崩れる。
    /// </summary>
    private bool EnsureNoActivate()
    {
        WindowStyles.ApplyNoActivate(_hwnd);
        return WindowStyles.HasExStyle(_hwnd, 0x08000000); // WS_EX_NOACTIVATE
    }

    /// <summary>表示する。AppBar を登録し直して作業領域を確保する。</summary>
    /// <param name="remember">
    /// 表示状態を設定に残すか。自動表示では false を渡す。
    /// 自動での開閉を書き込むと、設定が利用者の意思を表さなくなるうえ、
    /// フォーカスが動くたびにファイルへ書き込むことになる。
    /// </param>
    public void ShowKeyboard(bool remember = true)
    {
        // 既に出ているなら作業領域には触らない。自動表示で入力欄を移るたびに
        // AppBar を登録し直すと、他アプリの再レイアウトを連発させる。
        //
        // ただし Z 順だけは取り直す。スタートメニューなどシェルの UI が開くと
        // その下へ潜ってしまうため。
        if (_isShown && !remember)
        {
            _dock?.RaiseToTop();
            return;
        }

        // 表示する前に付け直す。Show の時点で外れているとフォーカスを奪う。
        EnsureNoActivate();

        // 先に位置を確定させてから表示する。
        // 表示を先にすると、確定前の位置で一度描画されてちらつく。
        ApplyPlacement();

        if (!_isShown)
        {
            // 開き直しは初期の見た目から始める。
            ResetToInitialView();

            AppWindow.Show(activateWindow: false);
            _isShown = true;

            // Show で Z 順とスタイルが戻ることがあるため、両方取り直す。
            var ok = EnsureNoActivate();
            ApplyPlacement();

            if (!ok) Log("WS_EX_NOACTIVATE を維持できていません", isError: true);

            StartStateWatch();
        }

        if (!remember) return;

        // 手動で出した。以後は自動では隠さない。
        _shownManually = true;

        _settings.Visible = true;
        _settings.Save();
    }

    /// <summary>
    /// 出すたびに初期の見た目へ戻す。
    ///
    /// 前に開いていたときのパネルやラッチが残っていると、出した瞬間に
    /// 押した覚えのない状態から始まることになる。クリップボード履歴を開いたまま
    /// 隠し、しばらく後に別のアプリで出したときが分かりやすい。
    ///
    /// グリップの短押しメニュー（テンキー・クリップボード履歴を開くボタンを含む）も
    /// 同じ扱いにする。開いたまま隠れて表示し直したときに残っていると、
    /// 押した覚えのないメニューが出ているように見える。
    /// </summary>
    private void ResetToInitialView()
    {
        ResetPanels();
        ResetMenu();

        _pressed.Clear();
        _dispatcher.Modifiers.Clear();

        RefreshAllKeys();
    }

    /// <summary>
    /// 手動で出したものか。自動で隠すかどうかの判断に使う。
    ///
    /// 自分で出したものが、こちらの見ていない都合——フォーカスの移動、
    /// マウスへの持ち替え、物理キーボードの接続——で消えると、出し直す手間が続く。
    /// 手動で出したものは手動でしか隠さない。
    /// </summary>
    private bool _shownManually;

    /// <summary>
    /// 実際に隠れた（表示中だったものが非表示になった）。
    /// 引数は <see cref="HideKeyboard"/> に渡された remember の値。
    /// 利用者が自分の意思で隠した（スワイプ、閉じるボタンなど）かどうかを表す。
    /// </summary>
    public event EventHandler<bool>? Hidden;

    /// <summary>隠す。AppBar を解除して作業領域を返す。</summary>
    /// <param name="remember">表示状態を設定に残すか。自動表示では false を渡す。</param>
    /// <param name="focusLost">
    /// 入力欄からフォーカスが外れたことによる非表示か。
    ///
    /// 手動で出したものは、この理由では隠さない。自分で出しておいたものが
    /// 別の窓を覗いただけで消えると、そのつど出し直すことになる。
    /// マウスへの持ち替えや物理キーボードは、打つ手段が変わった合図なので隠す。
    /// </param>
    public void HideKeyboard(bool remember = true, bool focusLost = false)
    {
        // 既に隠れているなら触らない。ABM_REMOVE を余計に通さない。
        if (!_isShown) return;

        if (!remember && focusLost && _shownManually)
        {
            TraceLog.Write("  -> 非表示を見送り（手動で表示中）");
            return;
        }

        _shownManually = false;

        StopStateWatch();
        _dock?.Undock();

        // 出し直すときは ApplyPlacement が置き直す。ここでは触らない。
        SetStatusVisible(false);

        AppWindow.Hide();
        _isShown = false;

        // AppBar を外すと、確保していた分だけ他アプリの作業領域が広がり、
        // 相手が再レイアウトすることがある。そのとき入力欄がフォーカスを
        // 取り直すと、監視側は「入力欄に来た」と見分けがつかず、隠した
        // そばから出し直してしまう。隠した事実と、自分の意思で隠したかを伝え、
        // そちらで見分けてもらう。
        Hidden?.Invoke(this, remember);

        if (!remember) return;

        _settings.Visible = false;
        _settings.Save();
    }

    /// <summary>外部から不具合を知らせる。ヘッダーを開いて表示する。</summary>
    public void ReportError(string message) => Log(message, isError: true);

    /// <summary>いま画面に出ているか。</summary>
    public bool IsShown => _isShown;

    /// <summary>
    /// 自分の面が最後に触られた時刻。<see cref="Environment.TickCount64"/>。
    /// 入力先のアプリに対する操作と区別するために、App が参照する。
    /// </summary>
    public long LastOwnPointerAt { get; private set; }

    public void ToggleKeyboard()
    {
        if (_isShown) HideKeyboard();
        else ShowKeyboard();
    }

    /// <summary>いま載っているモニタの高さ。DIP。設定画面が高さの上限を出すのに使う。</summary>
    public double CurrentMonitorHeightDip =>
        _dock?.CurrentMonitor?.HeightDip ?? AppSettings.AbsoluteMaxHeightDip;

    /// <summary>ドッキング先モニタを変更する。null なら自動（ウィンドウの載っているモニタ）。</summary>
    public void SetMonitor(string? deviceName)
    {
        _settings.MonitorDeviceName = deviceName;
        _settings.Save();

        // モニタが変われば覚えている大きさも変わる。置き直しから通す。
        ApplyPlacement();
    }

    /// <summary>AppBar 通知や DPI 変更を受けて位置を取り直す。App のフックから呼ぶ。</summary>
    public void RefreshDock()
    {
        // 画面を回すと組み合わせが変わる。覚えている大きさへの読み替えは
        // 隠れていても行う。次に出したときに前回の向きの高さを引きずらないため。
        //
        // ただし置き直す（ApplyPlacement → Dock）のは表示中だけ。隠れている間に
        // 通すと、AppBar を新規登録して作業領域を確保してしまう。表示していない
        // のに画面の下が空くのはこれが原因だった。
        var contextChanged = UsePlacementContext();

        if (!_isShown) return;

        if (contextChanged)
        {
            ApplyPlacement();
            return;
        }

        _dock?.Refresh(_settings.HeightDip, _settings.MonitorDeviceName, _settings.CoverTaskbar);
    }

    /// <summary>
    /// いまの組み合わせに合わせて、覚えている大きさと位置を引き当てる。
    ///
    /// 配列・浮かせているか・モニタ・画面の向きで分ける。同じ配列でも縦に回せば
    /// 入る大きさが違い、モニタを移せば適した高さも違う。
    ///
    /// 配列は「選んだもの」で見る。細いときに自動で差し替わる配列で見ると、
    /// 高さを変えるたびに配列が変わり、その配列の高さがまた読み込まれて往復する。
    /// </summary>
    /// <returns>組み合わせが変わったら true。</returns>
    private bool UsePlacementContext()
    {
        var monitor = _dock?.CurrentMonitor
                      ?? MonitorInfo.Resolve(_settings.MonitorDeviceName, _hwnd);

        var device = monitor?.DeviceName ?? "?";

        var orientation = monitor is null
            ? "?"
            : monitor.Bounds.Width >= monitor.Bounds.Height ? "横" : "縦";

        var mode = IsFloating ? "浮" : "端";

        return _settings.UseContext($"{_settings.LayoutFile}|{mode}|{device}|{orientation}");
    }

    /// <summary>AppBar 通知用のメッセージ ID。App のフックで判定に使う。</summary>
    public uint AppBarCallbackMessage => _dock?.CallbackMessage ?? 0;

    // ------------------------------------------------------------------
    // 高さの変更
    // ------------------------------------------------------------------
    //
    // ドラッグ中は AppBar を経由せずウィンドウだけを動かす。
    // 移動のたびに ABM_QUERYPOS / ABM_SETPOS を通すと他アプリの再レイアウトが連発する。
    // 確定は指を離したときに行う。

    /// <summary>長押しと判断するまでの時間。これを超えて押されていれば高さ変更に入る。</summary>
    private const int GripLongPressMs = 350;

    /// <summary>下へスワイプして隠すと判断する移動量（DIP）。</summary>
    private const double SwipeToHideDip = 40;

    /// <summary>
    /// これだけ動いたら長押しとは見なさない（DIP）。
    /// 指のわずかな揺れで高さ変更が始まらない程度に取る。
    /// </summary>
    private const double LongPressSlopDip = 12;

    /// <summary>長押しの判定中。まだ高さ変更にもスワイプにも確定していない。</summary>
    private bool _gripPending;

    private DispatcherQueueTimer? _gripTimer;

    private void WireGrip()
    {
        // 長押しで高さ変更、短く押して下へスワイプで非表示。
        // 押した時点ではどちらか決まらないので、時間か移動量で確定させる。
        _gripTimer = DispatcherQueue.CreateTimer();
        _gripTimer.Interval = TimeSpan.FromMilliseconds(GripLongPressMs);
        _gripTimer.IsRepeating = false;
        _gripTimer.Tick += (_, _) =>
        {
            // 移動はタイトルバーが担うので、グリップは常に高さ変更でよい。
            if (_gripPending) BeginResize();
        };

        ResizeGrip.PointerPressed += (sender, e) =>
        {
            var element = (UIElement)sender;
            element.CapturePointer(e.Pointer);

            _gripPending = true;
            _gripGrab = e.GetCurrentPoint(Shell).Position;
            _resizeStartScreenY = ScreenY(e);
            _resizeStartHeightDip = _settings.HeightDip;

            _gripTimer.Start();
            e.Handled = true;
        };

        ResizeGrip.PointerMoved += (sender, e) =>
        {
            // ハンドルは画面端に接している間だけ出る。移動はここでは扱わない。
            var y = ScreenY(e);

            if (_isResizing)
            {
                UpdateResize(y);
                e.Handled = true;
                return;
            }

            if (!_gripPending) return;

            // 画面座標は下方向が正。押した位置からの差で向きを見る。
            var scale = _dock?.CurrentMonitor?.DpiScale ?? 1.0;
            var moved = (y - _resizeStartScreenY) / scale;

            // 動き始めたら、もう長押しではない。タイマーを止めて高さ変更へ入らせない。
            // これを止めないと、上へゆっくり引いている間に 350ms が過ぎて動き出す。
            if (Math.Abs(moved) >= LongPressSlopDip) _gripTimer?.Stop();

            // 下へ払ったときだけ隠す。上方向や小さな動きでは何もしない。
            if (moved >= SwipeToHideDip)
            {
                EndGrip();
                ((UIElement)sender).ReleasePointerCapture(e.Pointer);
                HideKeyboard();
            }

            e.Handled = true;
        };

        ResizeGrip.PointerReleased += (sender, e) =>
        {
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);

            // 長押しにもスワイプにも至らなかったなら短い押下。メニューを出す。
            var wasTap = !_isResizing && _gripPending;

            if (_isResizing) EndResize();
            EndGrip();

            if (wasTap) ToggleMenu();

            e.Handled = true;
        };

        ResizeGrip.PointerCaptureLost += (_, _) =>
        {
            if (_isResizing) EndResize();
            EndGrip();
        };
    }

    // ------------------------------------------------------------------
    // 端からのスワイプ
    // ------------------------------------------------------------------

    /// <summary>スワイプの開始位置。</summary>
    private Windows.Foundation.Point _edgeStart;

    /// <summary>グリップを掴んだ位置。移動の追従に使う。</summary>
    private Windows.Foundation.Point _gripGrab;
    private bool _edgeTracking;

    private void WireEdges()
    {
        // 上端の左右から下へ引き下ろす。引いた距離がそのままパネルの位置になる。
        //
        // 画面の左右端は使わない。そこは Windows のエッジジェスチャの領域で、
        // アプリより先に処理されて届かない。ウィンドウ単位で無効にする API も無い。
        // キーボードの上端は画面の中ほどにあるため、この問題が起きない。
        Wire(LeftEdge, PanelSide.Left, moved => moved.Y);
        Wire(RightEdge, PanelSide.Right, moved => moved.Y);

        void Wire(UIElement edge, PanelSide side, Func<Windows.Foundation.Point, double> pulled)
        {
            edge.PointerPressed += (sender, e) =>
            {
                _edgeStart = e.GetCurrentPoint(Shell).Position;
                _edgeTracking = BeginPanelDrag(side);

                if (_edgeTracking) ((UIElement)sender).CapturePointer(e.Pointer);
                e.Handled = true;
            };

            edge.PointerMoved += (_, e) =>
            {
                if (!_edgeTracking) return;

                var current = e.GetCurrentPoint(Shell).Position;
                UpdatePanelDrag(pulled(new Windows.Foundation.Point(
                    current.X - _edgeStart.X, current.Y - _edgeStart.Y)));

                e.Handled = true;
            };

            edge.PointerReleased += (sender, e) =>
            {
                if (!_edgeTracking) return;

                _edgeTracking = false;
                ((UIElement)sender).ReleasePointerCapture(e.Pointer);

                // ほとんど動いていないなら EndPanelDrag が畳む。
                EndPanelDrag();

                e.Handled = true;
            };

            edge.PointerCaptureLost += (_, _) =>
            {
                if (!_edgeTracking) return;

                _edgeTracking = false;
                CancelPanelDrag();
            };
        }
    }

    // ------------------------------------------------------------------
    // テンキーのパネル
    // ------------------------------------------------------------------

    /// <summary>テンキーの定義。初回に読み、以降は表示を切り替えるだけ。</summary>
    private LayoutDefinition? _numpadLayout;

    private bool LoadNumpad()
    {
        try
        {
            _numpadLayout = LayoutLoader.Load(LayoutLoader.PathOf("numpad.json"));
            Populate(NumpadRoot, _numpadLayout);
            return true;
        }
        catch (Exception ex)
        {
            Log($"テンキーの読み込み失敗: {ex.Message}", isError: true);
            return false;
        }
    }

    /// <summary>
    /// クリップボード履歴を開く。Win+V を送る。
    ///
    /// 自前で履歴を持たない。OS の履歴と二重管理になるうえ、
    /// 貼り付けの経路も作り直すことになる。
    /// </summary>
    private void SendClipboardHistory()
    {
        try
        {
            KeySender.TapWithModifiers(
                0x2F, extended: false, stackalloc[] { KeyEvent.Down(0x5B, extended: true) });
            Log("クリップボード履歴 (Win+V)", isError: false);
        }
        catch (Exception ex)
        {
            Log($"送出失敗: {ex.Message}", isError: true);
        }
    }

    private void BeginResize()
    {
        EndGrip();
        _isResizing = true;
    }

    /// <summary>長押しの判定を打ち切る。</summary>
    private void EndGrip()
    {
        _gripPending = false;
        _gripTimer?.Stop();
    }

    /// <summary>ポインタの Y 座標を物理ピクセルで得る。</summary>
    private double ScreenY(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null).Position;
        var scale = _dock?.CurrentMonitor?.DpiScale ?? 1.0;
        var top = _dock?.CurrentTop ?? 0;

        return top + (point.Y * scale);
    }

    private void UpdateResize(double screenY)
    {
        if (!_isResizing) return;

        // 上へドラッグすると高くなる。上限は画面高さの半分。
        var scale = _dock?.CurrentMonitor?.DpiScale ?? 1.0;
        var deltaDip = (_resizeStartScreenY - screenY) / scale;
        var monitorHeightDip = _dock?.CurrentMonitor?.HeightDip ?? AppSettings.AbsoluteMaxHeightDip;

        var height = AppSettings.ClampHeight(_resizeStartHeightDip + deltaDip, monitorHeightDip);

        _settings.HeightDip = height;
        _dock?.PreviewHeight(height, _settings.CoverTaskbar);
    }

    private void EndResize()
    {
        if (!_isResizing) return;
        _isResizing = false;

        // ここで初めて AppBar に確定させ、他アプリの作業領域を追従させる。
        Dock();
        _settings.Save();
    }

    // ------------------------------------------------------------------
    // キーの押下と解放
    // ------------------------------------------------------------------

    /// <summary>
    /// RepeatButton の Click。押下時に 1 回、その後リピートのたびに上がる。
    /// 1 回目は通常の入力、2 回目以降はリピートとして扱う。
    /// 違いは修飾キーのラッチを消費するかどうか。
    /// </summary>
    private void Key_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not KeyButton button) return;

        _sound.Play(SoundOf(button.Definition));

        if (_pressed.Add(button))
        {
            // 1 回目。ここで Fn / Shift のラッチが解決され、消費される。
            // ラッチが動けば ModifierState.Changed から全キーが描き直される。
            _dispatcher.Press(button.Definition);
        }
        else
        {
            // 2 回目以降。ラッチは消費しない。
            _dispatcher.Repeat(button.Definition);
        }

        // Caps Lock は定期確認（400ms 間隔）任せだと、自分で押した直後は
        // 灯りの切り替わりが遅れて見える。押した直後だけその場で確かめ直す。
        // 他の修飾キーと違い ModifierState.Changed からは分からない
        // （Caps Lock はこちらでラッチを管理していないため）。
        if (button.Definition.IsCapsLock) PollExternalState();
    }

    /// <summary>指が離れた・キーの外へ出た・キャンセルされた。</summary>
    private void Key_Released(object? sender, EventArgs e)
    {
        if (sender is not KeyButton button) return;

        _pressed.Remove(button);

        // 修飾キーは押さえている間ずっと有効。離した時点で後始末する。
        _dispatcher.Release(button.Definition);
    }

    /// <summary>打鍵の音。標準のタッチキーボードと同じ音源を鳴らす。</summary>
    private readonly KeySound _sound = new();

    /// <summary>
    /// そのキーに割り当てる音。標準のタッチキーボードと同じ分け方にする。
    /// スペースだけ別の音で、役割のキーはさらに別。
    /// </summary>
    private static KeySoundKind SoundOf(KeyDefinition key) =>
        key.ScanCode == SpaceScanCode ? KeySoundKind.Space :
        key.IsCharacter ? KeySoundKind.Tap :
        KeySoundKind.Function;

    private const ushort SpaceScanCode = 0x39;

    private LatchState LatchOf(KeyButton button)
    {
        if (button.Definition.IsModifier) return _dispatcher.Modifiers.DisplayState(button.Definition.Modifier);

        // Caps Lock はこちらでラッチを管理しない。掛かる・外れるは普通のキー送出の
        // 結果として OS 側で決まる（物理キーボードからでも切り替わる）。実際の状態を
        // 読みに行き、「ずっと掛かっている」灯りとして Locked にそのまま対応させる。
        // Latched（次の 1 文字で消える）にあたる状態は Caps Lock には無い。
        if (button.Definition.IsCapsLock) return _capsLockOn ? LatchState.Locked : LatchState.Off;

        return LatchState.Off;
    }

    private string LabelOf(KeyButton button) => _dispatcher.ResolveLabel(button.Definition);

    private string? IconOf(KeyButton button) => _dispatcher.ResolveIcon(button.Definition);

    /// <summary>いま Fn 段の割り当てを出しているキーか。</summary>
    private bool OnFnLayer(KeyButton button) =>
        _dispatcher.Modifiers.IsFnActive && button.Definition.HasFnLayer;

    // ------------------------------------------------------------------
    // デバッグ用スクリーンショット
    // ------------------------------------------------------------------
    //
    // リモートの開発環境からは実機の画面を直接確認する手段が無い（画面キャプチャは
    // この環境自身の画面しか映せない）。盤面の見た目の不具合を調べるには実機で
    // 撮ってもらうしかないため、Ctrl+Alt+Shift+Fn を同時に押したときにアプリ自身が
    // 盤面を画像として保存する、この開発中だけの入り口を用意する。

    /// <summary>直前に確認した時点で、4 つの同時押しが成立していたか。</summary>
    private bool _debugScreenshotComboActive;

    /// <summary>
    /// 4 つの修飾キーがすべて有効になった瞬間（成立していない→した、の立ち上がり）
    /// だけ撮る。成立したまま指を離さずにいる間、何度も撮り直さないようにするため。
    /// </summary>
    private void CheckDebugScreenshotCombo()
    {
        var modifiers = _dispatcher.Modifiers;
        var active = modifiers.IsActive(ModifierKind.Ctrl)
            && modifiers.IsActive(ModifierKind.Alt)
            && modifiers.IsActive(ModifierKind.Shift)
            && modifiers.IsFnActive;

        if (active && !_debugScreenshotComboActive) _ = CaptureDebugScreenshotAsync();

        _debugScreenshotComboActive = active;
    }

    /// <summary>
    /// 盤面（<see cref="Shell"/> 全体）をそのまま PNG で保存する。
    /// <c>%APPDATA%\TouchKeyboard\debug-screenshots\</c> に、日時をファイル名にして置く。
    ///
    /// 実際の解像度の等倍だと刻印 1 個が数十 px しかなく、細部の比較ができない。
    /// <see cref="RenderTargetBitmap.RenderAsync(UIElement, int, int)"/> の
    /// スケール指定オーバーロードで、見た目はそのままに大きく描き直させる。
    /// </summary>
    private async Task CaptureDebugScreenshotAsync()
    {
        try
        {
            const int scale = 6;
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(Shell, (int)(Shell.ActualWidth * scale), (int)(Shell.ActualHeight * scale));

            var pixels = await bitmap.GetPixelsAsync();

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TouchKeyboard", "debug-screenshots");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

            await using var stream = File.Create(path);
            using var randomAccessStream = stream.AsRandomAccessStream();

            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, randomAccessStream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight,
                96, 96,
                pixels.ToArray());
            await encoder.FlushAsync();

            // 手動で撮ったことの確認なので、普段は畳んでいる状態欄でも
            // エラーと同様にここだけは開いて見せる。
            Log($"デバッグ用スクリーンショットを保存しました: {path}", isError: false);
            SetStatusVisible(true);
        }
        catch (Exception ex)
        {
            Log($"デバッグ用スクリーンショットの保存に失敗: {ex.Message}", isError: true);
        }
    }

    // ------------------------------------------------------------------
    // 他プロセス・OS 側の状態（IME の入切、Caps Lock）
    // ------------------------------------------------------------------
    //
    // どちらも変わったことの通知が来ないため、こちらから定期的に見に行くしかない。
    // 元は IME 専用だったが、Caps Lock の実際の状態を映す仕組みにも同じ形が要るため、
    // まとめて 1 本のタイマーで見る。

    /// <summary>入力先の IME が入っているか。かなの刻印の濃さに使う。</summary>
    private bool _imeOpen = true;

    /// <summary>Caps Lock が掛かっているか。Caps キーの灯りに使う。</summary>
    private bool _capsLockOn;

    private DispatcherQueueTimer? _stateTimer;

    /// <summary>かなを刻む配列でのみ見る。他では問い合わせる意味が無い。</summary>
    private bool NeedsImeState =>
        _layout?.Rows.Any(row => row.Keys.Any(key => key.Kana is { Length: > 0 })) == true;

    /// <summary>
    /// IME の入切・Caps Lock の状態を定期的に確かめる。
    /// 見るのは表示中だけ。隠れている間に問い合わせても使い道が無い。
    /// </summary>
    private void StartStateWatch()
    {
        _stateTimer ??= CreateStateTimer();

        PollExternalState();
        _stateTimer.Start();
    }

    private void StopStateWatch() => _stateTimer?.Stop();

    private DispatcherQueueTimer CreateStateTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => PollExternalState();

        return timer;
    }

    private void PollExternalState()
    {
        var changed = false;

        // IME はかなを刻む配列のときだけ見る。他では問い合わせる意味が無い。
        if (NeedsImeState)
        {
            // 読めないときは前の値を保つ。取れないたびに濃淡が揺れると読みにくい。
            var open = ImeState.IsOpen(WindowStyles.ForegroundWindow()) ?? _imeOpen;
            if (open != _imeOpen)
            {
                _imeOpen = open;
                changed = true;
            }
        }

        // Caps Lock はどの配列でも常に見る。物理キーボードからでも切り替わるため。
        var capsOn = (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0;
        if (capsOn != _capsLockOn)
        {
            _capsLockOn = capsOn;
            changed = true;
        }

        if (changed) RefreshAllKeys();
    }

    /// <summary>
    /// 修飾キーの状態が変わったら全キーを更新する。
    /// Fn ラッチで数字段が F1〜F12 に変わり、Shift ラッチで記号の表示が変わる。
    /// </summary>
    private void RefreshAllKeys()
    {
        // 押下中のキーも除外しない。押下表示は KeyButton が IsPressed から決めるため、
        // ここで描き直しても押下中の見た目は保たれる。
        foreach (var button in _buttons)
        {
            button.Refresh(
                LatchOf(button),
                LabelOf(button),
                IconOf(button),
                OnFnLayer(button),
                _dispatcher.Modifiers.IsShiftActive,
                _imeOpen);
        }

        ApplyFnLayout(_dispatcher.Modifiers.IsFnActive);
    }

    /// <summary>
    /// Fn 段での段の構成を反映する。
    ///
    /// Fn 中は置かないキーを畳み、空いた幅を同じ行の文字キーが等分する。
    /// モダンの数字段では ¥ が畳まれ、13 ユニットぶんの場所を F1〜F12 の
    /// 12 個が等分する。Esc と BS は幅を変えず、位置も動かない。
    /// </summary>
    private void ApplyFnLayout(bool fnActive)
    {
        foreach (var cells in _fnRows)
        {
            var folded = cells
                .Where(cell => cell.Button.Definition.FnHidden)
                .Sum(cell => cell.Button.Definition.Width);

            // 受け取るのは Fn 段を持つ文字キー。Esc や BS のような役割のキーは広げない。
            var takers = cells
                .Where(cell => cell.Button.Definition is { HasFnLayer: true, IsCharacter: true })
                .ToList();

            if (folded <= 0 || takers.Count == 0) continue;

            var extra = folded / takers.Count;

            foreach (var (column, button) in cells)
            {
                var def = button.Definition;
                var fold = fnActive && def.FnHidden;

                var width = def.Width;
                if (fnActive && !fold && takers.Any(t => ReferenceEquals(t.Button, button)))
                {
                    width += extra;
                }

                column.Width = new GridLength(fold ? 0 : width, GridUnitType.Star);
                button.Visibility = fold ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private void Log(string message, bool isError)
    {
        LogText.Text = message;
        LogText.Foreground = isError
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x11, 0x23))
            : _theme.SecondaryForeground;

        // 普段は畳んでいるが、エラーは見えないと困るので開く。
        if (isError) SetStatusVisible(true);
    }

    // ------------------------------------------------------------------
    // 終了処理
    // ------------------------------------------------------------------

    /// <summary>
    /// ABM_REMOVE を確実に通す。呼ばずに終了すると他アプリの作業領域が縮んだまま残る。
    /// </summary>
    public void Teardown()
    {
        // 閉じる要求を止めなくする。
        // 標準の閉じるボタンのために取り消しているが、終了のときまで拒み続けると
        // ウィンドウが畳めず、後片付けがそこで止まる。
        _closable = true;

        // ラッチしたままの修飾キーを残さない（要件 F-1）。
        // 送出は押下時に完結しているため、ここでは状態を落とすだけでよい。
        _dispatcher.Modifiers.Clear();
        _pressed.Clear();

        _settings.Save();

        PowerMode.Changed -= OnEnergySaverStatusChanged;

        _backdrop?.Dispose();
        _backdrop = null;

        _dock?.Dispose();
        _dock = null;
    }

    /// <summary>低電力モードが入切した。背景の素材を選び直す。</summary>
    private void OnEnergySaverStatusChanged(object? sender, object e) =>
        DispatcherQueue.TryEnqueue(ApplyBackdrop);
}
