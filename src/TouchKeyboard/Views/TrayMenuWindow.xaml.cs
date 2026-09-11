using System;
using System.Collections.Generic;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TouchKeyboard.Diagnostics;
using TouchKeyboard.Interop;
using TouchKeyboard.Layout;
using TouchKeyboard.Settings;
using Windows.Graphics;
using WinRT.Interop;

namespace TouchKeyboard.Views;

/// <summary>
/// 通知領域のメニュー。
///
/// Win32 の <c>TrackPopupMenu</c> は既定のテーマで描かれ、明暗の切り替えにも
/// 素材にも追従しない。<see cref="MenuFlyout"/> で組み直す。
///
/// フライアウトは XamlRoot に属するため、置き場所のウィンドウが要る。
/// ここでは 1×1 の器だけを通知領域の座標へ置き、そこを起点に開く。
/// フライアウト自体は <c>ShouldConstrainToRootBounds</c> を false にして
/// ウィンドウの外へ広げる。
///
/// このウィンドウはフォーカスを受け取ってよい。
/// キーボード本体（絶対制約 2）とは役割が違う。
/// </summary>
public sealed partial class TrayMenuWindow : Window
{
    private readonly AppSettings _settings;
    private readonly nint _hwnd;

    /// <summary>使い回す。開くたびに作ると前のものが外れずに残る。</summary>
    private readonly MenuFlyout _flyout = new()
    {
        // 器は 1×1 しかない。内側に収める設定のままだと何も出ない。
        ShouldConstrainToRootBounds = false,
    };

    private bool _shown;

    /// <summary>器が視覚の木に入ったか。入るまでフライアウトは開けない。</summary>
    private bool _anchorReady;

    /// <summary>組み上がるのを待っている状態か。</summary>
    private bool _openPending;

    /// <summary>開くときに決めたテーマ。面へ入れるまで持っておく。</summary>
    private ElementTheme _theme = ElementTheme.Default;

    public TrayMenuWindow(AppSettings settings)
    {
        _settings = settings;

        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        // 一覧にも Alt+Tab にも出さない。メニューは居座るものではない。
        AppWindow.IsShownInSwitchers = false;

        // 器そのものは見せない。1×1 を指定してもシステムが下限まで広げるため、
        // そのままだとメニューの脇に四角が出る。
        // 別ウィンドウとして開くフライアウトはこの指定の影響を受けない。
        WindowStyles.MakeInvisible(_hwnd);

        // 中身が組み上がるまで開けない。初回は Show の直後だとまだ間に合わない。
        Anchor.Loaded += (_, _) =>
        {
            _anchorReady = true;
            if (_openPending) Open();
        };

        // 閉じたら器も畳む。残すと器が前面に居座る。
        //
        // 直接畳まない。閉じる処理の途中でウィンドウを隠すと、
        // その結果さらに閉じる通知が来て往復する。次の番に回す。
        _flyout.Closed += (_, _) => DispatcherQueue.TryEnqueue(Hide);

        // 面は開いた時点で作られる。それまで辿れない。
        _flyout.Opened += (_, _) => ApplyThemeToPresenter();

        AppWindow.Hide();
    }

    /// <summary>
    /// 指定の画面座標にメニューを出す。
    /// </summary>
    /// <param name="x">シェルが通知してきた画面座標。物理ピクセル。</param>
    /// <param name="y">同上。</param>
    public void ShowAt(int x, int y, IReadOnlyList<TrayMenuItem> items)
    {
        // アプリ用ではなくシステム用の設定に従う。
        // 通知領域から出るものなので、タスクバーと揃っている方が自然になる。
        // 開くたびに読み直すので、Windows 側で切り替えたらそのまま追従する。
        _theme = Theme.SystemIsDark ? ElementTheme.Dark : ElementTheme.Light;

        if (Content is FrameworkElement root) root.RequestedTheme = _theme;

        Fill(items, _theme);

        // 器は点で足りる。大きさを持たせると、その分が画面に見えてしまう。
        AppWindow.ResizeClient(new SizeInt32(1, 1));
        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Show();

        _shown = true;

        // 前面に出さないと、フライアウトが入力を受け取れない。
        ShellNotify.SetForegroundWindow(_hwnd);

        // 初回は Show の直後だと中身がまだ組み上がっておらず、
        // XamlRoot も配置も決まっていない。組み上がってから開く。
        if (!_anchorReady)
        {
            _openPending = true;
            return;
        }

        DispatcherQueue.TryEnqueue(Open);
    }

    private void Open()
    {
        _openPending = false;

        if (!_shown) return;

        // 器が木に入っていなければ開けない。開くと配置の計算が決まらない。
        if (Anchor.XamlRoot is null)
        {
            Hide();
            return;
        }

        try
        {
            // 通知領域は画面の右下にあることが多い。起点より上、右揃えで開く。
            // 収まらない側は WinUI が自分で折り返す。
            _flyout.ShowAt(Anchor, new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
            });
        }
        catch (Exception ex)
        {
            // 開けなかった。器だけが前面に残るのを避ける。
            TraceLog.Write($"通知領域のメニューを開けませんでした: {ex.Message}");
            Hide();
        }
    }

    /// <summary>
    /// 開いたときに面へテーマを入れる。
    ///
    /// フライアウトはウィンドウの中身とは別の根に載るため、
    /// 器の <c>RequestedTheme</c> は届かない。
    ///
    /// <c>MenuFlyoutPresenterStyle</c> に <c>Style</c> を渡す方法は採らない。
    /// 既定の <c>Style</c> を丸ごと置き換えることになり、
    /// テンプレートまで失われて面が白いままになる。
    /// 開いたあとに面そのものを辿って指定する。
    /// </summary>
    private void ApplyThemeToPresenter()
    {
        if (_flyout.Items.Count == 0) return;
        if (_flyout.Items[0] is not FrameworkElement first) return;

        DependencyObject? node = first;

        while (node is not null and not MenuFlyoutPresenter)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        if (node is MenuFlyoutPresenter presenter) presenter.RequestedTheme = _theme;
    }

    private void Fill(IReadOnlyList<TrayMenuItem> items, ElementTheme theme)
    {
        _flyout.Items.Clear();

        foreach (var item in items)
        {
            var element = ToFlyoutItem(item);

            // 面と同じ指定を項目にも入れる。
            // 面から受け継がれるはずだが、受け継がれなかった場合に地の色だけが
            // 切り替わって字が読めなくなる。
            if (element is FrameworkElement framework) framework.RequestedTheme = theme;

            _flyout.Items.Add(element);
        }
    }

    private static MenuFlyoutItemBase ToFlyoutItem(TrayMenuItem item)
    {
        if (item.IsSeparator) return new MenuFlyoutSeparator();

        if (item.IsCheckable)
        {
            var toggle = new ToggleMenuFlyoutItem
            {
                Text = item.Text,
                IsChecked = item.IsChecked,
                IsEnabled = item.IsEnabled,
            };

            toggle.Click += (_, _) => item.Invoke?.Invoke();
            return toggle;
        }

        var entry = new MenuFlyoutItem
        {
            Text = item.Text,
            IsEnabled = item.IsEnabled,
        };

        if (KeyDefinition.ResolveIcon(item.Icon) is { } glyph)
        {
            entry.Icon = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            };
        }

        entry.Click += (_, _) => item.Invoke?.Invoke();
        return entry;
    }

    public void Hide()
    {
        if (!_shown) return;

        _shown = false;
        AppWindow.Hide();
    }
}
