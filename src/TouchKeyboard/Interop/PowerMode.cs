using System;
using Windows.System.Power;

namespace TouchKeyboard.Interop;

/// <summary>
/// 低電力モード（バッテリー節約機能）の状態を教える。
///
/// Acrylic はウィンドウの背後をリアルタイムでぼかし続けるため、常時 GPU を使う。
/// 低電力モード中はこれを避け、より軽い素材へ切り替える判断材料に使う。
/// </summary>
public static class PowerMode
{
    /// <summary>いま低電力モードが有効か。</summary>
    public static bool IsLowPower => PowerManager.EnergySaverStatus == EnergySaverStatus.On;

    /// <summary>低電力モードの入切で発火する。UI スレッドで呼ばれるとは限らない。</summary>
    public static event EventHandler<object>? Changed
    {
        add => PowerManager.EnergySaverStatusChanged += value;
        remove => PowerManager.EnergySaverStatusChanged -= value;
    }
}
