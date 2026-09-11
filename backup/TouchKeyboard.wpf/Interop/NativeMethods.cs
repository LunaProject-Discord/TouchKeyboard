using System;
using System.Runtime.InteropServices;

namespace TouchKeyboard.Interop;

/// <summary>
/// P/Invoke 宣言のみを持つ。ロジックを書かない。
/// このファイルより上のレイヤに DllImport を置かないこと（CLAUDE.md 実装方針）。
/// </summary>
internal static class NativeMethods
{
    // ------------------------------------------------------------------
    // SendInput
    // ------------------------------------------------------------------

    internal const uint INPUT_MOUSE = 0;
    internal const uint INPUT_KEYBOARD = 1;
    internal const uint INPUT_HARDWARE = 2;

    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;
    internal const uint KEYEVENTF_SCANCODE = 0x0008;

    /// <summary>
    /// ULONG_PTR を含むため 32bit / 64bit でサイズが異なる。
    /// x64: 2 + 2 + 4 + 4 + (pad 4) + 8 = 24 バイト。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    /// <summary>
    /// INPUT のユニオンサイズを決める最大メンバ。
    /// x64: 4 * 5 + (pad 4) + 8 = 32 バイト。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    /// <summary>
    /// MOUSEINPUT / KEYBDINPUT / HARDWAREINPUT のユニオン。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    /// <summary>
    /// サイズは最大メンバである MOUSEINPUT に合わせて決まる。
    /// x64: type(4) + パディング(4) + ユニオン(32) = 40 バイト。
    /// cbSize には必ず Marshal.SizeOf&lt;INPUT&gt;() を渡し、定数を直書きしないこと。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern ushort MapVirtualKeyW(uint uCode, uint uMapType);

    internal const uint MAPVK_VK_TO_VSC = 0;
    internal const uint MAPVK_VSC_TO_VK = 1;
    internal const uint MAPVK_VK_TO_VSC_EX = 4;

    // ------------------------------------------------------------------
    // ウィンドウスタイル
    // ------------------------------------------------------------------

    internal const int GWL_EXSTYLE = -20;
    internal const int GWL_STYLE = -16;

    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>64bit 用。32bit の user32 はこれをエクスポートしない。</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtrW(nint hWnd, int nIndex);

    /// <summary>32bit 用フォールバック。</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    internal static extern int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    internal static extern int GetWindowLongW(nint hWnd, int nIndex);

    // ------------------------------------------------------------------
    // ウィンドウ位置
    // ------------------------------------------------------------------

    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_FRAMECHANGED = 0x0020;

    internal static readonly nint HWND_TOPMOST = -1;
    internal static readonly nint HWND_NOTOPMOST = -2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;

        public int Width => right - left;
        public int Height => bottom - top;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // ------------------------------------------------------------------
    // AppBar
    // ------------------------------------------------------------------

    internal const uint ABM_NEW = 0x00000000;
    internal const uint ABM_REMOVE = 0x00000001;
    internal const uint ABM_QUERYPOS = 0x00000002;
    internal const uint ABM_SETPOS = 0x00000003;
    internal const uint ABM_GETSTATE = 0x00000004;
    internal const uint ABM_GETTASKBARPOS = 0x00000005;
    internal const uint ABM_WINDOWPOSCHANGED = 0x00000009;

    internal const uint ABE_LEFT = 0;
    internal const uint ABE_TOP = 1;
    internal const uint ABE_RIGHT = 2;
    internal const uint ABE_BOTTOM = 3;

    // AppBar 通知（uCallbackMessage の wParam）
    internal const int ABN_STATECHANGE = 0x0000;
    internal const int ABN_POSCHANGED = 0x0001;
    internal const int ABN_FULLSCREENAPP = 0x0002;
    internal const int ABN_WINDOWARRANGE = 0x0003;

    /// <summary>
    /// x64: cbSize(4) + パディング(4) + hWnd(8) + uCallbackMessage(4) + uEdge(4)
    ///      + rc(16) + lParam(8) = 48 バイト。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessageW(string lpString);

    /// <summary>全トップレベルウィンドウ宛。二重起動時に既存インスタンスへ通知するのに使う。</summary>
    internal static readonly nint HWND_BROADCAST = 0xFFFF;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    // ------------------------------------------------------------------
    // モニタ
    // ------------------------------------------------------------------

    internal const uint MONITOR_DEFAULTTONULL = 0;
    internal const uint MONITOR_DEFAULTTOPRIMARY = 1;
    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    internal const uint MONITORINFOF_PRIMARY = 1;

    /// <summary>
    /// szDevice は CCHDEVICENAME(32) 文字固定。
    /// サイズは 4 + 16 + 16 + 4 + 64 = 104 バイト。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW lpmi);

    internal delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint lprcClip, nint dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    /// <summary>MDT_EFFECTIVE_DPI。スケーリング設定を反映した DPI。</summary>
    internal const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>
    /// モニタごとの DPI を得る。AppBar の矩形は物理ピクセルなので、
    /// DIP で保持している高さを物理ピクセルへ変換するのに使う。
    /// </summary>
    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(
        nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ------------------------------------------------------------------
    // DWM（Mica / Acrylic の背景）
    // ------------------------------------------------------------------

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE。タイトルバーとフレームを暗くする。</summary>
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE。</summary>
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE。Windows 11 22H2 以降。</summary>
    internal const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    /// <summary>
    /// DWMSBT_NONE。バックドロップを描かせない。
    /// 非アクティブなウィンドウでは単色フォールバックが描かれるため、
    /// SetWindowCompositionAttribute による Acrylic と併用すると手前を潰してしまう。
    /// </summary>
    internal const int DWMSBT_NONE = 1;

    /// <summary>DWMSBT_MAINWINDOW = Mica。</summary>
    internal const int DWMSBT_MAINWINDOW = 2;

    /// <summary>DWMSBT_TRANSIENTWINDOW = Acrylic。ポップアップ向けでキーボードに適する。</summary>
    internal const int DWMSBT_TRANSIENTWINDOW = 3;

    /// <summary>DWMWCP_DONOTROUND。角を丸めない。画面端へドッキングするときに使う。</summary>
    internal const int DWMWCP_DONOTROUND = 1;

    /// <summary>DWMWCP_ROUND。</summary>
    internal const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(
        nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    /// <summary>
    /// フレームをクライアント領域へ広げる。全メンバに -1 を渡すと領域全体がガラスになり、
    /// DWM が描くバックドロップがクライアント領域に現れる。
    /// </summary>
    [DllImport("dwmapi.dll")]
    internal static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS pMarInset);

    // ------------------------------------------------------------------
    // コンポジション（Acrylic）
    // ------------------------------------------------------------------
    //
    // DWMWA_SYSTEMBACKDROP_TYPE によるバックドロップは、ウィンドウが非アクティブのとき
    // 単色のフォールバック色で描画される。本アプリは WS_EX_NOACTIVATE により
    // 構造上アクティブになれないため、あちらの経路では常に単色になる。
    //
    // SetWindowCompositionAttribute はアクティブ状態に依存しない。
    // 非公開 API だが Windows 10 の頃から広く使われており、
    // 標準タッチキーボード（WinUI のコンポジション API）と同等の見た目を得る唯一の現実的な手段。

    internal enum AccentState
    {
        Disabled = 0,
        EnableGradient = 1,
        EnableTransparentGradient = 2,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
        EnableHostBackdrop = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ACCENT_POLICY
    {
        public AccentState AccentState;
        public int AccentFlags;

        /// <summary>色は AABBGGRR。RGBA ではない点に注意。</summary>
        public uint GradientColor;

        public int AnimationId;
    }

    /// <summary>WCA_ACCENT_POLICY。</summary>
    internal const int WCA_ACCENT_POLICY = 19;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attrib;
        public nint pvData;
        public int cbData;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowCompositionAttribute(
        nint hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);
}
