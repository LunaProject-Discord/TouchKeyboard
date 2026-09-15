using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace TouchKeyboard.Interop;

/// <summary>
/// Raw Input で届いたタッチの報告から、押された画面上の位置を読む。
///
/// タッチではカーソルが動かない。実測では、指で 3 回タップしてもカーソル位置は
/// 1 度も変わらなかった。低レベルのマウスフックにも届かない。
/// 押された場所を知るには、デバイスが出す生の報告を解釈するしかない。
///
/// 報告の中身は HID の記述子で決まるため、位置がどのビットにあるかは
/// デバイスごとに違う。記述子を Windows が解釈したもの（下ごしらえ済みのデータ）を
/// hid.dll に渡し、使用法（X・Y・接触の有無）を指定して取り出す。
/// </summary>
public static class TouchDigitizer
{
    /// <summary>デバイスごとの読み方。報告のたびに引き直すと重いので覚えておく。</summary>
    private sealed class Device
    {
        public nint Preparsed;
        public int MinX, MaxX, MinY, MaxY;
        public bool Usable;
    }

    private static readonly Dictionary<nint, Device> Devices = new();

    /// <summary>
    /// 生の報告 1 件を読んだ結果。
    ///
    /// 「接触したか」と「位置が分かったか」は別々に持つ。ペンはホバー中（画面に
    /// 近いだけで触れていない）も報告を出し続け、その頻度は接触より遥かに高い。
    /// 接触の有無を見ずに「位置が読めなければとりあえず触れたことにする」という
    /// 扱いをすると、かざしただけで反応する。
    /// </summary>
    public readonly record struct TouchReport(bool IsTouching, (int X, int Y)? Point);

    /// <summary>
    /// 報告を読む。読めない報告（このアプリが登録した種類でない、記述子が
    /// 壊れている等）では null。呼び出し側はそのまま無視してよい。
    /// </summary>
    public static TouchReport? Read(nint hRawInput)
    {
        uint size = 0;
        var header = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();

        if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, 0, ref size, header)
            == unchecked((uint)-1) || size == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size);

        try
        {
            var read = NativeMethods.GetRawInputData(
                hRawInput, NativeMethods.RID_INPUT, buffer, ref size, header);

            if (read == unchecked((uint)-1) || read < header) return null;

            var head = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(buffer);
            if (head.dwType != NativeMethods.RIM_TYPEHID) return null;

            var device = DeviceFor(head.hDevice);
            if (device is null) return null;

            // ヘッダーの直後が RAWHID。報告の大きさと本数が並び、その後ろに本体が続く。
            var hid = buffer + (int)header;
            var reportSize = (uint)Marshal.ReadInt32(hid);
            var reportCount = (uint)Marshal.ReadInt32(hid, 4);

            if (reportSize == 0 || reportCount == 0) return null;

            var reports = hid + 8;

            for (var i = 0; i < reportCount; i++)
            {
                var report = reports + (int)(i * reportSize);
                if (!Touching(device, report, reportSize)) continue;

                // 接触は確認できた。位置まで読めるかは記述子の作り次第で別問題。
                var point = device.Usable ? PointIn(device, report, reportSize) : null;
                return new TouchReport(true, point);
            }

            // どの報告も接触していなかった。ホバーや、離した瞬間の報告がここに来る。
            return new TouchReport(false, null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>報告 1 件から位置を読む。呼ぶ前に接触は確認済みであること。</summary>
    private static (int X, int Y)? PointIn(Device device, nint report, uint length)
    {
        if (NativeMethods.HidP_GetUsageValue(
                NativeMethods.HIDP_REPORT_TYPE_INPUT,
                NativeMethods.HID_USAGE_PAGE_GENERIC,
                0,
                NativeMethods.HID_USAGE_GENERIC_X,
                out var x,
                device.Preparsed,
                report,
                length)
            != NativeMethods.HIDP_STATUS_SUCCESS)
        {
            return null;
        }

        if (NativeMethods.HidP_GetUsageValue(
                NativeMethods.HIDP_REPORT_TYPE_INPUT,
                NativeMethods.HID_USAGE_PAGE_GENERIC,
                0,
                NativeMethods.HID_USAGE_GENERIC_Y,
                out var y,
                device.Preparsed,
                report,
                length)
            != NativeMethods.HIDP_STATUS_SUCCESS)
        {
            return null;
        }

        return ToScreen(device, (int)x, (int)y);
    }

    /// <summary>
    /// 指やペン先が触れている報告か。
    ///
    /// 離した瞬間にも報告は来る。そちらで位置を拾うと、離した場所で判断してしまう。
    /// ペンは画面に近いだけ（ホバー）でも報告を出し、その頻度は接触よりずっと高い。
    ///
    /// 接触の有無（Tip Switch）が読めないデバイスでは、触れていないものとして扱う。
    /// 触れているとみなす向きに倒すと、ホバーの多いペンではかざしただけで
    /// 反応し続けることになる。指のタップを取りこぼす方が実害が小さい。
    /// </summary>
    private static bool Touching(Device device, nint report, uint length)
    {
        var max = NativeMethods.HidP_MaxUsageListLength(
            NativeMethods.HIDP_REPORT_TYPE_INPUT,
            NativeMethods.HID_USAGE_PAGE_DIGITIZER,
            device.Preparsed);

        if (max == 0) return false;

        var list = new ushort[max];
        var count = max;

        var status = NativeMethods.HidP_GetUsages(
            NativeMethods.HIDP_REPORT_TYPE_INPUT,
            NativeMethods.HID_USAGE_PAGE_DIGITIZER,
            0,
            list,
            ref count,
            device.Preparsed,
            report,
            length);

        if (status != NativeMethods.HIDP_STATUS_SUCCESS) return false;

        // 反転（消しゴム側）のときは無条件に「触れていない」扱いにする。
        // 一部のペン／ドライバでは、消しゴム側をかざしただけの状態でも
        // Tip Switch に見える値が紛れて出ることがあり、それをタップと誤認して
        // キーボードを表示してしまっていた。消しゴムは指でもタッチでもなく、
        // 「タップしてキーボードを出したい」という操作とは無関係。
        //
        // In Range・Confidence の有無で消しゴムを見分けようとしたことがあるが、
        // 実機では指の本物のタッチも Confidence だけを伴い In Range を伴わない
        // 場合があり、区別できずに指のタップまで巻き込んで潰してしまった
        // （実機で確認済み）。
        //
        // Microsoft のドキュメント（Supporting Usages in Digitizer Report
        // Descriptors）によれば、Confidence はそもそも Touch（指）専用の
        // 使用状況で Pen 側には無く、「本物の意図した接触か」を示すもの。
        // つまり消しゴムの誤検出として見えていた値は、ペンの消しゴムそのもの
        // ではなく、消しゴムを近づける動作中に指や手が触れた別の接触を
        // タッチ側のコレクションが拾ったものだった可能性が高い。この報告
        // だけでは、その接触と本物の指のタップを確実に見分けられないため、
        // Invert による判定に留める。消しゴム側でも Invert が一度も出ない
        // 機種があることは分かっているが、指のタップを取りこぼす方が実害が
        // 小さいという冒頭の方針どおり、より安全な側に倒す。
        var isInverted = false;
        var isTipSwitch = false;

        for (var i = 0; i < count; i++)
        {
            if (list[i] == NativeMethods.HID_USAGE_DIGITIZER_INVERT) isInverted = true;
            if (list[i] == NativeMethods.HID_USAGE_DIGITIZER_TIP_SWITCH) isTipSwitch = true;
        }

        return isTipSwitch && !isInverted;
    }

    /// <summary>
    /// デバイスの目盛りを画面の座標へ移す。
    ///
    /// 内蔵のタッチパネルは自分が載っている画面に対応づけられている。
    /// どの画面かをデバイスから引く手立てが無いため、メインの画面とみなす。
    /// 外付けモニタを繋いでいても、指で触れるのは内蔵側だという前提。
    /// </summary>
    private static (int X, int Y)? ToScreen(Device device, int x, int y)
    {
        var monitor = MonitorInfo.All().FirstOrDefault(m => m.IsPrimary);
        if (monitor is null) return null;

        var spanX = device.MaxX - device.MinX;
        var spanY = device.MaxY - device.MinY;

        if (spanX <= 0 || spanY <= 0) return null;

        var bounds = monitor.Bounds;

        var screenX = bounds.Left + (int)Math.Round((x - device.MinX) / (double)spanX * bounds.Width);
        var screenY = bounds.Top + (int)Math.Round((y - device.MinY) / (double)spanY * bounds.Height);

        return (screenX, screenY);
    }

    /// <summary>デバイスの読み方を引く。初回だけ調べ、以後は覚えたものを返す。</summary>
    private static Device? DeviceFor(nint handle)
    {
        if (handle == 0) return null;

        lock (Devices)
        {
            if (Devices.TryGetValue(handle, out var known)) return known;

            var device = Describe(handle);
            Devices[handle] = device;

            return device;
        }
    }

    private static Device Describe(nint handle)
    {
        var device = new Device();

        uint size = 0;

        if (NativeMethods.GetRawInputDeviceInfoW(
                handle, NativeMethods.RIDI_PREPARSEDDATA, 0, ref size) != 0 || size == 0)
        {
            return device;
        }

        // 下ごしらえ済みのデータは、このデバイスの報告を読む間ずっと使う。
        // 解放するのはプロセスの終わりで、そこは OS に任せる。
        var preparsed = Marshal.AllocHGlobal((int)size);

        if (NativeMethods.GetRawInputDeviceInfoW(
                handle, NativeMethods.RIDI_PREPARSEDDATA, preparsed, ref size) == unchecked((uint)-1))
        {
            Marshal.FreeHGlobal(preparsed);
            return device;
        }

        device.Preparsed = preparsed;

        if (NativeMethods.HidP_GetCaps(preparsed, out var caps) != NativeMethods.HIDP_STATUS_SUCCESS)
        {
            return device;
        }

        var count = caps.NumberInputValueCaps;
        if (count == 0) return device;

        var values = new NativeMethods.HIDP_VALUE_CAPS[count];

        if (NativeMethods.HidP_GetValueCaps(
                NativeMethods.HIDP_REPORT_TYPE_INPUT, values, ref count, preparsed)
            != NativeMethods.HIDP_STATUS_SUCCESS)
        {
            return device;
        }

        var hasX = false;
        var hasY = false;

        foreach (var value in values)
        {
            if (value.UsagePage != NativeMethods.HID_USAGE_PAGE_GENERIC) continue;

            if (value.Usage == NativeMethods.HID_USAGE_GENERIC_X && !hasX)
            {
                device.MinX = value.LogicalMin;
                device.MaxX = value.LogicalMax;
                hasX = true;
            }
            else if (value.Usage == NativeMethods.HID_USAGE_GENERIC_Y && !hasY)
            {
                device.MinY = value.LogicalMin;
                device.MaxY = value.LogicalMax;
                hasY = true;
            }
        }

        device.Usable = hasX && hasY && device.MaxX > device.MinX && device.MaxY > device.MinY;

        return device;
    }
}
