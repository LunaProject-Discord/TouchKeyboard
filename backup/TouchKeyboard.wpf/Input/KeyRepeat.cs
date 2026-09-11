using System;
using System.Windows;
using System.Windows.Threading;

namespace TouchKeyboard.Input;

/// <summary>
/// 長押しによるキーリピート。
///
/// 既定値は OS の設定（SPI_GETKEYBOARDDELAY / SPI_GETKEYBOARDSPEED）を基準にするが、
/// タッチは物理キーより誤リピートしやすいため、開始遅延を OS 設定より長めに取る。
/// </summary>
public sealed class KeyRepeat : IDisposable
{
    private readonly DispatcherTimer _timer;
    private Action? _action;
    private bool _started;

    /// <summary>リピート開始までの遅延。</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>リピートの間隔。</summary>
    public TimeSpan Interval { get; set; }

    public KeyRepeat()
    {
        (Delay, Interval) = ReadSystemDefaults();

        _timer = new DispatcherTimer(DispatcherPriority.Input);
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// OS のキーボード設定を読む。
    /// KeyboardDelay は 0〜3（250ms 単位）、KeyboardSpeed は 0〜31（速いほど大きい）。
    /// </summary>
    private static (TimeSpan Delay, TimeSpan Interval) ReadSystemDefaults()
    {
        // SystemParameters.KeyboardDelay: 0 → 250ms, 3 → 1000ms
        var delayMs = (SystemParameters.KeyboardDelay + 1) * 250;

        // SystemParameters.KeyboardSpeed: 0 → 約 400ms 間隔, 31 → 約 33ms 間隔
        var speed = Math.Clamp(SystemParameters.KeyboardSpeed, 0, 31);
        var intervalMs = 400 - (speed * (400 - 33) / 31);

        // タッチは指が触れたままになりやすく、物理キーより誤リピートしやすい。
        // OS 設定より開始遅延を長めに取る。
        delayMs = Math.Max(delayMs, 600);

        return (TimeSpan.FromMilliseconds(delayMs), TimeSpan.FromMilliseconds(intervalMs));
    }

    /// <summary>押下時に呼ぶ。<paramref name="action"/> は Delay 経過後から Interval ごとに呼ばれる。</summary>
    public void Start(Action action)
    {
        Stop();

        _action = action;
        _started = false;
        _timer.Interval = Delay;
        _timer.Start();
    }

    /// <summary>解放時に呼ぶ。</summary>
    public void Stop()
    {
        _timer.Stop();
        _action = null;
        _started = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_started)
        {
            // 初回の Tick は遅延の経過。ここから間隔を切り替える。
            _started = true;
            _timer.Interval = Interval;
        }

        _action?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _action = null;
    }
}
