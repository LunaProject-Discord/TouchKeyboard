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
