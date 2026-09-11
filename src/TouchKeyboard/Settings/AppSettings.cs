using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using TouchKeyboard.Layout;

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
    public double HeightDip { get; set; } = 420;

    /// <summary>前回終了時に表示されていたか。</summary>
    public bool Visible { get; set; } = true;

    /// <summary>入力欄へのフォーカスで自動表示するか（Phase 4 で使用）。</summary>
    public bool AutoShow { get; set; } = true;

    /// <summary>
    /// アプリごとの自動表示の扱い。キーは実行ファイルのフルパス。
    ///
    /// 自動表示が起きたアプリは、初めて見た時点でここに追加される。
    /// あとから settings.json を編集して <see cref="AutoShowPolicy"/> を選べる。
    /// 変更はアプリの再起動で反映される。
    /// </summary>
    public Dictionary<string, AutoShowPolicy> AutoShowRules { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 初めて見たときに <see cref="AutoShowPolicy.Strict"/> を既定とするプロセス名。
    ///
    /// シェルの UI は開いただけで入力欄へフォーカスを移す。そのまま Auto にすると
    /// スタートメニューを開くたびに出てしまう。利用者が自分で押したときだけ出す。
    /// </summary>
    private static readonly string[] StrictByDefault =
    [
        "SearchHost",                // スタートメニューとタスクバーの検索
        "StartMenuExperienceHost",   // スタートメニュー本体
        "ShellExperienceHost",       // 通知センターなどのシェル UI
    ];

    /// <summary>
    /// <see cref="AutoShowRules"/> を守る錠。
    ///
    /// 参照と追加は UI Automation のコールバックスレッドから来る。一方で保存と
    /// 設定画面は UI スレッドから触る。Dictionary は同時書き換えに耐えず、
    /// 壊れると参照が終わらなくなって CPU を食い尽くす。
    /// </summary>
    private static readonly object Gate = new();

    /// <summary>
    /// そのアプリの扱いを返す。未登録なら既定を決めて登録する。
    /// 一覧に現れて初めて、利用者は設定できることに気付ける。
    /// </summary>
    public AutoShowPolicy PolicyFor(string executablePath)
    {
        if (executablePath.Length == 0) return AutoShowPolicy.Auto;

        bool added;
        AutoShowPolicy policy;

        lock (Gate)
        {
            if (AutoShowRules.TryGetValue(executablePath, out policy)) return policy;

            var name = Path.GetFileNameWithoutExtension(executablePath);
            policy = StrictByDefault.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? AutoShowPolicy.Strict
                : AutoShowPolicy.Auto;

            AutoShowRules[executablePath] = policy;
            added = true;
        }

        // 保存はここでは行わない。呼び出し元は入力の経路上にあり、
        // ファイル書き込みで待たせるとフォーカス移動そのものが詰まる。
        if (added) SaveLater();

        return policy;
    }

    /// <summary>一覧の写しを返す。設定画面が並べるのに使う。</summary>
    public List<KeyValuePair<string, AutoShowPolicy>> RuleSnapshot()
    {
        lock (Gate) return [.. AutoShowRules];
    }

    /// <summary>扱いを変える。設定画面から呼ぶ。</summary>
    public void SetPolicy(string executablePath, AutoShowPolicy policy)
    {
        lock (Gate) AutoShowRules[executablePath] = policy;
        Save();
    }

    /// <summary>保存を別スレッドへ逃がす。連続して呼ばれても 1 回にまとめる。</summary>
    private int _savePending;

    private void SaveLater()
    {
        if (Interlocked.Exchange(ref _savePending, 1) == 1) return;

        Task.Run(() =>
        {
            Thread.Sleep(500);
            Interlocked.Exchange(ref _savePending, 0);
            Save();
        });
    }

    /// <summary>
    /// 表示・非表示の判断を %APPDATA%\TouchKeyboard\trace.log に記録するか。
    ///
    /// 自動表示は、フォーカス移動・物理キー・ポインタの持ち替え・キーボードの着脱と
    /// 入り口が多い。どの経路で動いたかは外から観測しても分からないため、
    /// 不具合を追うときはここを true にする。
    /// </summary>
    public bool Trace { get; set; }

    /// <summary>
    /// ピン留めしたクリップボードの内容。上に並べる順に持つ。
    ///
    /// OS の履歴にはピン留めを読み書きする API が無い。Windows 自身の画面では
    /// 留められるが、外からは触れない。そのため独自に持つ。
    /// 中身そのものを持つのは、履歴から溢れても残すため。
    /// </summary>
    public List<string> ClipboardPins { get; set; } = [];

    /// <summary>
    /// 使うレイアウトのファイル名。layouts/ 配下を指す。
    ///
    /// 読めなかった場合は既定に戻す。名前を書き間違えただけで
    /// キーボードが出なくなるのは行き過ぎる。
    /// </summary>
    public string LayoutFile { get; set; } = LayoutLoader.DefaultFileName;

    /// <summary>
    /// 画面端から離して浮かせるか。
    ///
    /// 浮かせている間は作業領域を確保しない。他のアプリは画面いっぱいを使い、
    /// キーボードはその上に重なる。位置と大きさは下の 4 つで覚える。
    /// </summary>
    public bool Floating { get; set; }

    /// <summary>浮かせているときの位置と幅。DIP。null なら画面中央に置く。</summary>
    public double? FloatingXDip { get; set; }

    public double? FloatingYDip { get; set; }

    public double? FloatingWidthDip { get; set; }

    /// <summary>
    /// 浮かせているときのキー段の高さ。DIP。タイトルバーは含めない。
    ///
    /// <see cref="HeightDip"/> とは別に持つ。あちらは画面端に接しているときの高さで、
    /// 作業領域の確保に使う。浮かせている間の大きさに引きずられると、
    /// 画面端に戻したときに他のアプリの領域まで変わってしまう。
    /// null ならドッキング中の高さをそのまま使う。
    /// </summary>
    public double? FloatingHeightDip { get; set; }

    /// <summary>
    /// 背景に敷く素材。
    ///
    /// 標準タッチキーボードは Acrylic だが、既定は Mica にしている。
    /// 下のウィンドウが透けないぶん落ち着いて見え、キーも読みやすいため。
    /// 見え方を標準に寄せたい場合は Acrylic を選ぶ。
    /// </summary>
    public BackdropMaterial Backdrop { get; set; } = BackdropMaterial.Mica;

    /// <summary>ドッキング先モニタのデバイス名。null ならウィンドウの載っているモニタ。</summary>
    public string? MonitorDeviceName { get; set; }

    // ------------------------------------------------------------------
    // 置き場所の記憶
    // ------------------------------------------------------------------

    /// <summary>
    /// 置き場所。組み合わせごとに 1 つ持つ。
    ///
    /// 高さの意味は浮かせているかで変わる。画面端に接しているときは作業領域を
    /// 確保する高さ、浮かせているときはキー段の高さ。組み合わせが違えば別の枠に
    /// 入るので、1 つの欄で足りる。
    /// </summary>
    public sealed class Placement
    {
        public double? HeightDip { get; set; }
        public double? WidthDip { get; set; }
        public double? XDip { get; set; }
        public double? YDip { get; set; }
    }

    /// <summary>
    /// 組み合わせごとの置き場所。
    ///
    /// 鍵は「配列・浮かせているか・モニタ・画面の向き」。同じ配列でも、縦に回すと
    /// 入る大きさが変わり、モニタを移せば適した高さも変わる。1 つの値で持つと、
    /// 戻ってくるたびに直すことになる。
    /// </summary>
    public Dictionary<string, Placement> Placements { get; set; } = new();

    /// <summary>いま使っている組み合わせ。空なら未設定。</summary>
    [JsonIgnore]
    public string PlacementContext { get; private set; } = string.Empty;

    /// <summary>
    /// 組み合わせを切り替える。
    ///
    /// 直前の組み合わせへ、いまの値を書き戻してから読み替える。
    /// 覚えが無い組み合わせでは、いまの値をそのまま引き継ぐ。初めて開いたときに
    /// 既定値へ飛ぶより、直前の大きさから始めたほうが手数が少ない。
    /// </summary>
    /// <returns>切り替わったら true。</returns>
    public bool UseContext(string key)
    {
        if (string.IsNullOrEmpty(key) || key == PlacementContext) return false;

        StoreCurrentPlacement();
        PlacementContext = key;

        if (Placements.TryGetValue(key, out var saved)) Restore(saved);

        return true;
    }

    /// <summary>いまの値を、いまの組み合わせの枠へ書き戻す。</summary>
    private void StoreCurrentPlacement()
    {
        if (string.IsNullOrEmpty(PlacementContext)) return;

        Placements[PlacementContext] = Floating
            ? new Placement
            {
                HeightDip = FloatingHeightDip,
                WidthDip = FloatingWidthDip,
                XDip = FloatingXDip,
                YDip = FloatingYDip,
            }
            : new Placement { HeightDip = HeightDip };
    }

    private void Restore(Placement saved)
    {
        if (Floating)
        {
            FloatingHeightDip = saved.HeightDip ?? FloatingHeightDip;
            FloatingWidthDip = saved.WidthDip ?? FloatingWidthDip;
            FloatingXDip = saved.XDip ?? FloatingXDip;
            FloatingYDip = saved.YDip ?? FloatingYDip;

            return;
        }

        if (saved.HeightDip is { } height) HeightDip = height;
    }

    /// <summary>
    /// タスクバーを覆う位置に配置するか。標準タッチキーボードと同じ見え方になる。
    /// AppBar が確保する領域は変わらない。ウィンドウを画面下端まで伸ばすだけ。
    /// </summary>
    public bool CoverTaskbar { get; set; } = true;

    /// <summary>
    /// 長押しリピートの開始遅延（ミリ秒）。null なら OS の設定に従う。
    /// 反応が鈍いと感じるときはここを小さくする。
    /// </summary>
    public int? RepeatDelayMs { get; set; }

    /// <summary>
    /// 長押しリピートの間隔（ミリ秒）。null なら OS の設定に従う。
    /// </summary>
    public int? RepeatIntervalMs { get; set; }

    /// <summary>
    /// 高さの下限。DIP。極端に小さくしてキーが押せなくなるのを防ぐ。
    /// 7 段構成のため、1 段あたりが指で押せる大きさを保てる値にしてある。
    /// </summary>
    public const double MinHeightDip = 260;

    /// <summary>
    /// 浮かせているときのキー段の高さの下限。DIP。タイトルバーを含まない。
    ///
    /// ドッキング中とは別に持つ。あちらは指で押すための大きさだが、
    /// 浮かせている間は隅に寄せて配列を確かめるような使い方もある。
    /// 下限は印字が余白の中に収まるところに置く。
    ///
    /// 字の大きさはウィンドウ高さの 0.065 倍を上限とする（KeyButton）。
    /// キーの高さから引かれるのは、隣との隙間 3 が上下で 6、
    /// 文字キーの余白 8 が上下で 16 の、合わせて 22。
    /// 段は 5.5 ユニットぶん積むので、タイトルバー 48 を含めた高さを W とすると
    ///   (W - 48) / 5.5 - 22 ≧ 字の外形（0.065W の 8 割）
    /// これを解くと W ≧ 237。余裕を見て 240、キー段はその 192 とする。
    /// このとき字は 15.6 になる。
    /// </summary>
    public const double MinFloatingBodyDip = 192;

    /// <summary>
    /// 浮かせているときの幅の下限。DIP。
    ///
    /// 幅を決めるのは、1 ユニットのキーに入る最も長い印字。
    /// クラシックの「無変換」で、全角 3 文字ぶんの幅がある。
    ///
    /// 幅に収まらない字は、そのキーだけ縮めて収める（KeyButton）。
    /// したがって下限は「はみ出さない大きさ」ではなく「読める大きさで収まる」ところに置く。
    /// 730 なら 1 ユニットが 47、隙間 6 と余白 8 を引いた 33 に全角 3 文字が入り、
    /// このとき字は 11 になる。Shift の併記と同じで、この盤で最も小さい字にあたる。
    /// </summary>
    public const double MinFloatingWidthDip = 730;

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

        // 扱いを "Auto" / "Strict" / "Suppress" と書けるようにする。
        // 数値だと編集する側が意味を読み取れない。
        Converters = { new JsonStringEnumConverter() },
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

            // 浮かせているときの大きさも同様に、壊れた値で出てこないようにする。
            // 下限はドッキング中とは別。あちらより小さくできる。
            if (settings.FloatingHeightDip is { } floatingHeight)
            {
                settings.FloatingHeightDip =
                    Math.Clamp(floatingHeight, MinFloatingBodyDip, AbsoluteMaxHeightDip);
            }

            if (settings.FloatingWidthDip is { } floatingWidth)
            {
                settings.FloatingWidthDip = Math.Max(floatingWidth, MinFloatingWidthDip);
            }

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

            // いまの大きさと位置を、いまの組み合わせの枠へ写してから書き出す。
            // 保存の呼び出し元は各所にあるので、ここで一括して面倒を見る。
            StoreCurrentPlacement();

            // 直列化中に AutoShowRules が書き換わると壊れる。
            // 追加は UI Automation のスレッドから来るため、必ず閉じ込める。
            byte[] bytes;
            lock (Gate)
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options);
            }

            File.WriteAllBytes(FilePath, bytes);
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
