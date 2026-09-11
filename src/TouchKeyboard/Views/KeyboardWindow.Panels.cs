using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TouchKeyboard.Settings;

namespace TouchKeyboard.Views;

/// <summary>
/// 端から引き出すパネル。左はクリップボードの履歴、右はテンキー。
///
/// 本体を置き換えず、重ねて出す。置き換えると出している間は文字が打てなくなる。
/// </summary>
public sealed partial class KeyboardWindow
{
    private enum PanelSide
    {
        None,
        Left,
        Right,
    }

    /// <summary>いま出している（または引き出し中の）パネル。</summary>
    private PanelSide _panelSide;

    private readonly TranslateTransform _panelSlide = new();
    private Storyboard? _panelAnimation;

    /// <summary>指で引き出している最中か。離すまで開くか閉じるか決めない。</summary>
    private bool _panelDragging;

    /// <summary>引き出し中のパネル。<see cref="_panelSide"/> に対応する実体。</summary>
    private FrameworkElement? _panelBorder;

    /// <summary>中身の左右の余白。XAML の Margin と揃えること。幅の計算に要る。</summary>
    private const double PanelInset = 8;

    /// <summary>ここまで引き出して離したら開く。パネル幅に対する割合。</summary>
    private const double PanelCommitRatio = 0.4;

    // ------------------------------------------------------------------
    // 引き出し
    // ------------------------------------------------------------------

    /// <summary>
    /// 引き出しを始める。指の動きに追従させるため、この時点で表示して端に寄せておく。
    /// </summary>
    private bool BeginPanelDrag(PanelSide side)
    {
        if (!PreparePanel(side)) return false;

        EnsurePanelClip();
        _panelAnimation?.Stop();

        _panelSide = side;
        _panelDragging = true;

        // 隠れている位置から始める。引き下ろした分だけ降りてくる。
        _panelSlide.Y = HiddenOffset(side);
        PanelScrim.Opacity = 0;
        PanelHost.Visibility = Visibility.Visible;

        return true;
    }

    /// <summary>
    /// パネルが隠れている位置から出きるまでの距離。上から降ろすので高さで測る。
    /// </summary>
    private double PanelExtent => Math.Max(1, Body.ActualHeight);

    private bool _panelClipWired;

    /// <summary>
    /// パネルを自分の行の中だけに描く。
    ///
    /// 引き出しの途中は上へ逃がした位置から降りてくる。Grid は子をはみ出させるので、
    /// そのままだとタイトルバーの帯の上に重なって見える。
    /// </summary>
    private void EnsurePanelClip()
    {
        if (!_panelClipWired)
        {
            _panelClipWired = true;
            PanelHost.SizeChanged += (_, e) => ApplyPanelClip(e.NewSize.Width, e.NewSize.Height);
        }

        ApplyPanelClip(PanelHost.ActualWidth, PanelHost.ActualHeight);
    }

    private void ApplyPanelClip(double width, double height)
    {
        if (width <= 0 || height <= 0) return;

        PanelHost.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, width, height),
        };
    }

    /// <summary>
    /// 引き出し量を反映する。戻す動きにもそのまま追従する。
    /// </summary>
    /// <param name="pulled">上端から引き下ろした距離。負なら押し戻している。</param>
    private void UpdatePanelDrag(double pulled)
    {
        if (!_panelDragging || _panelBorder is null) return;

        var extent = PanelExtent;
        var shown = Math.Clamp(pulled, 0, extent);

        _panelSlide.Y = HiddenOffset(_panelSide) * (1 - (shown / extent));
        PanelScrim.Opacity = shown / extent;
    }

    /// <summary>
    /// 指を離した。引き出した量で開くか閉じるかを決め、残りを動きで埋める。
    /// </summary>
    private void EndPanelDrag()
    {
        if (!_panelDragging || _panelBorder is null) return;

        _panelDragging = false;

        var extent = PanelExtent;
        var shown = extent - Math.Abs(_panelSlide.Y);

        // ほとんど動いていないならタップ。動きを見せずに畳む。
        // 一瞬だけ覆いが差し込むのは、触っただけの操作に対して大げさに映る。
        if (shown < 2)
        {
            PanelHost.Visibility = Visibility.Collapsed;
            _panelBorder.Visibility = Visibility.Collapsed;
            _panelSide = PanelSide.None;

            RefreshCommandStates();
            return;
        }

        if (shown >= extent * PanelCommitRatio) OpenPanel();
        else ClosePanel();
    }

    /// <summary>引き出しを取り消す。指の追従が途切れた場合など。</summary>
    private void CancelPanelDrag()
    {
        if (!_panelDragging) return;

        _panelDragging = false;
        ClosePanel();
    }

    // ------------------------------------------------------------------
    // 開閉
    // ------------------------------------------------------------------

    /// <summary>指の動きを介さずに開く。メニューから呼ぶ。</summary>
    private void ShowPanel(PanelSide side)
    {
        if (!BeginPanelDrag(side)) return;

        _panelDragging = false;
        OpenPanel();
    }

    /// <summary>
    /// 開いていなければ開き、開いていれば閉じる。
    /// ボタンが入／切で状態を示す以上、押して戻せないと辻褄が合わない。
    /// </summary>
    private void TogglePanel(PanelSide side)
    {
        if (PanelHost.Visibility == Visibility.Visible && _panelSide == side)
        {
            ClosePanel();
            return;
        }

        ShowPanel(side);
    }

    private void OpenPanel()
    {
        Slide(0, 1, 180, new CubicEase { EasingMode = EasingMode.EaseOut }, onDone: null);
        RefreshCommandStates();
    }

    private void ClosePanel()
    {
        if (PanelHost.Visibility != Visibility.Visible) return;

        Slide(HiddenOffset(_panelSide), 0, 150, new CubicEase { EasingMode = EasingMode.EaseIn },
            onDone: () =>
            {
                PanelHost.Visibility = Visibility.Collapsed;
                if (_panelBorder is not null) _panelBorder.Visibility = Visibility.Collapsed;
                _panelSide = PanelSide.None;

                RefreshCommandStates();
            });
    }

    /// <summary>
    /// 動きを付けずに畳む。隠れている間に整えるときに使う。
    /// 見えていないところで滑らせても意味が無く、出した直後に動きが残る。
    /// </summary>
    private void ResetPanels()
    {
        _panelAnimation?.Stop();
        _panelDragging = false;

        PanelHost.Visibility = Visibility.Collapsed;
        if (_panelBorder is not null) _panelBorder.Visibility = Visibility.Collapsed;

        _panelSide = PanelSide.None;
        _panelSlide.Y = 0;
        PanelScrim.Opacity = 0;

        RefreshCommandStates();
    }

    /// <summary>隠れている位置。上へ逃がす。左右どちらのパネルも降りてくる。</summary>
    private double HiddenOffset(PanelSide side) => side == PanelSide.None ? 0 : -PanelExtent;

    /// <summary>
    /// パネルを滑らせ、覆いの濃さを合わせて変える。
    ///
    /// 位置と不透明度はどちらも合成側で動くため、キー入力の処理を妨げない。
    /// レイアウトを再計算する種類の動きにすると、打鍵が引っかかる。
    /// </summary>
    private void Slide(double toY, double toOpacity, int durationMs, EasingFunctionBase easing, Action? onDone)
    {
        // 前の動きが残っていると値が飛ぶ。必ず止めてから始める。
        _panelAnimation?.Stop();

        var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));

        var slide = new DoubleAnimation
        {
            From = _panelSlide.Y,
            To = toY,
            Duration = duration,
            EasingFunction = easing,
        };

        Storyboard.SetTarget(slide, _panelSlide);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation
        {
            From = PanelScrim.Opacity,
            To = toOpacity,
            Duration = duration,
        };

        Storyboard.SetTarget(fade, PanelScrim);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);

        storyboard.Completed += (_, _) =>
        {
            // 止められた場合は途中の値が残る。終わりに確定させる。
            _panelSlide.Y = toY;
            PanelScrim.Opacity = toOpacity;
            onDone?.Invoke();
        };

        _panelAnimation = storyboard;
        storyboard.Begin();
    }

    // ------------------------------------------------------------------
    // 中身の用意
    // ------------------------------------------------------------------

    /// <summary>出す側のパネルを組み立て、幅を決める。用意できなければ false。</summary>
    private bool PreparePanel(PanelSide side)
    {
        NumpadPanel.Visibility = Visibility.Collapsed;
        ClipboardPanel.Visibility = Visibility.Collapsed;

        _panelBorder = side == PanelSide.Right ? NumpadPanel : ClipboardPanel;

        if (side == PanelSide.Right && !PrepareNumpad()) return false;
        if (side == PanelSide.Left) PrepareClipboard();

        _panelBorder.RenderTransform = _panelSlide;
        _panelBorder.Visibility = Visibility.Visible;

        NumpadEdgeLine.BorderBrush = _theme.KeyBorder;
        ClipboardEdgeLine.BorderBrush = _theme.KeyBorder;

        ApplyPanelBackdrop();

        return true;
    }

    /// <summary>
    /// パネルに素材を敷く。ウィンドウと同じものを選ぶ。
    ///
    /// <see cref="SystemBackdropElement"/> はウィンドウの一部にバックドロップを
    /// 掛けられる。塗りで似せる必要はなく、Mica も本物が使える。
    ///
    /// 既製の <c>MicaBackdrop</c> は渡さない。あちらは非アクティブなウィンドウで
    /// 素材を落とすため、このアプリではパネルが透けてしまう。
    /// </summary>
    private void ApplyPanelBackdrop()
    {
        NumpadBackdrop.SystemBackdrop = new ActiveBackdrop(_settings.Backdrop, _theme.IsDark);
        ClipboardBackdrop.SystemBackdrop = new ActiveBackdrop(_settings.Backdrop, _theme.IsDark);
    }

    private bool PrepareNumpad()
    {
        if (_numpadLayout is null && !LoadNumpad()) return false;

        // キーの大きさを本体と揃える。本体は rowUnits で全幅を割っているので、
        // 同じ 1 ユニットあたりの幅を使えば見た目が揃う。
        var unit = KeyRoot.ActualWidth / (_layout?.RowUnits ?? 15.5);
        NumpadPanel.Width = (unit * _numpadLayout!.RowUnits) + (PanelInset * 2);

        RefreshAllKeys();
        return true;
    }

}
