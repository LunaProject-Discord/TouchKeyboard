using System;

namespace TouchKeyboard.Interop;

/// <summary>
/// 拡張ウィンドウスタイルの適用。
///
/// 絶対制約: ウィンドウは WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW を持つこと。
/// キーをタップするたびに入力先のフォーカスが外れる実装は仕様違反。
///
/// WPF では SourceInitialized のタイミングで HwndSource からハンドルを取得して適用する。
/// コンストラクタではまだハンドルが存在しない。
/// </summary>
public static class WindowStyles
{
    /// <summary>
    /// タップしてもフォーカスを奪わないウィンドウにする。
    /// WS_EX_NOACTIVATE でアクティブ化を防ぎ、WS_EX_TOOLWINDOW で Alt+Tab から外す。
    /// </summary>
    public static void ApplyNoActivate(nint hwnd)
    {
        if (hwnd == 0) throw new ArgumentException("ウィンドウハンドルが無効です。", nameof(hwnd));

        var current = GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        var updated = current
                      | NativeMethods.WS_EX_NOACTIVATE
                      | NativeMethods.WS_EX_TOOLWINDOW;

        SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, updated);
    }

    /// <summary>現在の拡張スタイルに指定のビットが立っているかを返す。動作確認用。</summary>
    public static bool HasExStyle(nint hwnd, int exStyle)
        => (GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE) & exStyle) == exStyle;

    /// <summary>WS_DLGFRAME。DWM はこれを見てウィンドウの外周に枠を描く。</summary>
    public const int WS_DLGFRAME = 0x00400000;

    /// <summary>WS_EX_WINDOWEDGE。</summary>
    public const int WS_EX_WINDOWEDGE = 0x00000100;

    /// <summary>
    /// 外周の枠を消す。
    ///
    /// <c>OverlappedPresenter.SetBorderAndTitleBar(hasBorder: false, ...)</c> を指定しても
    /// <see cref="WS_DLGFRAME"/> は残る。DWM はこれを見て 1px の枠を描くため、
    /// 画面端に接して置くと右下に線として現れる。
    /// <c>DWMWA_BORDER_COLOR</c> では消えない。スタイル自体を落とす必要がある。
    /// </summary>
    public static void RemoveFrame(nint hwnd)
    {
        if (hwnd == 0) return;

        var style = GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE);
        var updated = style & ~(nint)WS_DLGFRAME;
        if (updated != style) SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, updated);

        var ex = GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        var exUpdated = ex & ~(nint)WS_EX_WINDOWEDGE;
        if (exUpdated != ex) SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, exUpdated);

        if (updated == style && exUpdated == ex) return;

        // スタイルの変更は再計算を要求しないと反映されない。
        NativeMethods.SetWindowPos(
            hwnd, 0, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE
            | NativeMethods.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// ウィンドウ自体を見えなくする。中身は描かれるが画面には出ない。
    ///
    /// フライアウトの置き場所にだけ使うウィンドウのように、
    /// 器そのものを見せたくない場合に使う。器を小さくしても、
    /// システムが下限まで広げるため四角が残る。
    ///
    /// 別ウィンドウとして開くポップアップはこの指定の影響を受けない。
    /// </summary>
    public static void MakeInvisible(nint hwnd)
    {
        if (hwnd == 0) return;

        var ex = GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_LAYERED);

        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 0, NativeMethods.LWA_ALPHA);
    }

    /// <summary>現在の前面ウィンドウ。</summary>
    public static nint ForegroundWindow() => NativeMethods.GetForegroundWindow();

    /// <summary>
    /// 所有チェーンをたどった根のウィンドウ。
    ///
    /// サジェストや変換候補は、呼び出し元のウィンドウに所有された別ウィンドウとして出る。
    /// 所有関係は Win32 が「補助 UI」を表すために持つ仕組みで、これをたどれば
    /// 「同じウィンドウの付属物か」「別のアプリへ移ったか」を区別できる。
    /// </summary>
    public static nint RootOwner(nint hwnd)
    {
        if (hwnd == 0) return 0;

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOTOWNER);
        return root == 0 ? hwnd : root;
    }

    /// <summary>
    /// 64bit では SetWindowLongPtr を使う。SetWindowLong のままだと値が切り詰められる環境がある。
    /// 32bit の user32 は SetWindowLongPtrW をエクスポートしないためフォールバックする。
    /// </summary>
    private static nint SetWindowLongPtr(nint hwnd, int index, nint value)
        => IntPtr.Size == 8
            ? NativeMethods.SetWindowLongPtrW(hwnd, index, value)
            : NativeMethods.SetWindowLongW(hwnd, index, (int)value);

    private static nint GetWindowLongPtr(nint hwnd, int index)
        => IntPtr.Size == 8
            ? NativeMethods.GetWindowLongPtrW(hwnd, index)
            : NativeMethods.GetWindowLongW(hwnd, index);
}
