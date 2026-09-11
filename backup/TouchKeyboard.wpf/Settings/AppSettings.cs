using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace TouchKeyboard.Settings;

/// <summary>
/// 設定の読み書き。
///
/// 保存先は %APPDATA%\TouchKeyboard\settings.json。
/// 実行ファイル横ではなくユーザープロファイル配下に置くのは、
/// Program Files へ配置しても書き込めるようにするため。
/// </summary>
public sealed class AppSettings
{
    /// <summary>キーボードの高さ。DIP で持つ。物理ピクセルで持つとモニタごとに作り直しになる。</summary>
    public double HeightDip { get; set; } = 320;

    /// <summary>前回終了時に表示されていたか。</summary>
    public bool Visible { get; set; } = true;

    /// <summary>入力欄へのフォーカスで自動表示するか（Phase 4 で使用）。</summary>
    public bool AutoShow { get; set; } = true;

    /// <summary>ドッキング先モニタのデバイス名。null ならウィンドウの載っているモニタ。</summary>
    public string? MonitorDeviceName { get; set; }

    /// <summary>
    /// タスクバーを覆う位置に配置するか。標準タッチキーボードと同じ見え方になる。
    /// AppBar が確保する領域は変わらない。ウィンドウを画面下端まで伸ばすだけ。
    /// </summary>
    public bool CoverTaskbar { get; set; } = true;

    /// <summary>高さの下限。DIP。極端に小さくしてキーが押せなくなるのを防ぐ。</summary>
    public const double MinHeightDip = 160;

    /// <summary>
    /// 保存値の健全性チェック用の絶対上限。DIP。
    /// 実際の上限はモニタごとに決まるため <see cref="ClampHeight"/> を使うこと。
    /// </summary>
    public const double AbsoluteMaxHeightDip = 2000;

    /// <summary>
    /// ドッキング中の高さの上限。画面高さの半分とする。
    /// これ以上大きいと、キーボードが画面の大半を占めて実用にならない。
    /// </summary>
    public static double MaxHeightDipFor(double monitorHeightDip) => monitorHeightDip / 2;

    /// <summary>モニタの高さを踏まえて高さを丸める。</summary>
    public static double ClampHeight(double heightDip, double monitorHeightDip)
    {
        var max = Math.Max(MinHeightDip, MaxHeightDipFor(monitorHeightDip));
        return Math.Clamp(heightDip, MinHeightDip, max);
    }

    [JsonIgnore]
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TouchKeyboard");

    [JsonIgnore]
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>読み込む。ファイルが無い・壊れている場合は既定値を返す。</summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();

            using var stream = File.OpenRead(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(stream, Options) ?? new AppSettings();

            // ここではモニタが分からないため絶対値のみで丸める。
            // 実際の上限（画面高さの半分）はドッキング時に ClampHeight で適用される。
            settings.HeightDip = Math.Clamp(settings.HeightDip, MinHeightDip, AbsoluteMaxHeightDip);
            return settings;
        }
        catch (Exception)
        {
            // 設定が読めないことでアプリが起動しないのは避ける。既定値で続行する。
            return new AppSettings();
        }
    }

    /// <summary>保存する。失敗しても例外は投げない。</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            using var stream = File.Create(FilePath);
            JsonSerializer.Serialize(stream, this, Options);
        }
        catch (Exception)
        {
            // 保存できなくても動作は続行する。
        }
    }

    // ------------------------------------------------------------------
    // 自動起動
    // ------------------------------------------------------------------

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "TouchKeyboard";

    /// <summary>Windows 起動時の自動起動が有効か。レジストリを直接見る。</summary>
    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // 権限がない環境でも落とさない。
        }
    }
}
