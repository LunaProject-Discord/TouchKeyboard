using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TouchKeyboard.Layout;

namespace TouchKeyboard.Views;

/// <summary>
/// グリップを短く押したときのメニュー。
///
/// ドッキング中はヘッダーを畳んでおり、画面上に操作の入口がグリップしか無い。
/// 高さ変更とスワイプでの非表示は指の動きに割り当てているので、
/// 押しただけの操作をここに割り当てる。
/// </summary>
public sealed partial class KeyboardWindow
{
private bool _menuWired;

    /// <summary>
    /// グリップの帯そのものの動き。<see cref="KeyboardWindow.Panels.cs"/> の
    /// パネルの引き出しと同じ作法（TranslateTransform + Opacity の Storyboard）。
    /// </summary>
    private readonly TranslateTransform _menuSlide = new();

    private Storyboard? _menuAnimation;

    /// <summary>隠れている位置。少し上に逃がしておき、降りてきながら現れる動きにする。</summary>
    private const double MenuHiddenOffsetY = -10;

    private void ToggleMenu()
    {
        if (MenuHost.Visibility == Visibility.Visible)
        {
            CloseMenu();
            return;
        }

        WireMenu();

        MenuBackdrop.SystemBackdrop = new ActiveBackdrop(_settings.Backdrop, _theme.IsDark);
        MenuEdgeLine.BorderBrush = _theme.KeyBorder;

        // いまの状態で意味が変わる。押す前に何が起きるか分かるようにする。
        RefreshCommandStates();

        // 前の動きが残っていると値が飛ぶ。止めてから隠れている状態に戻す。
        _menuAnimation?.Stop();
        _menuSlide.Y = MenuHiddenOffsetY;
        MenuCard.Opacity = 0;

        MenuHost.Visibility = Visibility.Visible;

        AnimateMenu(
            toY: 0, toOpacity: 1, durationMs: 180,
            easing: new CubicEase { EasingMode = EasingMode.EaseOut }, onDone: null);
    }

    private void CloseMenu()
    {
        if (MenuHost.Visibility != Visibility.Visible) return;

        AnimateMenu(
            toY: MenuHiddenOffsetY, toOpacity: 0, durationMs: 150,
            easing: new CubicEase { EasingMode = EasingMode.EaseIn },
            onDone: () => MenuHost.Visibility = Visibility.Collapsed);
    }

    /// <summary>
    /// 動きを付けずに畳む。<see cref="ResetToInitialView"/> から呼ぶ。
    ///
    /// 開いたまま隠れて、そのあと表示し直したときに残っていないようにする。
    /// 見えていないところで滑らせても意味が無く、出した直後に動きが残る
    /// （パネルの <c>ResetPanels</c> と同じ考え方）。
    /// </summary>
    private void ResetMenu()
    {
        _menuAnimation?.Stop();

        MenuHost.Visibility = Visibility.Collapsed;
        _menuSlide.Y = MenuHiddenOffsetY;
        MenuCard.Opacity = 0;
    }

    /// <summary>
    /// メニューの帯を滑らせながら不透明度を合わせて変える。
    /// パネルの <c>Slide</c>（KeyboardWindow.Panels.cs）と同じ組み方。
    /// </summary>
    private void AnimateMenu(double toY, double toOpacity, int durationMs, EasingFunctionBase easing, Action? onDone)
    {
        _menuAnimation?.Stop();

        var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));

        var slide = new DoubleAnimation
        {
            From = _menuSlide.Y,
            To = toY,
            Duration = duration,
            EasingFunction = easing,
        };

        Storyboard.SetTarget(slide, _menuSlide);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation
        {
            From = MenuCard.Opacity,
            To = toOpacity,
            Duration = duration,
            EasingFunction = easing,
        };

        Storyboard.SetTarget(fade, MenuCard);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);

        storyboard.Completed += (_, _) =>
        {
            // 途中で止められた場合は値が半端で残る。終わりに確定させる。
            _menuSlide.Y = toY;
            MenuCard.Opacity = toOpacity;
            onDone?.Invoke();
        };

        _menuAnimation = storyboard;
        storyboard.Begin();
    }

    /// <summary>
    /// ボタンの入／切をいまの状態に合わせる。
    ///
    /// 押した結果は WinUI が勝手に反転させる。押しただけで状態が変わったように
    /// 見えると、実際には開かなかった場合に食い違う。実物の状態から入れ直す。
    /// </summary>
    private void RefreshCommandStates()
    {
        var panelOpen = PanelHost.Visibility == Visibility.Visible;

        var clipboard = panelOpen && _panelSide == PanelSide.Left;
        var numpad = panelOpen && _panelSide == PanelSide.Right;

        TitleClipboard.IsChecked = clipboard;
        MenuClipboard.IsChecked = clipboard;

        TitleNumpad.IsChecked = numpad;
        MenuNumpad.IsChecked = numpad;

        // ドッキングの切り替えは押すたびに意味が変わる。文言で示す。
        var dock = IsFloating ? "画面端に戻す" : "フローティング";

        TitleDock.Content = dock;
        MenuDockText.Text = dock;
    }

    // ------------------------------------------------------------------
    // 設定のフライアウト
    // ------------------------------------------------------------------

    private MenuFlyout? _settingsMenu;

    /// <summary>
    /// 自分のメニューを開いた／閉じた。
    ///
    /// 別ウィンドウとして開くため、相手のアプリはその間フォーカスを失う。
    /// 自動表示の監視はこれを受けて、隠す判断を止める。
    /// </summary>
    public event EventHandler<bool>? MenuActiveChanged;

    /// <summary>グリップのメニューから開いたか。閉じたときに親も畳むかの判断に使う。</summary>
    private bool _closeMenuWithSettings;

    /// <summary>
    /// 設定のフライアウト。
    ///
    /// タイトルバーの設定ボタンと、グリップのメニューの設定ボタンで共用する。
    /// 中身が同じものを 2 つ作ると、片方だけ直す取りこぼしが起きる。
    ///
    /// よく触る配列の選択だけを出す。替えた結果はその場で見えるので、
    /// 設定画面まで行き来せずに選べる。残りは設定画面へ送る。
    /// </summary>
    private MenuFlyout SettingsMenu()
    {
        if (_settingsMenu is not null) return _settingsMenu;

        var menu = new MenuFlyout
        {
            // ウィンドウの内側に収めない。
            // キーボードは背が低く、下に足りないと判断されて上へ折り返る。
            ShouldConstrainToRootBounds = false,
        };

        // 配列は入れ子にする。並びに直接置くと、増やしたときに
        // 「すべての設定」と同じ高さで混ざり、何を選ぶ場所か分からなくなる。
        var layouts = new MenuFlyoutSubItem
        {
            Text = "キー配列",
            Icon = new FontIcon
            {
                Glyph = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            },
        };

        foreach (var (fileName, displayName) in LayoutLoader.Available)
        {
            var choice = new RadioMenuFlyoutItem
            {
                Text = displayName,

                // 同じ組にすると、選んだものだけに印が付く。
                GroupName = "layout",
                Tag = fileName,
            };

            choice.Click += (_, _) => SetLayout(fileName);
            layouts.Items.Add(choice);
        }

        menu.Items.Add(layouts);
        menu.Items.Add(new MenuFlyoutSeparator());

        var all = new MenuFlyoutItem
        {
            Text = "すべての設定",
            Icon = new FontIcon
            {
                Glyph = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            },
        };

        all.Click += (_, _) => SettingsRequested?.Invoke(this, System.EventArgs.Empty);
        menu.Items.Add(all);

        // 開くたびに合わせ直す。設定画面から替えられることもある。
        menu.Opening += (_, _) => SyncLayoutChoice(menu);

        menu.Opened += (_, _) =>
        {
            TitleSettings.IsChecked = true;
            MenuSettings.IsChecked = true;

            MenuActiveChanged?.Invoke(this, true);
        };

        menu.Closed += (_, _) =>
        {
            TitleSettings.IsChecked = false;
            MenuSettings.IsChecked = false;

            MenuActiveChanged?.Invoke(this, false);

            if (!_closeMenuWithSettings) return;

            _closeMenuWithSettings = false;
            CloseMenu();
        };

        _settingsMenu = menu;
        return menu;
    }

    private void SyncLayoutChoice(MenuFlyout menu)
    {
        foreach (var item in menu.Items)
        {
            if (item is not MenuFlyoutSubItem group) continue;

            foreach (var entry in group.Items)
            {
                if (entry is not RadioMenuFlyoutItem { Tag: string fileName } choice) continue;

                choice.IsChecked = string.Equals(
                    fileName, _settings.LayoutFile, System.StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private void WireMenu()
    {
        if (_menuWired) return;
        _menuWired = true;

        MenuCard.RenderTransform = _menuSlide;

        MenuScrim.PointerPressed += (_, e) =>
        {
            CloseMenu();
            e.Handled = true;
        };

        MenuClipboard.Click += (_, _) =>
        {
            CloseMenu();
            TogglePanel(PanelSide.Left);
        };

        MenuNumpad.Click += (_, _) =>
        {
            CloseMenu();
            TogglePanel(PanelSide.Right);
        };

        // ここでは畳まない。畳むとフライアウトを出す先が消える。
        // 閉じるのはフライアウトが閉じたあと。
        MenuSettings.Click += (_, _) =>
        {
            _closeMenuWithSettings = true;

            // ハンドルからのメニューは真下に中央で開く。
            //
            // ウィンドウの内側に収める設定のままだと、下に足りないと判断されて
            // 上へ折り返る。メニュー自体が上端近くにあるため、その判断になりやすい。
            SettingsMenu().ShowAt(MenuSettings, new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.Bottom,
            });
        };

        MenuDock.Click += (_, _) =>
        {
            CloseMenu();
            SetFloating(!IsFloating);
        };

        MenuClose.Click += (_, _) =>
        {
            CloseMenu();
            HideKeyboard();
        };
    }

}
