namespace TouchKeyboard.Interop;

/// <summary>
/// サインアウト・シャットダウン・再起動のときの扱い。
/// </summary>
public static class ProcessShutdown
{
    /// <summary>
    /// 終了に時間がかかっても、シャットダウンを妨げているアプリとして出さない。
    ///
    /// キーボードのウィンドウは隠している間もクロークで残しているため、OS からは
    /// 表示中のウィンドウに見える。終了が少しでも遅れると「このアプリがシャット
    /// ダウンを妨げています」の一覧に出てしまう（利用者の報告：「シャットダウンや
    /// 再起動時にこのアプリがブロックしていると出てきます」）。
    /// 確保した作業領域は WM_ENDSESSION で返してから落ちるので、待たせる理由は無い。
    /// </summary>
    /// <returns>設定できたら true。</returns>
    public static bool DoNotBlock() =>
        NativeMethods.SetProcessShutdownParameters(
            NativeMethods.SHUTDOWN_LEVEL_DEFAULT, NativeMethods.SHUTDOWN_NORETRY);
}
