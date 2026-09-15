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

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    /// <summary>
    /// 変換型デバイスの姿勢。0 ならスレート（キーボードを畳んだ・外した状態）、
    /// 0 以外ならラップトップ。Windows 自身がタッチキーボードの自動表示を
    /// 判断するのに使う値と同じもの。
    /// </summary>
    internal const int SM_CONVERTIBLESLATEMODE = 0x2003;

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    /// <summary>OS が二度押しとみなす間隔（ミリ秒）。既定は 500。</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDoubleClickTime();

    /// <summary>
    /// 配列を指定して仮想キーからスキャンコードを引く。
    /// <see cref="MAPVK_VK_TO_VSC_EX"/> を渡すと、上位バイトに拡張の前置き
    /// （0xE0 / 0xE1）が入る。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKeyExW(uint code, uint mapType, nint layout);

    /// <summary>スレッドの現在のキーボード配列。0 で呼び出し元スレッド。</summary>
    [DllImport("user32.dll")]
    internal static extern nint GetKeyboardLayout(uint threadId);

    // ------------------------------------------------------------------
    // 低レベルキーボードフック
    // ------------------------------------------------------------------

    internal const int WH_KEYBOARD_LL = 13;

    /// <summary>SendInput で送り込まれたイベントに立つ。自分が送ったキーを見分けるために使う。</summary>
    internal const uint LLKHF_INJECTED = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    internal const int WH_MOUSE_LL = 14;

    /// <summary>
    /// タッチ・ペン由来のマウスイベントに付く署名。
    /// <c>dwExtraInfo</c> をこのマスクで見て一致すればタッチかペン、しなければ本物のマウス。
    /// </summary>
    internal const nuint MI_WP_SIGNATURE = 0xFF515700;

    internal const nuint MI_WP_SIGNATURE_MASK = 0xFFFFFF00;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public int X;
        public int Y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    // ------------------------------------------------------------------
    // Raw Input（タッチ・ペンの検出）
    // ------------------------------------------------------------------

    internal const int WM_INPUT = 0x00FF;

    /// <summary>前面でなくても入力を受け取る。</summary>
    internal const uint RIDEV_INPUTSINK = 0x00000100;

    internal const ushort HID_USAGE_PAGE_DIGITIZER = 0x0D;
    internal const ushort HID_USAGE_DIGITIZER_PEN = 0x02;
    internal const ushort HID_USAGE_DIGITIZER_TOUCH_SCREEN = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    // ------------------------------------------------------------------
    // Raw Input の中身（タッチの座標）
    //
    // タッチではカーソルが動かないため、押された場所は生の報告から読むしかない。
    // 実測では、指で 3 回タップしてもカーソル位置は 1 度も変わらなかった。
    // ------------------------------------------------------------------

    internal const uint RID_INPUT = 0x10000003;

    /// <summary>報告の種別。ヒューマンインターフェイスデバイス。</summary>
    internal const uint RIM_TYPEHID = 2;

    /// <summary>デバイスの報告の書式（HidP_* に渡す下ごしらえ済みのデータ）。</summary>
    internal const uint RIDI_PREPARSEDDATA = 0x20000005;

    /// <summary>入力の報告。HidP_* の種別。</summary>
    internal const int HIDP_REPORT_TYPE_INPUT = 0;

    internal const uint HIDP_STATUS_SUCCESS = 0x00110000;

    internal const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    internal const ushort HID_USAGE_GENERIC_X = 0x30;
    internal const ushort HID_USAGE_GENERIC_Y = 0x31;

    /// <summary>接触しているか。離した報告と区別する。</summary>
    internal const ushort HID_USAGE_DIGITIZER_TIP_SWITCH = 0x42;

    /// <summary>
    /// ペンを反転させている（先端ではなく反対側の消しゴムを使っている）か。
    /// 一部のペン／ドライバでは、この状態でも接触に見える値が紛れて出ることがある。
    ///
    /// 消しゴム側でもこの使用状況が一度も出ない機種があることを実機で確認したが、
    /// In Range・Confidence の有無による判別は指の本物のタッチとの区別が
    /// つかず断念した（TouchDigitizer.Touching 参照）。
    /// </summary>
    internal const ushort HID_USAGE_DIGITIZER_INVERT = 0x3C;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public nint hDevice;
        public nint wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    /// <summary>
    /// 値の取りうる範囲の定義。
    ///
    /// 末尾は範囲指定の有無で意味が変わる共用体だが、どちらも大きさは同じで、
    /// 先頭は使用法（Usage）にあたる。座標は範囲指定を持たないので、
    /// 範囲でないほうの並びで受ける。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        [MarshalAs(UnmanagedType.U1)] public bool IsRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;
        [MarshalAs(UnmanagedType.U1)] public bool HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public ushort[] Reserved2;

        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;

        public ushort Usage;
        public ushort Reserved3;
        public ushort StringIndex;
        public ushort Reserved4;
        public ushort DesignatorIndex;
        public ushort Reserved5;
        public ushort DataIndex;
        public ushort Reserved6;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(
        nint hRawInput, uint uiCommand, nint pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputDeviceInfoW(
        nint hDevice, uint uiCommand, nint pData, ref uint pcbSize);

    [DllImport("hid.dll")]
    internal static extern uint HidP_GetCaps(nint preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll")]
    internal static extern uint HidP_GetValueCaps(
        int reportType,
        [Out] HIDP_VALUE_CAPS[] valueCaps,
        ref ushort valueCapsLength,
        nint preparsedData);

    [DllImport("hid.dll")]
    internal static extern uint HidP_GetUsageValue(
        int reportType,
        ushort usagePage,
        ushort linkCollection,
        ushort usage,
        out uint usageValue,
        nint preparsedData,
        nint report,
        uint reportLength);

    [DllImport("hid.dll")]
    internal static extern uint HidP_GetUsages(
        int reportType,
        ushort usagePage,
        ushort linkCollection,
        [Out] ushort[] usageList,
        ref uint usageLength,
        nint preparsedData,
        nint report,
        uint reportLength);

    [DllImport("hid.dll")]
    internal static extern uint HidP_MaxUsageListLength(
        int reportType, ushort usagePage, nint preparsedData);

    internal delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    /// <summary>親をたどって根のウィンドウを得る。</summary>
    internal const uint GA_ROOT = 2;

    /// <summary>所有チェーンをたどって根のウィンドウを得る。</summary>
    internal const uint GA_ROOTOWNER = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(POINT point);

    // ------------------------------------------------------------------
    // プロセスの問い合わせ
    // ------------------------------------------------------------------

    /// <summary>実行ファイルのパスを引くのに必要な最小の権限。</summary>
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(
        nint process, uint flags, System.Text.StringBuilder buffer, ref int size);

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint hWnd, uint gaFlags);

    /// <summary>WS_EX_LAYERED。半透明・完全透明の指定を受け付けるようにする。</summary>
    internal const int WS_EX_LAYERED = 0x00080000;

    /// <summary>LWA_ALPHA。不透明度で指定する。</summary>
    internal const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(
        nint hWnd, uint color, byte alpha, uint flags);

    /// <summary>WM_NCLBUTTONDOWN。非クライアント領域の押下。</summary>
    internal const uint WM_NCLBUTTONDOWN = 0x00A1;

    /// <summary>HTCAPTION。タイトルバーとして扱う。</summary>
    internal const nint HTCAPTION = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>
    /// 返事を待つ上限を決めて送る。他プロセスの窓へ問い合わせるときに使う。
    /// 相手が固まっていると SendMessage は戻らず、こちらの入力処理まで止まる。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint SendMessageTimeoutW(
        nint hWnd, uint msg, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);

    /// <summary>応答しない相手なら待たずに諦める。</summary>
    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>スレッドに割り当てられた IME の窓。</summary>
    [DllImport("imm32.dll")]
    internal static extern nint ImmGetDefaultIMEWnd(nint hWnd);

    internal const uint WM_IME_CONTROL = 0x0283;
    internal const nint IMC_GETOPENSTATUS = 0x0005;

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
    // システムパラメータ
    // ------------------------------------------------------------------

    internal const uint SPI_GETKEYBOARDDELAY = 0x0016;
    internal const uint SPI_GETKEYBOARDSPEED = 0x000A;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfoW(
        uint uiAction, uint uiParam, out int pvParam, uint fWinIni);

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

    /// <summary>DWMWA_BORDER_COLOR。ウィンドウの外周に DWM が引く線の色。</summary>
    internal const int DWMWA_BORDER_COLOR = 34;

    /// <summary>DWMWA_COLOR_NONE。線を引かせない。</summary>
    internal const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    /// <summary>DWMWA_COLOR_DEFAULT。線の色を既定に戻す。</summary>
    internal const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

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

    // ------------------------------------------------------------------
    // トグルキーの状態（Caps Lock）
    // ------------------------------------------------------------------
    //
    // GetKeyState は呼び出し元スレッドのメッセージキューに積まれた最後の入力を基準にする
    // ため、他プロセス宛ての入力の値は本来正しくない。ただしトグル系（Caps Lock 等）の
    // 下位ビットはシステム全体で共有される値で、スレッドをまたいでも正しく読める。

    internal const int VK_CAPITAL = 0x14;

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int nVirtKey);
}
