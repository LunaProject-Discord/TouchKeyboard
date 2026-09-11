namespace TouchKeyboard.Interop;

/// <summary>他アプリの IME の状態を読む。</summary>
public static class ImeState
{
    /// <summary>
    /// そのウィンドウで IME が入っているか。読めなければ null。
    ///
    /// <see cref="NativeMethods.ImmGetDefaultIMEWnd"/> で相手スレッドの IME の窓を引き、
    /// <c>WM_IME_CONTROL</c> で問い合わせる。<c>ImmGetOpenStatus</c> は自プロセスの
    /// 入力コンテキストしか読めないため、他アプリの状態はこの経路になる。
    ///
    /// 読むのは入／切だけにする。変換モード（<c>IMC_GETCONVERSIONMODE</c>）も取れるが、
    /// ATOK では実測で NATIVE・KATAKANA・FULLSHAPE が立ち、ローマ字入力を示す
    /// ROMAN は立たなかった。TSF 側で実装された IME の値は互換層を通した近似で、
    /// 入力方式の判別には使えない。
    /// </summary>
    public static bool? IsOpen(nint window)
    {
        if (window == 0) return null;

        var ime = NativeMethods.ImmGetDefaultIMEWnd(window);
        if (ime == 0) return null;

        // 相手が固まっていても待たない。ここは表示の更新のために定期的に通る。
        var sent = NativeMethods.SendMessageTimeoutW(
            ime,
            NativeMethods.WM_IME_CONTROL,
            NativeMethods.IMC_GETOPENSTATUS,
            0,
            NativeMethods.SMTO_ABORTIFHUNG,
            TimeoutMs,
            out var result);

        return sent == 0 ? null : result != 0;
    }

    /// <summary>問い合わせの上限（ミリ秒）。</summary>
    private const uint TimeoutMs = 200;
}
