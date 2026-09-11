using System;
using System.Runtime.InteropServices;

namespace TouchKeyboard.Interop;

/// <summary>
/// ウィンドウプロシージャを差し替えて、独自メッセージを受け取れるようにする。
///
/// WinUI 3 には WPF の HwndSource.AddHook にあたる仕組みが無いため、
/// SetWindowSubclass でサブクラス化する。
/// AppBar の通知（ABN_POSCHANGED）や DPI 変更を受けるのに要る。
/// </summary>
public sealed class WindowSubclass : IDisposable
{
    private delegate nint SubclassProc(
        nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

    private const nuint SubclassId = 1;

    private readonly nint _hwnd;

    /// <summary>GC に回収されると呼び出しが壊れるため、デリゲートを保持する。</summary>
    private readonly SubclassProc _proc;

    private bool _disposed;

    /// <summary>
    /// メッセージを受け取る。true を返すと既定の処理を行わない。
    /// </summary>
    public event Func<uint, nint, nint, bool>? MessageReceived;

    public WindowSubclass(nint hwnd)
    {
        _hwnd = hwnd;
        _proc = Handle;

        SetWindowSubclass(hwnd, _proc, SubclassId, 0);
    }

    private nint Handle(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
    {
        if (MessageReceived?.Invoke(msg, wParam, lParam) == true) return 0;

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RemoveWindowSubclass(_hwnd, _proc, SubclassId);
    }
}
