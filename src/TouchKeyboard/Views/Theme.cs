using System;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
using TouchKeyboard.Interop;
using TouchKeyboard.Layout;

namespace TouchKeyboard.Views;

/// <summary>
/// 標準タッチキーボードに寄せた配色。
/// OS のライト／ダーク設定は WinUI の ElementTheme に任せ、ここでは
/// キーの塗りなど自前で描く部分の色だけを持つ。
/// </summary>
public sealed record Theme(
    bool IsDark,
    Brush KeyBackground,
    Brush ModifierBackground,
    Brush KeyBorder,
    Brush KeyForeground,
    Brush KeyPressed,
    Brush ModifierLatched,
    Brush ModifierLocked,
    Brush SecondaryForeground,
    Brush PanelBackground)
{
    /// <summary>
    /// 消えている灯り。
    ///
    /// 実物の LED と同じく、光っていないときも位置が分かるようにする。
    /// 掛かった瞬間に丸が現れるより、色が変わるほうが状態を読み取りやすい。
    /// </summary>
    public Brush LedOff { get; } = new SolidColorBrush(Colors.White);

    /// <summary>
    /// キーの塗りを決める。
    ///
    /// <see cref="KeyDefinition.Tone"/> が名前なら対応する色、
    /// "#RRGGBB" 形式ならその色。省略時は修飾キーだけ別の色にする。
    /// 修飾キーは押しても文字が出ないので、見分けが付く方がよい。
    /// </summary>
    public Brush BackgroundFor(KeyDefinition key)
    {
        if (key.Tone is not { Length: > 0 } tone)
        {
            return key.IsModifier ? ModifierBackground : KeyBackground;
        }

        if (tone[0] == '#') return Parse(tone) ?? KeyBackground;

        return tone.ToLowerInvariant() switch
        {
            "modifier" => ModifierBackground,
            "normal" => KeyBackground,

            // 綴りの誤りは黙って通常色にする。配列の編集で起きうるもので、
            // ここで落とすとキーボード全体が出なくなる。
            _ => KeyBackground,
        };
    }

    /// <summary>"#RRGGBB" / "#AARRGGBB" を読む。読めなければ null。</summary>
    private static Brush? Parse(string text)
    {
        var body = text.AsSpan(1);

        if (body.Length is not (6 or 8)) return null;
        if (!uint.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var alpha = body.Length == 8 ? (byte)(value >> 24) : (byte)0xFF;

        return Solid((byte)(value >> 16), (byte)(value >> 8), (byte)value, alpha);
    }

    private static Brush Solid(byte r, byte g, byte b, byte a = 0xFF) =>
        new SolidColorBrush(Color.FromArgb(a, r, g, b));

    /// <summary>
    /// Fluent のコントロール枠。上辺が明るく下辺が暗いグラデーションで、
    /// 影を落とさずにキーの立体感を出す。
    /// </summary>
    private static Brush EdgeBorder(Color top, Color bottom) =>
        new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = top, Offset = 0 },
                new GradientStop { Color = bottom, Offset = 1 },
            },
        };

    private static Color Rgba(byte r, byte g, byte b, byte a) => Color.FromArgb(a, r, g, b);

    /// <summary>
    /// Windows のアクセントカラー。
    ///
    /// 素の色そのままは使わない。Windows 自身と同じく、暗い面には明るい段、
    /// 明るい面には暗い段を選ぶ。素の色は暗い背景では沈み、明るい背景では飛ぶ。
    /// </summary>
    private static Brush Accent(bool dark)
    {
        try
        {
            var color = new UISettings().GetColorValue(
                dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);

            return new SolidColorBrush(color);
        }
        catch (Exception)
        {
            // 取得できない環境では既定の青にする。灯りが消えるよりはよい。
            return Solid(0x00, 0x5F, 0xB8);
        }
    }

    /// <summary>いま使っているアクセントの色。設定変更を拾うために比べる。</summary>
    public Color AccentColor =>
        ModifierLocked is SolidColorBrush brush ? brush.Color : Microsoft.UI.Colors.Transparent;

    public static Theme Dark => new(
        IsDark: true,
        KeyBackground: Solid(0x3B, 0x3B, 0x3B),

        // 通常キーよりわずかに沈ませる。強い差を付けると盤面がまだらに見える。
        ModifierBackground: Solid(0x2E, 0x2E, 0x2E),
        KeyBorder: EdgeBorder(Rgba(0xFF, 0xFF, 0xFF, 0x1A), Rgba(0xFF, 0xFF, 0xFF, 0x12)),
        KeyForeground: Solid(0xFF, 0xFF, 0xFF),
        KeyPressed: Solid(0x00, 0x5F, 0xB8),
        ModifierLatched: Solid(0x2C, 0x4A, 0x66),
        ModifierLocked: Accent(dark: true),
        SecondaryForeground: Solid(0x9E, 0x9E, 0x9E),

        // 実物の Mica が落ち着く色に合わせた素の塗り。低電力モード中など
        // 素材を適用しないときに Shell の背景として使う。
        PanelBackground: Solid(0x20, 0x20, 0x20));

    public static Theme Light => new(
        IsDark: false,
        KeyBackground: Solid(0xFF, 0xFF, 0xFF),
        ModifierBackground: Solid(0xEE, 0xEE, 0xEE),
        KeyBorder: EdgeBorder(Rgba(0x00, 0x00, 0x00, 0x18), Rgba(0x00, 0x00, 0x00, 0x40)),
        KeyForeground: Solid(0x1B, 0x1B, 0x1B),
        KeyPressed: Solid(0x00, 0x5F, 0xB8),
        ModifierLatched: Solid(0xCC, 0xE0, 0xF5),
        ModifierLocked: Accent(dark: false),
        SecondaryForeground: Solid(0x60, 0x60, 0x60),
        PanelBackground: Solid(0xF3, 0xF3, 0xF3));

    /// <summary>
    /// OS のアプリ用テーマ設定を読む。読めない場合はダークを既定とする。
    /// </summary>
    public static Theme FromSystem() => ReadLightTheme("AppsUseLightTheme") ? Light : Dark;

    /// <summary>
    /// OS の Windows 用テーマ設定を読む。読めない場合はダークを既定とする。
    ///
    /// Windows はアプリ用とシステム用を別々に設定できる。
    /// タスクバーや通知領域まわりに出すものは、こちらに合わせる。
    /// </summary>
    public static bool SystemIsDark => !ReadLightTheme("SystemUsesLightTheme");

    private static bool ReadLightTheme(string valueName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            // 1 ならライト、0 ならダーク。
            if (key?.GetValue(valueName) is int value) return value != 0;
        }
        catch (Exception)
        {
            // レジストリが読めない環境でも動作を止めない。
        }

        return false;
    }

    /// <summary>
    /// 角を丸めるかどうかを指定する。
    /// 画面端へドッキングしているときに丸めると、角の外側に下の画面が覗いて不自然になる。
    /// </summary>
    public static void SetRoundedCorners(nint hwnd, bool rounded)
    {
        if (hwnd == 0) return;

        var corner = rounded ? NativeMethods.DWMWCP_ROUND : NativeMethods.DWMWCP_DONOTROUND;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>
    /// ウィンドウの外周に DWM が引く線を消す。
    ///
    /// 既定では 1px の枠が描かれる。キーボードは画面端に接して置くため、
    /// 右下に細い線として残って見える。素材の縁を自前で描いているので不要。
    /// </summary>
    public static void HideBorder(nint hwnd)
    {
        if (hwnd == 0) return;

        var color = NativeMethods.DWMWA_COLOR_NONE;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref color, sizeof(int));
    }

    /// <summary>
    /// 外周の線を既定の色に戻す。
    ///
    /// <see cref="HideBorder"/> を掛けたままだと、角を丸めたときに
    /// 直線部分だけ線が残り、角では途切れて見える。
    /// 標準のウィンドウとして見せるなら、こちらに戻す必要がある。
    /// </summary>
    public static void ShowBorder(nint hwnd)
    {
        if (hwnd == 0) return;

        var color = NativeMethods.DWMWA_COLOR_DEFAULT;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref color, sizeof(int));
    }
}
