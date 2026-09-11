using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using TouchKeyboard.Interop;

namespace TouchKeyboard.Views;

/// <summary>
/// 標準タッチキーボードに寄せた配色。
/// OS のライト／ダーク設定に追従する。
/// </summary>
public sealed record Theme(
    bool IsDark,
    Brush WindowBackground,
    Brush KeyBackground,
    Brush KeyBorder,
    Brush KeyForeground,
    Brush KeyPressed,
    Brush ModifierLatched,
    Brush ModifierLocked,
    Brush AccentForeground,
    Brush SecondaryForeground)
{
    private static Brush Frozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Fluent のコントロール枠。上辺が明るく下辺が暗いグラデーションで、
    /// 影を落とさずにキーの立体感を出す。標準タッチキーボードもこの手法を使っている。
    /// </summary>
    private static Brush EdgeBorder(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static Color Rgba(byte r, byte g, byte b, byte a) => Color.FromArgb(a, r, g, b);

    public static Theme Dark { get; } = new(
        IsDark: true,
        WindowBackground: Frozen(0x2C, 0x2C, 0x2C, 0xF2),
        KeyBackground: Frozen(0x3B, 0x3B, 0x3B),
        KeyBorder: EdgeBorder(Rgba(0xFF, 0xFF, 0xFF, 0x1A), Rgba(0xFF, 0xFF, 0xFF, 0x12)),
        KeyForeground: Frozen(0xFF, 0xFF, 0xFF),
        KeyPressed: Frozen(0x00, 0x5F, 0xB8),
        ModifierLatched: Frozen(0x2C, 0x4A, 0x66),
        ModifierLocked: Frozen(0x00, 0x5F, 0xB8),
        AccentForeground: Frozen(0x60, 0xCD, 0xFF),
        SecondaryForeground: Frozen(0x9E, 0x9E, 0x9E));

    public static Theme Light { get; } = new(
        IsDark: false,
        WindowBackground: Frozen(0xF3, 0xF3, 0xF3, 0xF2),
        KeyBackground: Frozen(0xFF, 0xFF, 0xFF),
        KeyBorder: EdgeBorder(Rgba(0x00, 0x00, 0x00, 0x18), Rgba(0x00, 0x00, 0x00, 0x40)),
        KeyForeground: Frozen(0x1B, 0x1B, 0x1B),
        KeyPressed: Frozen(0x00, 0x5F, 0xB8),
        ModifierLatched: Frozen(0xCC, 0xE0, 0xF5),
        ModifierLocked: Frozen(0x00, 0x5F, 0xB8),
        AccentForeground: Frozen(0x00, 0x5F, 0xB8),
        SecondaryForeground: Frozen(0x60, 0x60, 0x60));

    /// <summary>
    /// OS のアプリ用テーマ設定を読む。
    /// 読めない場合はダークを既定とする。
    /// </summary>
    public static Theme FromSystem()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            // AppsUseLightTheme: 1 ならライト、0 ならダーク。
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value != 0 ? Light : Dark;
            }
        }
        catch (Exception)
        {
            // レジストリが読めない環境でも動作を止めない。
        }

        return Dark;
    }

    /// <summary>背景適用の結果。切り分け用に各 API の戻り値をそのまま持つ。</summary>
    /// <param name="Applied">Acrylic が適用でき、背景を透明にしてよいか。</param>
    /// <param name="BackdropHResult">DWMWA_SYSTEMBACKDROP_TYPE の HRESULT。</param>
    /// <param name="FrameHResult">DwmExtendFrameIntoClientArea の HRESULT。</param>
    public readonly record struct BackdropResult(bool Applied, int BackdropHResult, int FrameHResult)
    {
        public override string ToString() =>
            $"acrylic={(Applied ? "OK" : "NG")} "
            + $"dwm-backdrop=0x{BackdropHResult:X8} dwm-frame=0x{FrameHResult:X8}";
    }

    /// <summary>
    /// DWM にテーマと背景を伝える。
    ///
    /// バックドロップは DWM がウィンドウの「背後」に描くため、
    /// WPF 側がクライアント領域を不透明に塗っていると見えない。
    /// 呼び出し側は <see cref="BackdropResult.Applied"/> が true のときだけ背景を透明にすること。
    ///
    /// なお WindowStyle="None" の WPF ウィンドウは WS_POPUP として作られ DWM のフレームを持たない。
    /// フレームが無いとガラスを広げる対象が無く、バックドロップがクライアント領域に出ない。
    /// 呼び出し側は WindowChrome でクロームを隠しつつフレームは残すこと。
    /// </summary>
    public BackdropResult ApplyToWindow(nint hwnd)
    {
        if (hwnd == 0) return new BackdropResult(false, unchecked((int)0x80070006), 0);

        var dark = IsDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        // 角丸はドッキング状態に応じて呼び出し側が決める。ここでは触らない。

        // DWM のバックドロップは明示的に切る。
        // 非アクティブなウィンドウでは単色フォールバックが描かれ、
        // その単色が Acrylic のぼかしを手前から潰してしまう。
        // WS_EX_NOACTIVATE の本アプリは構造上つねに非アクティブなので、この経路は使えない。
        var backdrop = NativeMethods.DWMSBT_NONE;
        var backdropHr = NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        // ぼかしは SetWindowCompositionAttribute だけで入れる。
        // こちらはフレームの拡張を必要とせず、アクティブ状態にも依存しない。
        var acrylic = ApplyAcrylic(hwnd);

        return new BackdropResult(acrylic, backdropHr, 0);
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
    /// Acrylic のぼかしを適用する。
    ///
    /// SetWindowCompositionAttribute は非公開 API だが、DWM のバックドロップと違い
    /// ウィンドウのアクティブ状態に依存しない。WS_EX_NOACTIVATE を必須とする本アプリで
    /// 標準タッチキーボード相当の見た目を得るにはこの経路しかない。
    /// </summary>
    /// <returns>適用できたか。失敗した場合は単色背景にフォールバックすること。</returns>
    private bool ApplyAcrylic(nint hwnd)
    {
        // GradientColor は AABBGGRR。アルファがぼかしの上に乗る色の濃さになる。
        // 薄すぎると壁紙の色がそのまま出て派手になり、濃すぎるとぼかしが消える。
        // 標準タッチキーボードは彩度が抜けた明るいグレーに見えるため、それに寄せる。
        var tint = IsDark ? 0xCC1F1F1Fu : 0xCCECECECu;

        var policy = new NativeMethods.ACCENT_POLICY
        {
            AccentState = NativeMethods.AccentState.EnableAcrylicBlurBehind,
            AccentFlags = 0,
            GradientColor = tint,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<NativeMethods.ACCENT_POLICY>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(policy, buffer, fDeleteOld: false);

            var data = new NativeMethods.WINDOWCOMPOSITIONATTRIBDATA
            {
                Attrib = NativeMethods.WCA_ACCENT_POLICY,
                pvData = buffer,
                cbData = size,
            };

            return NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (EntryPointNotFoundException)
        {
            // 非公開 API のため、存在しない環境も想定しておく。
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
