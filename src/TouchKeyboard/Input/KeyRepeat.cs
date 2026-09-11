using System;
using TouchKeyboard.Interop;

namespace TouchKeyboard.Input;

/// <summary>
/// 長押しリピートの時間を決める。
///
/// リピートそのものは RepeatButton に任せる。自前のタイマーより
/// 押下判定やポインタのキャンセル処理が確実なため。
/// ここでは OS の設定と利用者の設定から、遅延と間隔を算出するだけ。
/// </summary>
public static class KeyRepeat
{
    /// <summary>
    /// 開始遅延の下限（ミリ秒）。
    /// タッチは指が触れたままになりやすく、物理キーより誤リピートしやすいので少し長く取る。
    /// ただし長すぎると反応が鈍く感じられる。
    /// </summary>
    private const int MinDelayMs = 300;

    /// <summary>間隔の下限。短すぎると入力が流れる。</summary>
    private const int MinIntervalMs = 30;

    /// <summary>
    /// OS の設定（SPI_GETKEYBOARDDELAY / SPI_GETKEYBOARDSPEED）を読み、
    /// 利用者の設定があればそちらを優先する。
    /// </summary>
    public static (int DelayMs, int IntervalMs) Resolve(int? overrideDelay, int? overrideInterval)
    {
        var (osDelay, osInterval) = ReadSystemDefaults();

        var delay = overrideDelay ?? osDelay;
        var interval = overrideInterval ?? osInterval;

        return (Math.Max(delay, MinDelayMs), Math.Max(interval, MinIntervalMs));
    }

    /// <summary>
    /// 遅延は 0〜3（250ms 単位）、速度は 0〜31（速いほど大きい）。
    /// </summary>
    private static (int DelayMs, int IntervalMs) ReadSystemDefaults()
    {
        var delayMs = 500;
        if (NativeMethods.SystemParametersInfoW(
                NativeMethods.SPI_GETKEYBOARDDELAY, 0, out var delaySetting, 0))
        {
            delayMs = (Math.Clamp(delaySetting, 0, 3) + 1) * 250;
        }

        var intervalMs = 33;
        if (NativeMethods.SystemParametersInfoW(
                NativeMethods.SPI_GETKEYBOARDSPEED, 0, out var speedSetting, 0))
        {
            var speed = Math.Clamp(speedSetting, 0, 31);
            intervalMs = 400 - (speed * (400 - 33) / 31);
        }

        return (delayMs, intervalMs);
    }
}
