using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TouchKeyboard.Input;
using TouchKeyboard.Interop;
using TouchKeyboard.Layout;
using TouchKeyboard.Settings;

namespace TouchKeyboard.Views;

/// <summary>
/// Phase 3: レイアウト定義から生成し、AppBar で画面下部にドッキングする。
/// 描画とタップの受付のみを担い、入力ロジックは Input 層に閉じる。
/// </summary>
public partial class KeyboardWindow : Window
{
    private readonly KeyDispatcher _dispatcher = new();
    private readonly KeyRepeat _repeat = new();
    private readonly List<KeyButton> _buttons = [];
    private readonly AppSettings _settings;

    /// <summary>押下中のキー。二重送出の防止と、終了時の取りこぼし防止を兼ねる。</summary>
    private readonly HashSet<KeyButton> _pressed = [];

    private Theme _theme = Theme.FromSystem();
    private LayoutDefinition? _layout;
    private DockManager? _dock;
    private nint _hwnd;

    /// <summary>
    /// DWM の Acrylic バックドロップが適用できたか。
    /// 適用できた場合のみ WPF 側の背景を透明にする。失敗時に透明にすると、
    /// DWM が何も描かないぶん窓が黒く抜ける。
    /// </summary>
    private bool _backdropApplied;

    // 高さドラッグの状態
    private bool _isResizing;
    private double _resizeStartScreenY;
    private double _resizeStartHeightDip;

    public KeyboardWindow(AppSettings settings)
    {
        _settings = settings;

        InitializeComponent();

        _dispatcher.Modifiers.Changed += (_, _) => RefreshAllKeys();
        _dispatcher.Sent += (_, message) => Log(message, isError: false);
        _dispatcher.SendFailed += (_, ex) => Log($"送出失敗: {ex.Message}", isError: true);

        Height = _settings.HeightDip;

        LoadLayout();
        ApplyTheme();
    }

    // ------------------------------------------------------------------
    // レイアウトの読み込みと生成
    // ------------------------------------------------------------------

    private void LoadLayout()
    {
        try
        {
            _layout = LayoutLoader.LoadDefault();
        }
        catch (Exception ex)
        {
            // 定義が読めないとキーが 1 つも出ない。原因を画面に出す。
            Log($"レイアウト読み込み失敗: {ex.Message}", isError: true);
            return;
        }

        Title = $"TouchKeyboard — {_layout.Name}";

        // 行は縦方向にも均等に分ける。StackPanel だと行がコンテンツの高さにしか広がらず、
        // ウィンドウ下部に使われない余白が残る。
        for (var i = 0; i < _layout.Rows.Count; i++)
        {
            KeyRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var row = BuildRow(_layout.Rows[i], _layout.RowUnits);
            Grid.SetRow(row, i);
            KeyRoot.Children.Add(row);
        }
    }

    private Grid BuildRow(KeyRow row, double rowUnits)
    {
        var grid = new Grid();

        var used = 0.0;
        for (var i = 0; i < row.Keys.Count; i++)
        {
            var def = row.Keys[i];

            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(def.Width, GridUnitType.Star),
            });
            used += def.Width;

            var button = BuildKey(def);
            Grid.SetColumn(button, i);
            grid.Children.Add(button);
        }

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

    private KeyButton BuildKey(KeyDefinition def)
    {
        var button = new KeyButton(def, _theme);

        if (!def.Spacer)
        {
            button.PreviewTouchDown += Key_TouchDown;
            button.PreviewTouchUp += Key_TouchUp;
            button.PreviewMouseLeftButtonDown += Key_MouseDown;
            button.PreviewMouseLeftButtonUp += Key_MouseUp;
            _buttons.Add(button);
        }

        return button;
    }

    // ------------------------------------------------------------------
    // テーマ
    // ------------------------------------------------------------------

    private void ApplyTheme()
    {
        // バックドロップが効いているときは DWM に背景を描かせる。
        // ここを不透明に塗ると Acrylic が隠れてしまう。
        Background = _backdropApplied ? Brushes.Transparent : _theme.WindowBackground;
        LogText.Foreground = _theme.SecondaryForeground;
        GripBar.Background = _theme.SecondaryForeground;

        if (QuitButton.Child is TextBlock quitText)
        {
            quitText.Foreground = _theme.SecondaryForeground;
        }

        foreach (var button in _buttons)
        {
            button.ApplyTheme(_theme);
        }

        RefreshAllKeys();
    }

    // ------------------------------------------------------------------
    // ウィンドウとドッキング
    // ------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // ハンドルはここで初めて存在する。コンストラクタではまだ取得できない。
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowStyles.ApplyNoActivate(_hwnd);

        var source = HwndSource.FromHwnd(_hwnd);

        var backdrop = _theme.ApplyToWindow(_hwnd);
        _backdropApplied = backdrop.Applied;

        if (_backdropApplied && source?.CompositionTarget is { } target)
        {
            // WPF の描画面そのものを透明にする。これを省くと、フレームを広げても
            // WPF が不透明に塗り潰してしまい Acrylic が見えない。
            target.BackgroundColor = Colors.Transparent;
        }

        ApplyTheme();

        if (!_backdropApplied)
        {
            Log(backdrop.ToString(), isError: true);
        }

        source?.AddHook(WndProc);

        // ここではドッキングしない。作業領域の確保は実際に表示するときに行う。
        // 隠したまま起動する場合に領域を確保してしまうと、見えないキーボードのぶん
        // 他アプリの作業領域が縮んだままになる。
        _dock = new DockManager(_hwnd);
    }

    /// <summary>ドッキングして作業領域を確保する。</summary>
    private void Dock()
    {
        _dock?.Dock(_settings.HeightDip, _settings.MonitorDeviceName, _settings.CoverTaskbar);

        // 上限はモニタ依存なので、ドッキング先が確定してから保存値も丸める。
        // これを省くと、上限を超えた値が残ってドラッグの開始位置がずれる。
        if (_dock?.CurrentMonitor is { } monitor)
        {
            _settings.HeightDip = AppSettings.ClampHeight(_settings.HeightDip, monitor.HeightDip);
        }
    }

    /// <summary>タスクバーを覆うかどうかを切り替える。</summary>
    public void SetCoverTaskbar(bool cover)
    {
        _settings.CoverTaskbar = cover;
        _settings.Save();

        if (IsVisible) Dock();
    }

    /// <summary>
    /// 表示する。AppBar を登録し直して作業領域を確保する。
    /// </summary>
    public void ShowKeyboard()
    {
        // 先にドッキングして位置を確定させてから表示する。
        // Show を先にすると、確定前の位置で一度描画されてちらつく。
        // 非表示のウィンドウでも SetWindowPos は効き、WPF は
        // WM_WINDOWPOSCHANGED で Left/Top/Width/Height を追従させる。
        Dock();

        if (!IsVisible) Show();

        _settings.Visible = true;
        _settings.Save();
    }

    /// <summary>
    /// 隠す。AppBar を解除して作業領域を返す。
    /// </summary>
    public void HideKeyboard()
    {
        _dock?.Undock();
        Hide();

        _settings.Visible = false;
        _settings.Save();
    }

    public void ToggleKeyboard()
    {
        if (IsVisible) HideKeyboard();
        else ShowKeyboard();
    }

    /// <summary>ドッキング先モニタを変更する。</summary>
    public void SetMonitor(string deviceName)
    {
        _settings.MonitorDeviceName = deviceName;
        _settings.Save();
        Dock();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_DPICHANGED = 0x02E0;
        const int WM_DISPLAYCHANGE = 0x007E;

        // AppBar からの通知。位置が変わったら取り直す。
        if (_dock is not null && msg == (int)_dock.CallbackMessage)
        {
            if ((int)wParam == NativeMethods.ABN_POSCHANGED)
            {
                _dock.Refresh(_settings.HeightDip, _settings.MonitorDeviceName, _settings.CoverTaskbar);
            }

            return 0;
        }

        switch (msg)
        {
            case WM_SETTINGCHANGE:
                var updated = Theme.FromSystem();
                if (updated.IsDark != _theme.IsDark)
                {
                    _theme = updated;
                    _backdropApplied = _theme.ApplyToWindow(hwnd).Applied;
                    ApplyTheme();
                }

                break;

            // モニタ間移動やスケーリング変更。AppBar が確保した物理領域とずれるため取り直す。
            case WM_DPICHANGED:
            case WM_DISPLAYCHANGE:
                _dock?.Refresh(_settings.HeightDip, _settings.MonitorDeviceName, _settings.CoverTaskbar);
                break;
        }

        return 0;
    }

    // ------------------------------------------------------------------
    // 高さの変更
    // ------------------------------------------------------------------
    //
    // ドラッグ中は AppBar を経由せずウィンドウだけを動かす。
    // 移動のたびに ABM_QUERYPOS / ABM_SETPOS を通すと他アプリの再レイアウトが連発する。
    // 確定は指を離したときに行う。

    private void BeginResize(double screenY)
    {
        _isResizing = true;
        _resizeStartScreenY = screenY;
        _resizeStartHeightDip = _settings.HeightDip;
    }

    private void UpdateResize(double screenY)
    {
        if (!_isResizing) return;

        // 上へドラッグすると高くなる。上限は画面高さの半分。
        var deltaDip = (_resizeStartScreenY - screenY) / CurrentDpiScale();
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

    /// <summary>ドッキング先モニタの DPI スケール。画面座標と DIP の変換に使う。</summary>
    private double CurrentDpiScale() => _dock?.CurrentMonitor?.DpiScale ?? 1.0;

    private void Grip_TouchDown(object? sender, TouchEventArgs e)
    {
        if (sender is not UIElement element) return;

        element.CaptureTouch(e.TouchDevice);
        BeginResize(PointToScreen(e.GetTouchPoint(this).Position).Y);
        e.Handled = true;
    }

    private void Grip_TouchMove(object? sender, TouchEventArgs e)
    {
        if (!_isResizing) return;

        UpdateResize(PointToScreen(e.GetTouchPoint(this).Position).Y);
        e.Handled = true;
    }

    private void Grip_TouchUp(object? sender, TouchEventArgs e)
    {
        if (sender is not UIElement element) return;

        element.ReleaseTouchCapture(e.TouchDevice);
        EndResize();
        e.Handled = true;
    }

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement element) return;

        element.CaptureMouse();
        BeginResize(PointToScreen(e.GetPosition(this)).Y);
        e.Handled = true;
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing) return;

        UpdateResize(PointToScreen(e.GetPosition(this)).Y);
        e.Handled = true;
    }

    private void Grip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement element) return;

        element.ReleaseMouseCapture();
        EndResize();
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // タッチ
    // ------------------------------------------------------------------
    // Handled = true にしてマウスイベントへの昇格を止める。
    // 昇格させると同じタップで二重に送出される。

    private void Key_TouchDown(object? sender, TouchEventArgs e)
    {
        if (sender is not KeyButton button) return;

        // 指がキーの外へ移動しても TouchUp を受け取れるようにキャプチャする。
        // 取りこぼすとリピートが止まらなくなる。
        button.CaptureTouch(e.TouchDevice);
        PressKey(button);
        e.Handled = true;
    }

    private void Key_TouchUp(object? sender, TouchEventArgs e)
    {
        if (sender is not KeyButton button) return;

        button.ReleaseTouchCapture(e.TouchDevice);
        ReleaseKey(button);
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // マウス（デスクトップでの動作確認用）
    // ------------------------------------------------------------------

    private void Key_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not KeyButton button) return;

        button.CaptureMouse();
        PressKey(button);
        e.Handled = true;
    }

    private void Key_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not KeyButton button) return;

        button.ReleaseMouseCapture();
        ReleaseKey(button);
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // 押下と解放
    // ------------------------------------------------------------------

    private void PressKey(KeyButton button)
    {
        if (!_pressed.Add(button)) return;

        // 送出は押下時に行う。ここで Fn / Shift のラッチが解決される。
        _dispatcher.Press(button.Definition);

        // ラッチ解除で全キーの表示が変わるため、押下表示はその後に適用する。
        RefreshAllKeys();
        button.SetPressed(true, LatchOf(button), LabelOf(button), IconOf(button));

        if (button.Definition.CanRepeat)
        {
            _repeat.Start(() => _dispatcher.Repeat(button.Definition));
        }
    }

    private void ReleaseKey(KeyButton button)
    {
        if (!_pressed.Remove(button)) return;

        _repeat.Stop();
        button.SetPressed(false, LatchOf(button), LabelOf(button), IconOf(button));
    }

    private LatchState LatchOf(KeyButton button) =>
        button.Definition.IsModifier
            ? _dispatcher.Modifiers[button.Definition.Modifier]
            : LatchState.Off;

    private string LabelOf(KeyButton button) => _dispatcher.ResolveLabel(button.Definition);

    private string? IconOf(KeyButton button) => _dispatcher.ResolveIcon(button.Definition);

    /// <summary>
    /// 修飾キーの状態が変わったら全キーを更新する。
    /// Fn ラッチで数字段が F1〜F12 に変わり、Shift ラッチで記号の表示が変わる。
    /// </summary>
    private void RefreshAllKeys()
    {
        foreach (var button in _buttons)
        {
            if (_pressed.Contains(button)) continue;
            button.Refresh(LatchOf(button), LabelOf(button), IconOf(button));
        }
    }

    private void Log(string message, bool isError)
    {
        LogText.Text = message;
        LogText.Foreground = isError ? Brushes.OrangeRed : _theme.SecondaryForeground;
    }

    // ------------------------------------------------------------------
    // 終了
    // ------------------------------------------------------------------

    /// <summary>
    /// ✕ は隠すだけ。終了はタスクトレイから行う（要件 F-6）。
    /// 標準タッチキーボードの ✕ も同じ挙動。
    /// </summary>
    private void Quit_Down(object sender, InputEventArgs e)
    {
        e.Handled = true;
        HideKeyboard();
    }

    /// <summary>
    /// 終了処理。ABM_REMOVE を確実に通す経路の 1 つ。
    /// 呼ばずに終了すると他アプリの作業領域が縮んだまま残る。
    /// </summary>
    public void Teardown()
    {
        _repeat.Dispose();

        // ラッチしたままの修飾キーを残さない（要件 F-1）。
        // 送出は押下時に完結しているため、ここでは状態を落とすだけでよい。
        _dispatcher.Modifiers.Clear();
        _pressed.Clear();

        _settings.Save();

        _dock?.Dispose();
        _dock = null;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        Teardown();
        base.OnClosing(e);
    }
}
