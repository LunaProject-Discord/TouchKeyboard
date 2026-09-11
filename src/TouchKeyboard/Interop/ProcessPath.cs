using System;
using System.Text;

namespace TouchKeyboard.Interop;

/// <summary>プロセス ID から実行ファイルのパスを引く。</summary>
public static class ProcessPath
{
    /// <summary>
    /// 実行ファイルのフルパス。取れなければ空文字。
    ///
    /// <c>Process.MainModule</c> は使わない。相手の整合性レベルによっては
    /// モジュール一覧を開けず例外になる。<c>QueryFullProcessImageName</c> なら
    /// 限定的な問い合わせ権限だけで取れる。
    /// </summary>
    public static string Of(int processId)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);

        if (handle == 0) return string.Empty;

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;

            return NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? buffer.ToString()
                : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
