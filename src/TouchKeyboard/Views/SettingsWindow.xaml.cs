using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TouchKeyboard.Diagnostics;
using TouchKeyboard.Interop;
using TouchKeyboard.Layout;
using TouchKeyboard.Settings;

namespace TouchKeyboard.Views;

/// <summary>
/// 設定画面。
///
/// キーボード本体と違いフォーカスを奪ってよい。編集のための普通のウィンドウ。
/// 変更はその場で保存し、反映できるものは即座に反映する。
/// 反映に再起動が要るものは画面上でそう伝える。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly KeyboardWindow _keyboard;

    /// <summary>読み込み中は変更通知を無視する。初期値の反映で保存が走らないように。</summary>
    private bool _loading = true;

    public SettingsWindow(AppSettings settings, KeyboardWindow keyboard)
    {
        _settings = settings;
        _keyboard = keyboard;

        InitializeComponent();

        Title = "TouchKeyboard の設定";

        // タスクトレイのアイコンと同じキーボードの絵。タイトルバーと Alt+Tab に出る。
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // 素材は Mica。このウィンドウは普通に活性化するので、
        // WS_EX_NOACTIVATE 用の WindowBackdrop（KeyboardWindow が使う回避策）は要らない。
        // 簡易 API のままで、アクティブ状態に応じたぼかしの切り替えも自然に働く。
        SystemBackdrop = new MicaBackdrop();

        // 背の高いタイトルバー。Windows の設定と同じ 48。
        //
        // WinUI Gallery の TitleBarWindow サンプルの順序に合わせる。
        // PreferredHeightOption を SetTitleBar より後に設定していたときは、
        // 高さが Tall まで届かなかった（実機で確認）。SetTitleBar が渡した時点の
        // 大きさで測ってしまい、あとから高さの希望を変えても反映されない模様。
        ExtendsContentIntoTitleBar = true;

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }

        SetTitleBar(AppTitleBar);

        Resize(980, 760);

        Load();
        Wire();
        ShowSection("display", "表示");

        _loading = false;
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        Nav.IsPaneOpen = !Nav.IsPaneOpen;

    /// <summary>
    /// 中身の幅の上限。これを超えると 1 行が長くなりすぎて読みにくい。
    /// </summary>
    private const double ContentMaxWidth = 1000;

    /// <summary>中身の左右に空ける余白。</summary>
    private const double ContentPadding = 28;

    /// <summary>
    /// 中身の幅を、送り枠の見えている幅から決める。
    ///
    /// 幅を指定に任せると、ページの中身によって変わったり、窓の大きさによって
    /// 左右の位置が変わったりする。実測では、最大化したときに右へ寄って
    /// 端が切れていた。ここで数値として入れれば、どのページでも同じ幅になり、
    /// 中央寄せの指定がそのまま効く。
    /// </summary>
    private void ApplyContentWidth(double viewportWidth)
    {
        if (viewportWidth <= 0) return;

        var width = Math.Min(ContentMaxWidth, viewportWidth - (ContentPadding * 2));

        PageContent.Width = Math.Max(320, width);
    }

    /// <summary>
    /// 大きさを DIP で指定して開く。
    ///
    /// AppWindow.Resize が受け取るのは物理ピクセル。拡大率 200% の画面で
    /// そのまま数値を渡すと、見た目の大きさが半分になる。中身が入らず、
    /// ページが右へはみ出す。拡大率を掛けてから渡す。
    ///
    /// 画面より大きくならないよう作業領域に収める。
    /// </summary>
    private void Resize(int widthDip, int heightDip)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var monitor = MonitorInfo.FromWindow(hwnd);

        var scale = monitor?.DpiScale ?? 1.0;

        var width = (int)Math.Round(widthDip * scale);
        var height = (int)Math.Round(heightDip * scale);

        if (monitor is { } screen)
        {
            width = Math.Min(width, (int)(screen.WorkArea.Width * 0.9));
            height = Math.Min(height, (int)(screen.WorkArea.Height * 0.9));
        }

        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    /// <summary>
    /// 分類を切り替える。
    ///
    /// Windows の設定と同じく、左で選んだものだけを右に出す。
    /// 一枚に全部を並べて送る形だと、目当ての項目まで遠くなる。
    /// </summary>
    private void ShowSection(string tag, string title)
    {
        PageTitle.Text = title;

        DisplaySection.Visibility = Visible(tag == "display");
        AutoShowSection.Visibility = Visible(tag == "autoShow");
        KeysSection.Visibility = Visible(tag == "keys");
        OtherSection.Visibility = Visible(tag == "other");

        static Visibility Visible(bool shown) =>
            shown ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // 初期値の反映
    // ------------------------------------------------------------------

    private void Load()
    {
        // 高さの上限はモニタごとに決まる。いま載っているモニタを基準にする。
        var monitorHeight = _keyboard.CurrentMonitorHeightDip;
        HeightSlider.Minimum = AppSettings.MinHeightDip;
        HeightSlider.Maximum = Math.Max(
            AppSettings.MinHeightDip + 1, AppSettings.MaxHeightDipFor(monitorHeight));
        HeightSlider.Value = Math.Clamp(_settings.HeightDip, HeightSlider.Minimum, HeightSlider.Maximum);
        ShowHeight();

        CoverTaskbarToggle.IsOn = _settings.CoverTaskbar;
        AutoShowToggle.IsOn = _settings.AutoShow;
        AutoStartToggle.IsOn = AppSettings.IsAutoStartEnabled();
        TraceToggle.IsOn = _settings.Trace;

        RepeatDelayBox.Value = _settings.RepeatDelayMs ?? double.NaN;
        RepeatIntervalBox.Value = _settings.RepeatIntervalMs ?? double.NaN;

        LoadMonitors();
        LoadBackdrops();
        LoadLayouts();
        LoadRules();
    }

    private void LoadLayouts()
    {
        LayoutCombo.Items.Clear();

        foreach (var (fileName, displayName) in LayoutLoader.Available)
        {
            LayoutCombo.Items.Add(new ComboBoxItem { Content = displayName, Tag = fileName });
        }

        // 一覧に無いものが設定されていることもある。手で足した配列など。
        // その場合は選択を空のままにして、触らない限り書き換えない。
        for (var i = 0; i < LayoutLoader.Available.Count; i++)
        {
            if (!string.Equals(
                LayoutLoader.Available[i].FileName,
                _settings.LayoutFile,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            LayoutCombo.SelectedIndex = i;
            break;
        }
    }

    private static readonly (BackdropMaterial Value, string Label)[] BackdropChoices =
    [
        (BackdropMaterial.Acrylic, "Acrylic — 下のウィンドウをぼかす（標準と同じ）"),
        (BackdropMaterial.Mica, "Mica — 壁紙を素材にする"),
        (BackdropMaterial.MicaAlt, "Mica Alt — Mica の濃いほう"),
    ];

    private void LoadBackdrops()
    {
        BackdropCombo.Items.Clear();

        foreach (var (value, label) in BackdropChoices)
        {
            BackdropCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        BackdropCombo.SelectedIndex =
            Array.FindIndex(BackdropChoices, choice => choice.Value == _settings.Backdrop);
    }

    private void LoadMonitors()
    {
        MonitorCombo.Items.Clear();

        // 先頭は「自動」。ウィンドウの載っているモニタに従う。
        MonitorCombo.Items.Add(new ComboBoxItem { Content = "自動", Tag = string.Empty });

        foreach (var monitor in MonitorInfo.All())
        {
            MonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{monitor.DeviceName}  {monitor.Bounds.Width}×{monitor.Bounds.Height}",
                Tag = monitor.DeviceName,
            });
        }

        var current = _settings.MonitorDeviceName ?? string.Empty;
        MonitorCombo.SelectedIndex = Math.Max(0, MonitorCombo.Items
            .OfType<ComboBoxItem>()
            .ToList()
            .FindIndex(item => (string)item.Tag == current));
    }

    /// <summary>
    /// アプリごとの扱いを並べる。
    ///
    /// 自動表示が起きたアプリは、初めて見た時点で設定に追加されている。
    /// ここでは並べ替えて見せるだけで、一覧そのものは利用状況が作る。
    /// </summary>
    private void LoadRules()
    {
        RulesExpander.Items.Clear();

        // 一覧は監視スレッドからも増える。写しを取ってから並べる。
        var rules = _settings.RuleSnapshot()
            .OrderBy(rule => Path.GetFileNameWithoutExtension(rule.Key), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (rules.Count == 0)
        {
            RulesExpander.Items.Add(new SettingsCard
            {
                Header = "まだ記録がありません",
                Description = "入力欄にフォーカスを当てると、そのアプリがここに並びます。",
                IsEnabled = false,
            });

            return;
        }

        foreach (var (path, policy) in rules)
        {
            RulesExpander.Items.Add(CreateRuleCard(path, policy));
        }
    }

    private SettingsCard CreateRuleCard(string path, AutoShowPolicy policy)
    {
        var combo = new ComboBox { MinWidth = 200 };

        foreach (var (value, label) in PolicyChoices)
        {
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        combo.SelectedIndex = Array.FindIndex(PolicyChoices, choice => choice.Value == policy);

        combo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            if (combo.SelectedItem is not ComboBoxItem { Tag: AutoShowPolicy selected }) return;

            _settings.SetPolicy(path, selected);
            Notify("アプリごとの扱いは、キーボードを再起動すると反映されます。");
        };

        return new SettingsCard
        {
            Header = Path.GetFileNameWithoutExtension(path),
            Description = path,
            Content = combo,
        };
    }

    private static readonly (AutoShowPolicy Value, string Label)[] PolicyChoices =
    [
        (AutoShowPolicy.Auto, "自動 — フォーカスが当たったら表示"),
        (AutoShowPolicy.Strict, "厳密 — 自分でフォーカスを当てたときのみ"),
        (AutoShowPolicy.Suppress, "抑制 — 手動でのみ表示"),
    ];

    // ------------------------------------------------------------------
    // 変更の反映
    // ------------------------------------------------------------------

    private void Wire()
    {
        ContentScroll.SizeChanged += (_, args) => ApplyContentWidth(args.NewSize.Width);

        Nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is not NavigationViewItem item) return;

            ShowSection(item.Tag as string ?? string.Empty, item.Content as string ?? string.Empty);
        };

        HeightSlider.ValueChanged += (_, _) =>
        {
            ShowHeight();
            if (_loading) return;

            _settings.HeightDip = HeightSlider.Value;
            _settings.Save();
            _keyboard.RefreshDock();
        };

        CoverTaskbarToggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            _keyboard.SetCoverTaskbar(CoverTaskbarToggle.IsOn);
        };

        AutoShowToggle.Toggled += (_, _) =>
        {
            if (_loading) return;

            _settings.AutoShow = AutoShowToggle.IsOn;
            _settings.Save();
            AutoShowChanged?.Invoke(this, AutoShowToggle.IsOn);
        };

        AutoStartToggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            AppSettings.SetAutoStart(AutoStartToggle.IsOn);
        };

        TraceToggle.Toggled += (_, _) =>
        {
            if (_loading) return;

            _settings.Trace = TraceToggle.IsOn;
            _settings.Save();

            // 記録の可否は即座に効かせる。不具合を追うのに再起動を要求しない。
            TraceLog.Enabled = TraceToggle.IsOn;
        };

        BackdropCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            if (BackdropCombo.SelectedItem is not ComboBoxItem { Tag: BackdropMaterial material }) return;

            _settings.Backdrop = material;
            _settings.Save();
            _keyboard.ApplyBackdrop();
        };

        MonitorCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            if (MonitorCombo.SelectedItem is not ComboBoxItem { Tag: string device }) return;

            _keyboard.SetMonitor(device.Length == 0 ? null : device);
        };

        LayoutCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            if (LayoutCombo.SelectedItem is not ComboBoxItem { Tag: string fileName }) return;

            _keyboard.SetLayout(fileName);
        };

        RepeatDelayBox.ValueChanged += (_, _) =>
        {
            if (_loading) return;

            _settings.RepeatDelayMs = ToNullableInt(RepeatDelayBox.Value);
            _settings.Save();
            Notify("リピートの設定は、キーボードを再起動すると反映されます。");
        };

        RepeatIntervalBox.ValueChanged += (_, _) =>
        {
            if (_loading) return;

            _settings.RepeatIntervalMs = ToNullableInt(RepeatIntervalBox.Value);
            _settings.Save();
            Notify("リピートの設定は、キーボードを再起動すると反映されます。");
        };

        OpenLogButton.Click += (_, _) => Open(TraceLog.Path);
        OpenFolderButton.Click += (_, _) => Open(AppSettings.DirectoryPath);
        OpenSettingsButton.Click += (_, _) => Open(AppSettings.DirectoryPath);

        // 開いている間に一覧が増えることがある。閉じる直前ではなく、
        // 表示に戻ってきたときに読み直す。
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;

            _loading = true;
            LoadRules();
            _loading = false;
        };
    }

    /// <summary>自動表示の切り替えを App へ伝える。監視の開始と停止のため。</summary>
    public event EventHandler<bool>? AutoShowChanged;

    private void ShowHeight() => HeightValue.Text = $"{HeightSlider.Value:0} DIP";

    private static int? ToNullableInt(double value) =>
        double.IsNaN(value) ? null : (int)Math.Round(value);

    private void Notify(string message) => StatusText.Text = message;

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 開けなくても設定画面は使える。
        }
    }
}
