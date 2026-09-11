using System;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TouchKeyboard.Interop;
using TouchKeyboard.Layout;
using TouchKeyboard.Settings;
using Windows.Graphics;

namespace TouchKeyboard.Views;

/// <summary>
/// 画面端から離して浮かせる状態。
///
/// 浮かせている間は作業領域を確保しない。他のアプリは画面いっぱいを使い、
/// キーボードはその上に重なる。位置はグリップを持って動かす。
/// </summary>
public sealed partial class KeyboardWindow
{
    /// <summary>浮かせたときの既定の幅。画面幅に対する割合。</summary>
    private const double FloatingWidthRatio = 0.62;

    /// <summary>
    /// タイトルバーの高さ。XAML の Height と揃えること。
    /// <see cref="TitleBarHeightOption.Tall"/> に合わせて 48。
    /// </summary>
    private const double TitleBarHeightDip = 48;

    public bool IsFloating => _settings.Floating;

    /// <summary>閉じる要求を受け入れてよいか。終了の後片付けで立てる。</summary>
    private bool _closable;

    /// <summary>
    /// 設定画面を開いてほしい。
    ///
    /// このウィンドウは設定画面の生存管理をしない。二重に開くとそれぞれが
    /// 同じ設定を書き戻して食い違うため、まとめて App が持つ。
    /// </summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// 浮かせる、または画面端へ戻す。
    /// </summary>
    private void SetFloating(bool floating)
    {
        _settings.Floating = floating;
        _settings.Save();

        ApplyPlacement();

        Log(floating ? "浮かせました" : "画面端に戻しました", isError: false);
    }

    /// <summary>
    /// いまの状態に応じて置き直す。表示のたびに通る。
    ///
    /// ここで状態を見ないと、浮かせた設定のまま隠して再表示したときに
    /// 画面端へ吸い付いてしまう。
    /// </summary>
    private void ApplyPlacement()
    {
        UsePlacementContext();

        ApplySystemTitleBar();
        SetTitleBarVisible(IsFloating);
        SetGripVisible(!IsFloating);
        SetResizeEdgesVisible(IsFloating);
        ApplyBodyInsets();
        RefreshCommandStates();

        if (IsFloating)
        {
            _dock?.Undock();
            ApplyFloatingBounds();
            _dock?.RaiseToTop();
            return;
        }

        Dock();
    }

    /// <summary>キーの周りの余白。</summary>
    private const double BodyInset = 8;

    /// <summary>
    /// ドッキング中の上の余白。ここがドラッグの帯になるため、指で捉えられる幅が要る。
    /// 浮かせている間はハンドルがタイトルバーへ移るので、この幅は要らない。
    /// </summary>
    private const double DockedTopInset = 20;

    /// <summary>
    /// 本体とパネルの余白を状態に合わせて入れる。
    ///
    /// 浮かせている間は四辺とも同じ。ドッキング中だけ上を広げる。
    /// パネル側も揃えないと、キーの大きさと行の位置が本体とずれる。
    /// </summary>
    private void ApplyBodyInsets()
    {
        var top = IsFloating ? BodyInset : DockedTopInset;

        var insets = new Thickness(BodyInset, top, BodyInset, BodyInset);

        Root.Margin = insets;
        NumpadRoot.Margin = insets;
        ClipboardRoot.Margin = insets;

        // パネルを引き出す帯は上の余白そのもの。キーに重ならないよう合わせる。
        LeftEdge.Height = top;
        RightEdge.Height = top;
    }

    /// <summary>
    /// ドラッグハンドルを出し入れする。
    ///
    /// 浮かせている間は出さない。ハンドルが担う操作はすべてタイトルバーにある。
    /// 高さの変更は縁を掴む方が直接的で、メニューの中身はボタンとして並んでいる。
    /// </summary>
    private void SetGripVisible(bool visible) =>
        ResizeGrip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// タイトルバーの領域を <see cref="AppWindowTitleBar"/> として構成する。
    ///
    /// 高さは <see cref="TitleBarHeightOption.Tall"/>。Windows 11 で標準の 48。
    /// ウィンドウのボタンもその高さに合わせて描かれる。
    ///
    /// <c>Window</c> にも同名の <c>ExtendsContentIntoTitleBar</c> があるが挙動が違う。
    /// こちらの <see cref="AppWindowTitleBar"/> の方を使う。
    ///
    /// ドッキング中は外す。画面端に接しているならタイトルバー自体を出さない。
    /// </summary>
    private void ApplySystemTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        var bar = AppWindow.TitleBar;
        var presenter = AppWindow.Presenter as OverlappedPresenter;

        if (!IsFloating)
        {
            bar.ExtendsContentIntoTitleBar = false;

            if (presenter is not null)
            {
                presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);

                // 画面端では大きさを AppBar が決める。縁を掴ませない。
                presenter.IsResizable = false;
            }

            // 画面端に接している間は角を丸めない。角の外側に下の画面が覗く。
            Theme.SetRoundedCorners(_hwnd, rounded: false);

            // スタイルを書き換える操作なので、この 2 つは必ず後から掛け直す。
            EnsureNoActivate();
            WindowStyles.RemoveFrame(_hwnd);
            Theme.HideBorder(_hwnd);
            return;
        }

        // 記事のアプリと違い、このウィンドウは既定でタイトルバーを持たない。
        // 伸ばす先が無いままでは以降の指定が素通りするので、先に持たせる。
        if (presenter is not null)
        {
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true);

            // 最小化と最大化は意味を持たない。ボタンも出さない。
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;

            // システムのリサイズ枠は使わない。WS_THICKFRAME が付き、上端に線が出る。
            // 大きさの変更は自前の当たり判定で受ける。
            presenter.IsResizable = false;
        }

        // 絶対制約 2。SetBorderAndTitleBar が拡張スタイルを書き換えるため掛け直す。
        EnsureNoActivate();

        bar.ExtendsContentIntoTitleBar = true;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = _theme.IsDark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = bar.ButtonForegroundColor;
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;

        // 枠は消さない。
        //
        // DWMWA_BORDER_COLOR でも WS_DLGFRAME を落としても消えなかった。
        // タイトルバーを持たせている以上、枠は付いてくるものとして扱う。
        // 浮かせている間は画面端に接していないので、角も丸めて標準の見た目に寄せる。
        Theme.SetRoundedCorners(_hwnd, rounded: true);

        // 線の色も既定に戻す。消す指定を残したままだと、直線部分の枠だけが残り、
        // 丸めた角では途切れて見える。
        Theme.ShowBorder(_hwnd);

        if (_dragRectsWired) return;

        _dragRectsWired = true;
        TitleBar.Loaded += (_, _) => UpdateDragRectangles();
        TitleBar.SizeChanged += (_, _) => UpdateDragRectangles();
        UpdateDragRectangles();
    }

    private bool _dragRectsWired;

    /// <summary>
    /// 掴んで動かせる範囲をシステムに伝える。
    ///
    /// 伝えないとタイトルバーの領域が単なる余白になり、掴めない。
    /// ボタンの上は外す。外さないと押せずに移動が始まる。
    /// </summary>
    private void UpdateDragRectangles()
    {
        if (!IsFloating || !AppWindowTitleBar.IsCustomizationSupported()) return;
        if (TitleBar.ActualHeight <= 0) return;

        var bar = AppWindow.TitleBar;
        var scale = RasterizationScale();

        // Inset は物理ピクセル。列の幅は DIP なので直してから入れる。
        LeftPaddingColumn.Width = new GridLength(bar.LeftInset / scale);
        RightPaddingColumn.Width = new GridLength(bar.RightInset / scale);

        // 逆に SetDragRectangles は物理ピクセルで受ける。
        // 掴めるのはボタンの間だけ。ボタンの列を含めると、押しても移動が始まる。
        // 上端は大きさを変える帯に譲る。含めるとシステムが先に受け取る。
        var top = ResizeTop.Height * scale;

        var drag = new RectInt32
        {
            X = (int)Math.Round(
                (LeftPaddingColumn.ActualWidth + LeftButtonColumn.ActualWidth) * scale),
            Y = (int)Math.Round(top),
            Width = (int)Math.Round(DragColumn.ActualWidth * scale),
            Height = (int)Math.Round((TitleBar.ActualHeight * scale) - top),
        };

        if (drag.Width <= 0 || drag.Height <= 0) return;

        bar.SetDragRectangles([drag]);

        // 閉じるボタンが出たなら自前のものは要らない。二重になる。
        HideButton.Visibility = bar.RightInset > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private double RasterizationScale()
    {
        var scale = TitleBar.XamlRoot?.RasterizationScale ?? 0;
        if (scale > 0) return scale;

        scale = MonitorInfo.FromWindow(_hwnd)?.DpiScale ?? 1.0;
        return scale > 0 ? scale : 1.0;
    }

    /// <summary>覚えている位置と幅で置く。無ければ画面の中央下寄りに出す。</summary>
    private void ApplyFloatingBounds()
    {
        var monitor = MonitorInfo.Resolve(_settings.MonitorDeviceName, _hwnd);
        if (monitor is null) return;

        var scale = monitor.DpiScale <= 0 ? 1.0 : monitor.DpiScale;

        var width = (_settings.FloatingWidthDip ?? (MonitorWidthDip(monitor) * FloatingWidthRatio)) * scale;

        // 浮かせているときの高さは別に覚える。初めて浮かせるときだけドッキング中の値を使う。
        // タイトルバーのぶんを足す。足さないとキー段がその高さだけ縮む。
        var body = _settings.FloatingHeightDip ?? _settings.HeightDip;
        var height = (body + TitleBarHeightDip) * scale;

        // 初めて浮かせるときは、指の届きやすい下寄りの中央に置く。
        var x = _settings.FloatingXDip is { } savedX
            ? savedX * scale
            : monitor.Bounds.Left + ((monitor.Bounds.Width - width) / 2);

        var y = _settings.FloatingYDip is { } savedY
            ? savedY * scale
            : monitor.Bounds.Bottom - height - (48 * scale);

        // 画面の中に収める。
        // 保存値が壊れていると画面外や全幅で出てきて、タイトルバーに触れなくなる。
        // 触れなければ戻す手段が無いので、出すたびに引き戻す。
        width = Math.Clamp(width, AppSettings.MinFloatingWidthDip * scale, monitor.Bounds.Width);
        height = Math.Min(height, monitor.Bounds.Height);

        x = Math.Clamp(x, monitor.Bounds.Left, monitor.Bounds.Right - width);
        y = Math.Clamp(y, monitor.Bounds.Top, monitor.Bounds.Bottom - height);

        AppWindow.MoveAndResize(new RectInt32(
            (int)Math.Round(x), (int)Math.Round(y),
            (int)Math.Round(width), (int)Math.Round(height)));
    }

    private static double MonitorWidthDip(MonitorInfo monitor) =>
        monitor.DpiScale > 0 ? monitor.Bounds.Width / monitor.DpiScale : monitor.Bounds.Width;

    // ------------------------------------------------------------------
    // 移動
    // ------------------------------------------------------------------

    /// <summary>
    /// タイトルバーを掴んで動かせるようにする。
    ///
    /// 空いている部分だけが対象。ボタンの上から始まった操作は移動にしない。
    /// </summary>
    private void WireTitleBarDrag()
    {
        // 標準の閉じるボタンは隠すだけにする。終了はタスクトレイから行う。
        // 押して消えたきり戻せない、という状態を作らない。
        //
        // 終了の後片付けに入ったら拒まない。拒み続けると畳めず、そこで止まる。
        AppWindow.Closing += (_, e) =>
        {
            if (_closable) return;

            e.Cancel = true;
            HideKeyboard();
        };

        // 動いた結果はここで拾う。ドラッグをシステムが担うと
        // こちらの Pointer の経路を通らず、置いた場所を覚えられない。
        //
        // ただし操作中は通さない。指を動かすたびに呼ばれ、そのつどモニタを
        // 引き当てて倍率を求めることになる。入力の経路でその処理が挟まると、
        // 追従が引っかかって見える。終わった時点で 1 回だけ覚える。
        AppWindow.Changed += (_, e) =>
        {
            if (!IsFloating || _moving || _edge != ResizeEdge.None) return;
            if (!e.DidPositionChange && !e.DidSizeChange) return;

            RememberFloatingBounds();
        };

        // 以下はシステムがドラッグ領域を引き受けなかった場合の経路。
        // このウィンドウは WS_EX_NOACTIVATE で、標準の移動が働くとは限らない。
        //
        // WM_NCLBUTTONDOWN で OS の移動ループに渡す方法は使えなかった。
        // あれはマウスを追う仕組みで、指には付いてこない。
        TitleBar.PointerPressed += (sender, e) =>
        {
            if (!IsFloating) return;

            ((UIElement)sender).CapturePointer(e.Pointer);
            BeginMove(e.GetCurrentPoint(Shell).Position);
            e.Handled = true;
        };

        TitleBar.PointerMoved += (_, e) =>
        {
            if (!_moving) return;

            UpdateMove(e.GetCurrentPoint(Shell).Position);
            e.Handled = true;
        };

        TitleBar.PointerReleased += (sender, e) =>
        {
            if (!_moving) return;

            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            EndMove();
            e.Handled = true;
        };

        TitleBar.PointerCaptureLost += (_, _) => EndMove();

        TitleClipboard.Click += (_, _) => TogglePanel(PanelSide.Left);
        TitleNumpad.Click += (_, _) => TogglePanel(PanelSide.Right);
        TitleDock.Click += (_, _) => SetFloating(!IsFloating);

        // タイトルバーのボタンは右端にある。右辺を揃えて真下に開く。
        // 中央に開くと、右のボタンほど画面の外へはみ出して折り返される。
        TitleSettings.Click += (_, _) => SettingsMenu().ShowAt(TitleSettings, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
        });
    }

    /// <summary>掴んだ位置。ウィンドウ内での相対位置を保つために覚える。</summary>
    private Windows.Foundation.Point _moveGrab;

    private bool _moving;

    /// <summary>移動しきれなかった端数。次の回に持ち越す。</summary>
    private double _moveLeftoverX;
    private double _moveLeftoverY;

    /// <summary>掴んだ時点の表示倍率。移動の間は変わらないので覚えておく。</summary>
    private double _moveScale = 1.0;

    private void BeginMove(Windows.Foundation.Point pointerInWindow)
    {
        _moving = true;
        _moveGrab = pointerInWindow;
        _moveLeftoverX = 0;
        _moveLeftoverY = 0;

        // 指を動かすたびにモニタを引き当てると、その分だけ追従が遅れる。
        _moveScale = RasterizationScale();
    }

    /// <summary>
    /// 掴んだ点がそのまま指の下に来るように動かす。
    ///
    /// ウィンドウが動くと同じ指の位置でもウィンドウ内の座標が変わる。
    /// 掴んだ位置との差分を足していけば、その分だけ追従する。
    ///
    /// 端数は切り捨てずに持ち越す。捨てると、ゆっくり動かしたときに
    /// 差分が 1px に届かない回が続き、溜まってから跳ねる。これが揺れて見える。
    /// </summary>
    private void UpdateMove(Windows.Foundation.Point pointerInWindow)
    {
        if (!_moving) return;

        var dx = ((pointerInWindow.X - _moveGrab.X) * _moveScale) + _moveLeftoverX;
        var dy = ((pointerInWindow.Y - _moveGrab.Y) * _moveScale) + _moveLeftoverY;

        var stepX = (int)Math.Truncate(dx);
        var stepY = (int)Math.Truncate(dy);

        _moveLeftoverX = dx - stepX;
        _moveLeftoverY = dy - stepY;

        if (stepX == 0 && stepY == 0) return;

        var position = AppWindow.Position;
        AppWindow.Move(new PointInt32(position.X + stepX, position.Y + stepY));
    }

    private void EndMove()
    {
        if (!_moving) return;
        _moving = false;

        RememberFloatingBounds();
    }

    private DispatcherQueueTimer? _rememberTimer;

    /// <summary>
    /// 置いた場所を覚える。次に浮かせたときも同じ位置に出す。
    ///
    /// 書き出しは動きが止まってからまとめて行う。移動中は座標が変わるたびに
    /// 呼ばれるため、そのつど書くと入力の経路でファイル操作が続くことになる。
    /// </summary>
    private void RememberFloatingBounds()
    {
        var scale = MonitorInfo.FromWindow(_hwnd)?.DpiScale ?? 1.0;
        if (scale <= 0) scale = 1.0;

        _settings.FloatingXDip = AppWindow.Position.X / scale;
        _settings.FloatingYDip = AppWindow.Position.Y / scale;
        _settings.FloatingWidthDip = AppWindow.Size.Width / scale;

        // 高さは浮かせているとき専用の値に入れる。ドッキング中の高さは触らない。
        // あちらは作業領域の確保に使う値で、浮かせている間の大きさに引きずられると
        // 画面端に戻したときに他のアプリの領域まで変わる。
        var body = (AppWindow.Size.Height / scale) - TitleBarHeightDip;

        _settings.FloatingHeightDip = Math.Clamp(
            body, AppSettings.MinFloatingBodyDip, AppSettings.AbsoluteMaxHeightDip);

        _rememberTimer ??= CreateRememberTimer();
        _rememberTimer.Stop();
        _rememberTimer.Start();
    }

    private DispatcherQueueTimer CreateRememberTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _settings.Save();
        return timer;
    }
}
