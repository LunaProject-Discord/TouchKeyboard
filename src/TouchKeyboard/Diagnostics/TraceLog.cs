using System;
using System.IO;
using System.Text;

namespace TouchKeyboard.Diagnostics;

/// <summary>
/// 表示・非表示の判断を記録する。原因の切り分け用。
///
/// 自動表示は、フォーカス移動・物理キー・キーボードの着脱と入り口が複数ある。
/// どの経路で隠れたかは外から観測しても分からないため、アプリ自身に書かせる。
/// </summary>
public static class TraceLog
{
    private const long MaxBytes = 1 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly DateTime Started = DateTime.Now;

    private static string? _path;

    /// <summary>
    /// 記録するか。既定は無効。
    ///
    /// 自動表示の判断は入り口が多く、外から観測しても原因を追えない。
    /// 不具合を追うときだけ settings.json で有効にする。実行ファイルは
    /// Program Files に署名して置く必要があるため、再ビルドせずに切り替えられることが要る。
    /// </summary>
    public static bool Enabled { get; set; }

    public static string Path => _path ??= Prepare();

    private static string Prepare()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TouchKeyboard");

        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "trace.log");

        // 起動のたびに肥大しないよう、大きくなっていたら捨てる。
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
        }
        catch (IOException)
        {
            // 消せなくても記録は続ける。
        }

        return path;
    }

    public static void Write(string message)
    {
        if (!Enabled) return;

        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
                .Append("  +")
                .Append(((int)(DateTime.Now - Started).TotalMilliseconds).ToString().PadLeft(7))
                .Append("ms  ")
                .Append(message)
                .Append(Environment.NewLine);

            lock (Gate) File.AppendAllText(Path, line.ToString());
        }
        catch (Exception)
        {
            // 記録が取れないことを理由に動作を止めない。
        }
    }
}
