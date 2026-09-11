using System;
using System.Runtime.InteropServices;

namespace TouchKeyboard.Interop;

/// <summary>ドッキング先のエッジ。</summary>
public enum AppBarEdge : uint
{
    Left = NativeMethods.ABE_LEFT,
    Top = NativeMethods.ABE_TOP,
    Right = NativeMethods.ABE_RIGHT,
    Bottom = NativeMethods.ABE_BOTTOM,
}

/// <summary>物理ピクセルの矩形。WPF の DIP とは異なる座標系である点に注意。</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// SHAppBarMessage ラッパー。
///
/// 絶対制約: 画面領域の確保は SHAppBarMessage で行う。Topmost で覆うだけでは
/// 入力欄がキーボードに隠れる。
///
/// 手順は ABM_NEW → ABM_QUERYPOS → ABM_SETPOS → SetWindowPos。
/// 高さ変更のたびにこの経路を通す。自前で座標を決めて SetWindowPos するだけでは
/// 他アプリの作業領域が更新されない。
///
/// 注意: 他プロセスが登録した AppBar を外部から解除する API は存在しない。
/// 異常終了で残骸が残った場合、起動時に掃除することはできないため、
/// ABM_REMOVE を確実に呼ぶ経路を呼び出し側で三重化すること
/// （Window.Closing / ProcessExit / SessionEnding）。
/// </summary>
public sealed class AppBar : IDisposable
{
    private readonly nint _hwnd;
    private bool _registered;
    private bool _disposed;

    /// <summary>
    /// AppBar 通知を受け取る独自メッセージ ID。
    /// ウィンドウプロシージャでこの ID のメッセージを監視し、
    /// wParam が ABN_POSCHANGED なら位置を再計算する。
    /// </summary>
    public uint CallbackMessage { get; }

    public bool IsRegistered => _registered;

    public AppBar(nint hwnd)
    {
        if (hwnd == 0) throw new ArgumentException("ウィンドウハンドルが無効です。", nameof(hwnd));

        _hwnd = hwnd;
        CallbackMessage = NativeMethods.RegisterWindowMessageW("TouchKeyboard_AppBarMessage");
        if (CallbackMessage == 0)
        {
            throw new InvalidOperationException("RegisterWindowMessage に失敗しました。");
        }
    }

    /// <summary>ABM_NEW で登録する。既に登録済みなら何もしない。</summary>
    public bool Register()
    {
        if (_registered) return true;

        var data = CreateData();
        data.uCallbackMessage = CallbackMessage;

        var result = NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data);
        _registered = result != 0;
        return _registered;
    }

    /// <summary>
    /// ABM_REMOVE で登録を解除する。
    /// 呼ばずに終了すると他アプリの作業領域が縮んだまま残る。
    /// </summary>
    public void Remove()
    {
        if (!_registered) return;

        var data = CreateData();
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
        _registered = false;
    }

    /// <summary>
    /// 希望する矩形を ABM_QUERYPOS で問い合わせ、システムが調整した矩形を ABM_SETPOS で確定し、
    /// 確定した矩形に SetWindowPos する。
    /// </summary>
    /// <param name="edge">ドッキング先のエッジ。</param>
    /// <param name="desired">希望する矩形（物理ピクセル）。</param>
    /// <param name="moveWindow">
    /// 確定した矩形へウィンドウを移動するか。
    /// 呼び出し側で別の矩形へ配置する場合は false にする。
    /// true にしたうえで直後にもう一度移動すると、途中の位置が見えてちらつく。
    /// </param>
    /// <returns>システムが確定した矩形（物理ピクセル）。</returns>
    public PixelRect Reserve(AppBarEdge edge, PixelRect desired, bool moveWindow = true)
    {
        if (!_registered)
        {
            throw new InvalidOperationException("AppBar が未登録です。先に Register を呼んでください。");
        }

        var data = CreateData();
        data.uEdge = (uint)edge;
        data.rc = new NativeMethods.RECT
        {
            left = desired.Left,
            top = desired.Top,
            right = desired.Right,
            bottom = desired.Bottom,
        };

        // 1. 希望位置を問い合わせる。システムは他の AppBar やタスクバーを考慮して rc を書き換える。
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref data);

        // QUERYPOS はエッジ方向の座標のみを調整する。要求した太さを保つよう反対側を詰め直す。
        var thickness = edge switch
        {
            AppBarEdge.Bottom or AppBarEdge.Top => desired.Bottom - desired.Top,
            _ => desired.Right - desired.Left,
        };

        switch (edge)
        {
            case AppBarEdge.Bottom:
                data.rc.top = data.rc.bottom - thickness;
                break;
            case AppBarEdge.Top:
                data.rc.bottom = data.rc.top + thickness;
                break;
            case AppBarEdge.Left:
                data.rc.right = data.rc.left + thickness;
                break;
            case AppBarEdge.Right:
                data.rc.left = data.rc.right - thickness;
                break;
        }

        // 2. 確定させる。ここで他アプリの作業領域が更新される。
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref data);

        // 3. 返ってきた矩形でウィンドウを配置する。
        if (moveWindow)
        {
            NativeMethods.SetWindowPos(
                _hwnd,
                0,
                data.rc.left,
                data.rc.top,
                data.rc.Width,
                data.rc.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        return new PixelRect(data.rc.left, data.rc.top, data.rc.right, data.rc.bottom);
    }

    /// <summary>
    /// ウィンドウの位置やサイズが変わったことをシステムに通知する。
    /// WM_WINDOWPOSCHANGED を受けたときに呼ぶ。
    /// </summary>
    public void NotifyPosChanged()
    {
        if (!_registered) return;

        var data = CreateData();
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_WINDOWPOSCHANGED, ref data);
    }

    private NativeMethods.APPBARDATA CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
        hWnd = _hwnd,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Remove();
    }
}
