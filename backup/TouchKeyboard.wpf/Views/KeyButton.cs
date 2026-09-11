using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TouchKeyboard.Input;
using TouchKeyboard.Layout;

namespace TouchKeyboard.Views;

/// <summary>
/// キー 1 つ分の表示。描画とタップの受付のみを担い、入力ロジックを持たない。
/// </summary>
public sealed class KeyButton : Border
{
    private const double Gap = 3;
    private const double Radius = 6;

    private readonly TextBlock _text;

    /// <summary>通常時とラッチ時の枠の太さ。結合する辺は 0 にしてある。</summary>
    private readonly Thickness _normalBorder;
    private readonly Thickness _latchedBorder;

    /// <summary>Shift 時の文字を左上に小さく併記する。英字キーには付けない。</summary>
    private readonly TextBlock? _shiftHint;

    private Theme _theme;
    private bool _isPressed;

    public KeyDefinition Definition { get; }

    public KeyButton(KeyDefinition definition, Theme theme)
    {
        Definition = definition;
        _theme = theme;

        // 上下の行と繋げるキーは、繋ぐ側の余白と角丸を落として 1 つのキーに見せる。
        Margin = new Thickness(
            Gap,
            definition.MergeUp ? 0 : Gap,
            Gap,
            definition.MergeDown ? 0 : Gap);

        CornerRadius = new CornerRadius(
            topLeft: definition.MergeUp ? 0 : Radius,
            topRight: definition.MergeUp ? 0 : Radius,
            bottomRight: definition.MergeDown ? 0 : Radius,
            bottomLeft: definition.MergeDown ? 0 : Radius);

        // 上下の行と繋げる辺には枠を引かない。引くと結合部に継ぎ目の線が出る。
        _normalBorder = new Thickness(
            1,
            definition.MergeUp ? 0 : 1,
            1,
            definition.MergeDown ? 0 : 1);

        _latchedBorder = new Thickness(
            2,
            definition.MergeUp ? 0 : 2,
            2,
            definition.MergeDown ? 0 : 2);

        BorderThickness = _normalBorder;

        _text = new TextBlock
        {
            Text = definition.Label,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        var content = new Grid { IsHitTestVisible = false };
        content.Children.Add(_text);

        if (definition.ShowsShiftHint)
        {
            _shiftHint = new TextBlock
            {
                Text = definition.ShiftLabel,
                FontSize = 11,
                Opacity = 0.65,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(9, 5, 0, 0),
                IsHitTestVisible = false,
            };
            content.Children.Add(_shiftHint);
        }

        Child = content;

        // プレス＆ホールド（右クリック相当）とフリックは遅延と誤爆の原因になるため無効化する。
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);

        if (definition.Spacer)
        {
            Background = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            IsHitTestVisible = false;
        }

        ApplyTheme(theme);
    }

    /// <summary>Segoe Fluent Icons。BackSpace や矢印など記号で表すキーに使う。</summary>
    private static readonly FontFamily IconFont =
        new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static readonly FontFamily TextFont =
        new("Segoe UI Variable Text, Segoe UI, Yu Gothic UI");

    private bool _showingIcon;

    /// <summary>
    /// ラベルの文字数に応じて字面を詰める。「無変換」のような 3 文字キーが欠けないようにする。
    /// アイコンは字面が 1 文字ぶんなので固定サイズでよい。
    /// </summary>
    private void UpdateFontSize()
    {
        if (_showingIcon)
        {
            _text.FontSize = 16;
            return;
        }

        _text.FontSize = _text.Text.Length switch
        {
            <= 1 => 17,
            2 => 14,
            3 => 12,
            _ => 11,
        };
    }

    public void ApplyTheme(Theme theme)
    {
        _theme = theme;
        if (Definition.Spacer) return;

        BorderBrush = theme.KeyBorder;
        Refresh(LatchState.Off, string.Empty);
    }

    /// <summary>
    /// 修飾キーのラッチ状態と、Fn / Shift を考慮したラベルを反映する。
    /// </summary>
    /// <param name="latch">このキーが修飾キーの場合のラッチ状態。それ以外は Off を渡す。</param>
    /// <param name="label">表示するラベル。空文字なら定義のラベルを使う。</param>
    /// <param name="icon">アイコン文字。null ならラベルを描く。</param>
    public void Refresh(LatchState latch, string label, string? icon = null)
    {
        if (Definition.Spacer) return;

        _showingIcon = icon is not null;
        _text.FontFamily = _showingIcon ? IconFont : TextFont;
        _text.Text = _showingIcon
            ? icon!
            : (string.IsNullOrEmpty(label) ? Definition.Label : label);

        UpdateFontSize();

        // Shift が効いているときは主ラベルが Shift 文字に入れ替わるので、左上の併記は消す。
        if (_shiftHint is not null)
        {
            _shiftHint.Visibility = _text.Text == Definition.ShiftLabel
                ? Visibility.Collapsed
                : Visibility.Visible;
            _shiftHint.Foreground = _theme.KeyForeground;
        }

        if (_isPressed)
        {
            Background = _theme.KeyPressed;
            _text.Foreground = Brushes.White;
            if (_shiftHint is not null) _shiftHint.Foreground = Brushes.White;
            return;
        }

        // ラッチとロックを視覚的に区別する（要件 F-2）。
        // ラッチ = 淡い着色 + アクセント枠、ロック = アクセント塗りつぶし。
        switch (latch)
        {
            case LatchState.Latched:
                Background = _theme.ModifierLatched;
                BorderBrush = _theme.ModifierLocked;
                BorderThickness = _latchedBorder;
                _text.Foreground = _theme.KeyForeground;
                break;

            case LatchState.Locked:
                Background = _theme.ModifierLocked;
                BorderBrush = _theme.ModifierLocked;
                BorderThickness = _latchedBorder;
                _text.Foreground = Brushes.White;
                break;

            default:
                Background = _theme.KeyBackground;
                BorderBrush = _theme.KeyBorder;
                BorderThickness = _normalBorder;
                _text.Foreground = _theme.KeyForeground;
                break;
        }
    }

    /// <summary>押下中の見た目に切り替える。</summary>
    public void SetPressed(bool pressed, LatchState latch, string label, string? icon = null)
    {
        _isPressed = pressed;
        Refresh(latch, label, icon);
    }
}
