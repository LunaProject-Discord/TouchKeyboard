using System;
using TouchKeyboard.Interop;
using TouchKeyboard.Settings;

namespace TouchKeyboard.Views;

/// <summary>
/// 画面下部へのドッキングを受け持つ。
///
/// AppBar の矩形は物理ピクセル、WPF は DIP。高さは DIP で受け取り、
/// ドッキング先モニタの DPI で物理ピクセルへ変換する。
/// WPF の自動スケーリングに任せると、AppBar が確保した物理領域とずれる。
/// </summary>
public sealed class DockManager : IDisposable
{
    private readonly nint _hwnd;
    private readonly AppBar _appBar;
    private bool _disposed;

    /// <summary>AppBar 通知を受け取る独自メッセージ ID。</summary>
    public uint CallbackMessage => _appBar.CallbackMessage;

    public bool IsDocked => _appBar.IsRegistered;

    /// <summary>現在ドッキングしているモニタ。未ドッキングなら null。</summary>
    public MonitorInfo? CurrentMonitor { get; private set; }

    /// <summary>
    /// 画面下端から、システムが確定した領域の下端までの距離（物理ピクセル）。
    /// タスクバーなど他の AppBar が占めている量にあたる。
    ///
    /// ABM_QUERYPOS はこの分だけ上端を押し上げるため、
    /// ドラッグ中の暫定表示でも同じだけ差し引かないと確定時に高さが跳ねる。
    /// </summary>
    private int _bottomOffset;

    /// <summary>
    /// 現在のウィンドウ上端（物理ピクセル）。
    /// ポインタ座標はウィンドウ内の相対値なので、画面座標へ直すのに使う。
    /// </summary>
    public int CurrentTop { get; private set; }

    public DockManager(nint hwnd)
    {
        _hwnd = hwnd;
        _appBar = new AppBar(hwnd);
    }

    /// <summary>
    /// 画面下部にドッキングし、他アプリの作業領域を確保する。
    /// 既にドッキング済みなら位置と高さを取り直す。
    /// </summary>
    /// <param name="heightDip">確保する高さ（DIP）。</param>
    /// <param name="preferredMonitor">ドッキング先モニタのデバイス名。null なら自動。</param>
    public void Dock(double heightDip, string? preferredMonitor, bool coverTaskbar)
    {
        var monitor = MonitorInfo.Resolve(preferredMonitor, _hwnd);
        if (monitor is null) return;

        CurrentMonitor = monitor;

        if (!_appBar.IsRegistered && !_appBar.Register()) return;

        // AppBar の確保はタスクバーの上で行われる。ここは変えない。
        // ウィンドウの移動はここではさせず、最終的な矩形で 1 回だけ動かす。
        // 2 回動かすと、途中でタスクバーの上に置かれた状態が見えてちらつく。
        var reserved = _appBar.Reserve(
            AppBarEdge.Bottom, DesiredRect(monitor, heightDip), moveWindow: false);

        // ドラッグ中の暫定表示を確定後と一致させるために控えておく。
        _bottomOffset = monitor.Bounds.Bottom - reserved.Bottom;

        // 画面端に接しているので角を丸めない。丸めると角の外側に下の画面が覗く。
        Theme.SetRoundedCorners(_hwnd, rounded: false);

        PositionWindow(reserved, monitor, coverTaskbar);
    }

    /// <summary>
    /// 確定した領域にウィンドウを配置する。
    ///
    /// タスクバーを覆う場合は、確保した領域の上端はそのままに、
    /// ウィンドウだけ画面下端まで伸ばす。作業領域は AppBar が確保した範囲のままなので、
    /// 他アプリの配置には影響しない。
    ///
    /// タスクバーも最前面ウィンドウなので、Z 順を明示的に上げ直す必要がある。
    /// </summary>
    private void PositionWindow(PixelRect reserved, MonitorInfo monitor, bool coverTaskbar)
    {
        var bottom = coverTaskbar ? monitor.Bounds.Bottom : reserved.Bottom;
        var height = bottom - reserved.Top;
        if (height <= 0) return;

        CurrentTop = reserved.Top;

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            reserved.Left,
            reserved.Top,
            reserved.Width,
            height,
            NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 位置と大きさは変えずに、最前面へ取り直す。
    ///
    /// タスクバーやスタートメニューはシェルが Z 順を管理しており、開かれた時点で
    /// 上に来る。すでに表示中のキーボードは作業領域を確保し直す必要がないため、
    /// AppBar には触れずに Z 順だけを取り直す。
    /// </summary>
    public void RaiseToTop() =>
        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    /// <summary>
    /// ドッキングを解除し、確保していた作業領域を返す。
    /// 自動表示で隠すたびに呼ばれるため、呼び出し側でデバウンスすること。
    /// </summary>
    public void Undock()
    {
        _appBar.Remove();
        CurrentMonitor = null;

        // 画面端から離れるので角丸を戻す。
        Theme.SetRoundedCorners(_hwnd, rounded: true);
    }

    /// <summary>
    /// ABN_POSCHANGED や WM_DPICHANGED を受けたときに位置を取り直す。
    /// </summary>
    public void Refresh(double heightDip, string? preferredMonitor, bool coverTaskbar)
    {
        if (!_appBar.IsRegistered) return;
        Dock(heightDip, preferredMonitor, coverTaskbar);
    }

    /// <summary>
    /// ドラッグ中の暫定表示。AppBar を経由せずウィンドウだけを動かす。
    ///
    /// ドラッグのたびに ABM_QUERYPOS / ABM_SETPOS を通すと、他アプリの再レイアウトが
    /// 連発してちらつく。確定は指を離したときに <see cref="Dock"/> で行う。
    /// </summary>
    public void PreviewHeight(double heightDip, bool coverTaskbar)
    {
        var monitor = CurrentMonitor;
        if (monitor is null) return;

        // 確定時と同じ矩形になるよう、他の AppBar が占める分を差し引いて上端を決める。
        // これを省くとタスクバーの高さぶん、指を離した瞬間に高さが跳ねる。
        var reservedBottom = monitor.Bounds.Bottom - _bottomOffset;
        var top = reservedBottom - HeightToPixels(monitor, heightDip);
        var bottom = coverTaskbar ? monitor.Bounds.Bottom : reservedBottom;

        CurrentTop = top;

        NativeMethods.SetWindowPos(
            _hwnd,
            0,
            monitor.Bounds.Left,
            top,
            monitor.Bounds.Width,
            bottom - top,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// DIP の高さを、モニタの DPI を考慮した物理ピクセルにする。
    /// 上限は画面高さの半分。
    /// </summary>
    private static int HeightToPixels(MonitorInfo monitor, double heightDip)
    {
        var clamped = AppSettings.ClampHeight(heightDip, monitor.HeightDip);
        return (int)Math.Round(clamped * monitor.DpiScale);
    }

    /// <summary>DIP の高さを、モニタの DPI を考慮した物理ピクセルの矩形にする。</summary>
    private static PixelRect DesiredRect(MonitorInfo monitor, double heightDip)
    {
        var heightPx = HeightToPixels(monitor, heightDip);

        // モニタ全体の下端に、幅いっぱいで置く。
        // 作業領域ではなくモニタ全体を使うのは、AppBar が自分の領域を差し引く前の
        // 座標を期待するため。実際の位置はシステムが ABM_QUERYPOS で調整する。
        var bounds = monitor.Bounds;

        return new PixelRect(
            bounds.Left,
            bounds.Bottom - heightPx,
            bounds.Right,
            bounds.Bottom);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 呼ばずに終了すると他アプリの作業領域が縮んだまま残る。
        _appBar.Dispose();
    }
}
