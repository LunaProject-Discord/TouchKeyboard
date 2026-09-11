using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TouchKeyboard.Interop;

/// <summary>
/// モニタ 1 台の情報。座標はすべて物理ピクセル。
/// </summary>
/// <param name="DeviceName">`\\.\DISPLAY1` 形式。設定に保存してドッキング先を復元するのに使う。</param>
/// <param name="Bounds">モニタ全体。</param>
/// <param name="WorkArea">タスクバーなどを除いた作業領域。</param>
/// <param name="IsPrimary">プライマリモニタか。</param>
/// <param name="DpiScale">96 DPI を 1.0 としたスケール。</param>
public sealed record MonitorInfo(
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    bool IsPrimary,
    double DpiScale)
{
    /// <summary>モニタの高さを DIP で表したもの。高さの上限計算に使う。</summary>
    public double HeightDip => DpiScale > 0 ? Bounds.Height / DpiScale : Bounds.Height;

    /// <summary>設定画面やトレイメニューに出す表示名。</summary>
    public string DisplayName
    {
        get
        {
            // `\\.\DISPLAY1` の末尾だけを使う。
            var index = DeviceName.LastIndexOf('\\');
            var shortName = index >= 0 ? DeviceName[(index + 1)..] : DeviceName;
            var primary = IsPrimary ? "（メイン）" : string.Empty;

            return $"{shortName} {Bounds.Width}×{Bounds.Height} {DpiScale * 100:0}%{primary}";
        }
    }

    /// <summary>接続されている全モニタを列挙する。</summary>
    public static IReadOnlyList<MonitorInfo> All()
    {
        var result = new List<MonitorInfo>();

        // デリゲートがコールバック中に回収されないよう、ローカルに保持したまま渡す。
        bool Callback(nint hMonitor, nint hdc, nint lprcClip, nint dwData)
        {
            var info = FromHandle(hMonitor);
            if (info is not null) result.Add(info);
            return true;
        }

        NativeMethods.EnumDisplayMonitors(0, 0, Callback, 0);
        return result;
    }

    /// <summary>指定ウィンドウが載っているモニタ。</summary>
    public static MonitorInfo? FromWindow(nint hwnd)
    {
        var handle = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return handle == 0 ? null : FromHandle(handle);
    }

    /// <summary>デバイス名で探す。モニタ構成が変わって見つからない場合は null。</summary>
    public static MonitorInfo? ByDeviceName(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return null;

        foreach (var monitor in All())
        {
            if (string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return monitor;
            }
        }

        return null;
    }

    public static MonitorInfo? Primary()
    {
        foreach (var monitor in All())
        {
            if (monitor.IsPrimary) return monitor;
        }

        return null;
    }

    /// <summary>
    /// 設定に保存されたモニタを優先し、見つからなければウィンドウの載っているモニタ、
    /// それも取れなければプライマリを返す。モニタ構成変更からの復帰に使う。
    /// </summary>
    public static MonitorInfo? Resolve(string? preferredDeviceName, nint hwnd)
        => ByDeviceName(preferredDeviceName) ?? FromWindow(hwnd) ?? Primary();

    private static MonitorInfo? FromHandle(nint hMonitor)
    {
        var info = new NativeMethods.MONITORINFOEXW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEXW>(),
            szDevice = string.Empty,
        };

        if (!NativeMethods.GetMonitorInfoW(hMonitor, ref info)) return null;

        var scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(
                hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
            && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        return new MonitorInfo(
            info.szDevice,
            ToPixelRect(info.rcMonitor),
            ToPixelRect(info.rcWork),
            (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
            scale);
    }

    private static PixelRect ToPixelRect(NativeMethods.RECT rect)
        => new(rect.left, rect.top, rect.right, rect.bottom);
}
