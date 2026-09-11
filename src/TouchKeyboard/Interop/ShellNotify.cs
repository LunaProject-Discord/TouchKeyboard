using System;
using System.Runtime.InteropServices;

namespace TouchKeyboard.Interop;

/// <summary>
/// 通知領域アイコンとポップアップメニューの P/Invoke 宣言。
///
/// WinUI 3 には標準の NotifyIcon が無いため、Shell_NotifyIcon と
/// Win32 のポップアップメニューを直接使う。
/// </summary>
internal static class ShellNotify
{
    // ------------------------------------------------------------------
    // 通知領域アイコン
    // ------------------------------------------------------------------

    internal const uint NIM_ADD = 0x00000000;
    internal const uint NIM_MODIFY = 0x00000001;
    internal const uint NIM_DELETE = 0x00000002;
    internal const uint NIM_SETVERSION = 0x00000004;

    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;
    internal const uint NIF_INFO = 0x00000010;

    /// <summary>
    /// NIF_SHOWTIP。標準の吹き出しを出す。
    ///
    /// NOTIFYICON_VERSION_4 にすると既定では抑制される。
    /// 自前で描く前提の版なので、標準のものが要るなら明示する。
    /// </summary>
    internal const uint NIF_SHOWTIP = 0x00000080;

    internal const uint NOTIFYICON_VERSION_4 = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        /// <summary>uTimeout と uVersion のユニオン。NIM_SETVERSION ではバージョンとして使う。</summary>
        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    /// <summary>
    /// 通知領域に置いたアイコンの画面上の位置。
    ///
    /// メニューを出す位置に使う。押した座標を使うとカーソルの位置に出てしまい、
    /// タッチやキーボードから開いた場合は見当違いの場所になる。
    /// </summary>
    [DllImport("shell32.dll", SetLastError = false)]
    internal static extern int Shell_NotifyIconGetRect(
        ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // ------------------------------------------------------------------
    // メニュー
    //
    // Win32 のポップアップメニューは使わない。既定のテーマで描かれ、
    // 明暗の切り替えにも素材にも追従しないため、WinUI で組み直した。
    // ------------------------------------------------------------------

    /// <summary>
    /// メニューを出す前に呼ぶ。これを省くと、メニュー外をタップしても閉じない。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    // ------------------------------------------------------------------
    // メッセージ専用ウィンドウ
    // ------------------------------------------------------------------

    internal const int WM_DESTROY = 0x0002;
    internal const int WM_COMMAND = 0x0111;
    internal const int WM_RBUTTONUP = 0x0205;
    internal const int WM_LBUTTONUP = 0x0202;

    /// <summary>HWND_MESSAGE。メッセージ受信専用のウィンドウを作るときの親。</summary>
    internal static readonly nint HWND_MESSAGE = -3;

    internal delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSW
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandleW(string? lpModuleName);

    // ------------------------------------------------------------------
    // アイコン生成
    // ------------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint CreateIconIndirect(ref ICONINFO iconInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint hIcon);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateBitmap(
        int nWidth, int nHeight, uint nPlanes, uint nBitCount, nint lpBits);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint hObject);

    // ------------------------------------------------------------------
    // 字面をアイコンに焼くための GDI
    //
    // GDI の文字描画は 32bit の DIB に対しても α を書かない。
    // 黒地に白で描いてから、明るさをそのまま α として埋め直す。
    // ------------------------------------------------------------------

    internal const int TRANSPARENT = 1;
    internal const uint DIB_RGB_COLORS = 0;
    internal const int DEFAULT_CHARSET = 1;
    internal const uint TA_CENTER = 6;

    /// <summary>
    /// ANTIALIASED_QUALITY。グレースケールで縁を滑らかにする。
    ///
    /// 既定のままだと ClearType になり、縁に色が付く。
    /// この色を明るさとして読むと α が偏り、にじんで見える。
    /// </summary>
    internal const uint ANTIALIASED_QUALITY = 4;

    /// <summary>SM_CXSMICON / SM_CYSMICON。通知領域が使う小さいアイコンの大きさ。</summary>
    internal const int SM_CXSMICON = 49;
    internal const int SM_CYSMICON = 50;

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint SelectObject(nint hdc, nint hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateDIBSection(
        nint hdc, ref BITMAPINFO bmi, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet,
        uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily,
        string faceName);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int SetBkMode(nint hdc, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern uint SetTextColor(nint hdc, uint color);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern uint SetTextAlign(nint hdc, uint align);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TextOutW(nint hdc, int x, int y, string text, int length);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTextExtentPoint32W(
        nint hdc, string text, int length, out SIZE size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }
}
