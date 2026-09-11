using System;
using System.Globalization;
using System.Text.Json.Serialization;

namespace TouchKeyboard.Layout;

/// <summary>修飾キーの種別。</summary>
public enum ModifierKind
{
    /// <summary>修飾キーではない通常キー。</summary>
    None = 0,

    Shift,
    Ctrl,
    Alt,
    Win,

    /// <summary>
    /// Fn はスキャンコードを送出しない。物理キーボードの Fn はファームウェアで処理され
    /// OS に届かないため、アプリ内部でキー段を切り替える機能として扱う。
    /// </summary>
    Fn,
}

/// <summary>
/// キー 1 つ分の定義。
/// キーのサイズはユニット倍率で表現する。絶対値にすると解像度・DPI ごとに
/// 定義を作り直すことになる。
/// </summary>
public sealed class KeyDefinition
{
    /// <summary>通常時の表示。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Segoe Fluent Icons のコードポイント（16 進、例 "E750"）。
    /// 指定するとラベルの代わりにアイコンを描く。BackSpace や矢印など、
    /// 標準タッチキーボードが文字ではなく記号で表しているキーに使う。
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>Fn ラッチ時のアイコン。省略時は Fn でもラベル表示に切り替わる。</summary>
    public string? FnIcon { get; init; }

    /// <summary>Shift ラッチ時の表示。省略時は <see cref="Label"/> を使う。</summary>
    public string? ShiftLabel { get; init; }

    /// <summary>Fn ラッチ時の表示。省略時は <see cref="Label"/> を使う。</summary>
    public string? FnLabel { get; init; }

    /// <summary>送出するスキャンコード。</summary>
    [JsonConverter(typeof(HexUShortConverter))]
    public ushort ScanCode { get; init; }

    /// <summary>Fn ラッチ時に送出するスキャンコード。省略時は Fn でも通常と同じキーを送る。</summary>
    [JsonConverter(typeof(NullableHexUShortConverter))]
    public ushort? FnScanCode { get; init; }

    /// <summary>KEYEVENTF_EXTENDEDKEY を付けるか。</summary>
    public bool Extended { get; init; }

    /// <summary>Fn ラッチ時に KEYEVENTF_EXTENDEDKEY を付けるか。</summary>
    public bool FnExtended { get; init; }

    /// <summary>ユニット倍率。行内での幅の比率。</summary>
    public double Width { get; init; } = 1.0;

    /// <summary>修飾キーの場合その種別。指定時はラッチ動作になる。</summary>
    public ModifierKind Modifier { get; init; } = ModifierKind.None;

    /// <summary>長押しリピートの対象か。修飾キーとスペーサーは常に対象外。</summary>
    public bool Repeatable { get; init; } = true;

    /// <summary>レイアウト上の隙間。キーとして描画も送出もしない。</summary>
    public bool Spacer { get; init; }

    /// <summary>
    /// 下の行のキーと繋げて 1 つのキーに見せる。下辺の余白と角丸を落とす。
    /// JIS の L 字 Enter を上下 2 段で表現するために使う。
    /// </summary>
    public bool MergeDown { get; init; }

    /// <summary>上の行のキーと繋げて 1 つのキーに見せる。上辺の余白と角丸を落とす。</summary>
    public bool MergeUp { get; init; }

    /// <summary>
    /// Shift 時の文字をキーの左上に小さく併記するか。
    /// 英字は大文字小文字が入れ替わるだけなので併記しない（標準タッチキーボードと同じ）。
    /// </summary>
    [JsonIgnore]
    public bool ShowsShiftHint =>
        ShiftLabel is { Length: > 0 }
        && Label is { Length: 1 }
        && !char.IsLetter(Label[0]);

    /// <summary>
    /// アイコン指定を実際の文字に変換する。未指定・不正な値なら null。
    /// </summary>
    public static string? ResolveIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;

        var text = icon.AsSpan().Trim();
        if (text.StartsWith("U+", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];

        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
            ? char.ConvertFromUtf32(code)
            : null;
    }

    [JsonIgnore]
    public bool IsModifier => Modifier != ModifierKind.None;

    /// <summary>実際にリピートさせてよいか。</summary>
    [JsonIgnore]
    public bool CanRepeat => Repeatable && !IsModifier && !Spacer;

    /// <summary>Fn ラッチ中に別のキーを送出する定義を持つか。</summary>
    [JsonIgnore]
    public bool HasFnLayer => FnScanCode.HasValue;
}
