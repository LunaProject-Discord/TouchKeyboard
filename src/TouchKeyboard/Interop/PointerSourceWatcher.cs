using System;
using System.Runtime.InteropServices;

namespace TouchKeyboard.Interop;

/// <summary>
/// 直近のポインタ操作がマウスだったかを覚えておく。
///
/// マウスで入力欄をクリックしたときにキーボードを出さないための判断材料。
/// タッチとペンは <c>dwExtraInfo</c> に署名が付くので、付いていないものを本物のマウスとみなす。
/// </summary>
public sealed class PointerSourceWatcher : IDisposable
{
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    /// <summary>フックの寿命の間、デリゲートを生かしておく。</summary>
    private readonly NativeMethods.HookProc _proc;

    private nint _hook;

    /// <summary>
    /// タッチからマウスへ持ち替えた。押された位置（画面座標）を渡す。
    ///
    /// 同じ入力欄を触り直した場合はフォーカスが変化せず、フォーカス側からは
    /// 判定を適用する機会が来ない。持ち替えそのものを合図にする。
    /// </summary>
    public event EventHandler<(int X, int Y)>? SwitchedToMouse;

    public PointerSourceWatcher() => _proc = OnMouse;

    public bool IsRunning => _hook != 0;

    public void Start()
    {
        if (IsRunning) return;

        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _proc, 0, 0);
    }

    public void Stop()
    {
        if (!IsRunning) return;

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    /// <summary>
    /// 最後の操作がマウスだったか。まだ何も操作されていなければ false。
    ///
    /// 時間の窓では判断しない。実測では、クリックから 600ms 遅れて 2 度目の
    /// フォーカス移動を出すアプリがあり、窓を過ぎて通過していた。
    /// いま手にしているのがマウスかタッチかは、次に触るまで変わらない。
    /// </summary>
    public bool LastClickWasMouse { get; private set; }

    /// <summary>
    /// 最後にポインタが操作された時刻（<see cref="Environment.TickCount64"/>）。
    /// 表示直後のフォーカス移動が、自分が起こしたものか利用者の操作かを見分けるのに使う。
    /// </summary>
    public long LastInputAt { get; private set; }

    /// <summary>
    /// タッチ・ペンの入力を受け取れるようにする。
    ///
    /// 低レベルマウスフックにはタッチが届かない。実測では、タッチでタップしても
    /// フックが 1 度も呼ばれなかった。最近のアプリはポインタ入力を直接扱うため、
    /// マウスへの変換が起きない。Raw Input なら変換の有無によらず受け取れる。
    ///
    /// 登録するのはタッチとペンだけなので、届いた時点で種別は分かる。
    /// 中身を読む必要はない。
    /// </summary>
    public bool RegisterTouch(nint hwnd)
    {
        var devices = new[]
        {
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = NativeMethods.HID_USAGE_PAGE_DIGITIZER,
                usUsage = NativeMethods.HID_USAGE_DIGITIZER_TOUCH_SCREEN,
                dwFlags = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget = hwnd,
            },
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = NativeMethods.HID_USAGE_PAGE_DIGITIZER,
                usUsage = NativeMethods.HID_USAGE_DIGITIZER_PEN,
                dwFlags = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget = hwnd,
            },
        };

        return NativeMethods.RegisterRawInputDevices(
            devices, (uint)devices.Length, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());
    }

    /// <summary>
    /// タッチ・ペンの入力が届いたときに呼ぶ。WM_INPUT のハンドラから。
    /// </summary>
    /// <returns>
    /// マウスから持ち替えた瞬間なら true。タッチ中は高頻度で呼ばれるため、
    /// 呼び出し側が重い処理をするのは、この切り替わりの 1 回だけにできる。
    /// </returns>
    public bool NotifyTouch()
    {
        LastInputAt = Environment.TickCount64;

        if (!LastClickWasMouse) return false;

        LastClickWasMouse = false;
        return true;
    }

    /// <summary>
    /// フックは入力の経路上で呼ばれる。移動が大量に流れてくるため、
    /// まず種別で振り分け、押下のときだけ構造体を読む。
    /// </summary>
    private nint OnMouse(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && IsButtonDown((int)wParam))
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

            var fromPenOrTouch =
                (data.dwExtraInfo & NativeMethods.MI_WP_SIGNATURE_MASK) == NativeMethods.MI_WP_SIGNATURE;

            LastInputAt = Environment.TickCount64;

            var switched = !fromPenOrTouch && !LastClickWasMouse;
            LastClickWasMouse = !fromPenOrTouch;

            if (switched) SwitchedToMouse?.Invoke(this, (data.X, data.Y));
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsButtonDown(int message) =>
        message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN;

    public void Dispose() => Stop();
}
