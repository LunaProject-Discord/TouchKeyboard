using System;
using System.Runtime.InteropServices;

namespace TouchKeyboard.Interop;

/// <summary>
/// 物理キーボードが打たれたことを知らせる。
///
/// 低レベルフックには自分が <c>SendInput</c> で送ったキーも流れてくるため、
/// <c>LLKHF_INJECTED</c> が立っていないものだけを物理入力とみなす。
/// これが無いと、自分のキーを押した瞬間に自分が隠れることになる。
/// </summary>
public sealed class PhysicalKeyWatcher : IDisposable
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    /// <summary>フックの寿命の間、デリゲートを生かしておく。回収されると呼び出しで落ちる。</summary>
    private readonly NativeMethods.HookProc _proc;

    private nint _hook;

    /// <summary>物理キーボードのキーが押された。</summary>
    public event EventHandler? Pressed;

    public PhysicalKeyWatcher() => _proc = OnKey;

    public bool IsRunning => _hook != 0;

    /// <summary>
    /// 監視を始める。UI スレッドから呼ぶこと。
    /// フックの通知は、仕掛けたスレッドのメッセージループへ届く。
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, 0, 0);
    }

    public void Stop()
    {
        if (!IsRunning) return;

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    /// <summary>
    /// フックは入力の経路上で呼ばれる。ここが遅いと OS 全体の入力が詰まるため、
    /// 判定だけを行って直ちに次へ渡す。実際の処理は購読側でディスパッチャへ逃がす。
    /// </summary>
    private nint OnKey(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN))
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

            // 自分が送ったキーには INJECTED が立つ。それ以外を物理入力とみなす。
            if ((data.flags & NativeMethods.LLKHF_INJECTED) == 0)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
