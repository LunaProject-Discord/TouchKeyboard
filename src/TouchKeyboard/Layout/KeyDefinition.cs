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
    ///
    /// 高さに余裕があればラベルと縦に並べ、無ければアイコンだけを描く。
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>Fn ラッチ時のアイコン。省略時は Fn でもラベル表示に切り替わる。</summary>
    public string? FnIcon { get; init; }

    /// <summary>
    /// 高さに余裕があってもラベルを併記しない。
    ///
    /// Windows キーや BackSpace のように、アイコンだけで通じるものに指定する。
    /// ラベル自体は残るので、送出の記録には引き続き使われる。
    /// </summary>
    public bool IconOnly { get; init; }

    /// <summary>
    /// 印字を枠で囲む。
    ///
    /// IME の入／切のように、刻印が枠付きで印刷されているキーに使う。
    /// 1 文字だけの印字を他の字と区別する印でもある。
    /// </summary>
    public bool BoxedLabel { get; init; }

    /// <summary>
    /// かな入力での字。物理のキーと同じく、キーの右下に小さく刻む。
    /// 送出には使わない。かな入力は IME 側の設定で決まる。
    /// </summary>
    public string? Kana { get; init; }

    /// <summary>Shift を伴うかなの字。キーの右上に刻む。小書きの字や句読点。</summary>
    public string? KanaShift { get; init; }

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

    /// <summary>
    /// 仮想キーコード。指定するとスキャンコードを OS の配列から引いて送る。
    ///
    /// IME の入／切のように、日本語 106/109 の刻印に無く、値を配列ドライバが
    /// 決めているキーに使う。推測で書かず、OS に聞いた値を送るための指定。
    /// 引けない環境では <see cref="ScanCode"/> をそのまま送る。
    /// </summary>
    [JsonConverter(typeof(NullableHexUShortConverter))]
    public ushort? VirtualKey { get; init; }

    /// <summary>KEYEVENTF_EXTENDEDKEY を付けるか。</summary>
    public bool Extended { get; init; }

    /// <summary>Fn ラッチ時に KEYEVENTF_EXTENDEDKEY を付けるか。</summary>
    public bool FnExtended { get; init; }

    /// <summary>
    /// Fn ラッチ中は置かない。
    ///
    /// 空いた幅は、同じ行で Fn 段を持つ文字キーが等分して受け取る。
    /// 数字段を 12 個の F キーで埋めるように、段の構成そのものを変えたい場合に使う。
    /// </summary>
    public bool FnHidden { get; init; }

    /// <summary>ユニット倍率。行内での幅の比率。</summary>
    public double Width { get; init; } = 1.0;

    /// <summary>修飾キーの場合その種別。指定時はラッチ動作になる。</summary>
    public ModifierKind Modifier { get; init; } = ModifierKind.None;

    /// <summary>
    /// Caps Lock キーか。灯り（LED）に実際の Caps Lock の状態を映す。
    ///
    /// Shift 等と違い、Caps Lock は自分では状態を持たない。送出は普通のキーと同じで、
    /// 掛かる・外れるは OS 側で決まる。物理キーボードから切り替えられることもあるため、
    /// <see cref="ModifierKind"/> によるラッチ管理ではなく、実際の状態を読みに行く。
    /// </summary>
    public bool IsCapsLock { get; init; }

    /// <summary>
    /// Windows キーの 4 マス印字か。
    ///
    /// 本物のロゴ（トレードマーク）はアイコンフォントに収録されていないため、
    /// 字形として持たせられない。Surface Type Cover の実機刻印（4 枚の正方形が
    /// 小さな隙間を空けて並ぶ、単色・角丸）を基準に、図形として描く。
    /// </summary>
    public bool IsWindowsLogo { get; init; }

    /// <summary>
    /// キーの色。
    ///
    /// テーマ側で定義した名前（"normal" / "modifier"）か、
    /// "#RRGGBB" / "#AARRGGBB" の形の色そのものを指定する。
    ///
    /// 名前で指定すると明暗のテーマに追従する。色を直に書くと追従しないため、
    /// 一部のキーだけ意図して目立たせたい場合に限る。
    ///
    /// 省略時は修飾キーなら "modifier"、それ以外は "normal" として扱う。
    /// </summary>
    public string? Tone { get; init; }

    /// <summary>
    /// 印字を上下にずらす量。字の大きさに対する割合で、正が下、負が上。
    ///
    /// 字もアイコンも、外形ではなく行の寸法で配置される。字形が行の中で
    /// 上下どちらかに寄っているキーでは、そのぶん印字全体が中央からずれる。
    /// ずれ方は字形ごとに違うので、実測した値をキーごとに持たせる。
    /// </summary>
    public double FaceOffset { get; init; }

    /// <summary>
    /// ラベルとアイコンを縦に並べているときの上下のずらし量。
    ///
    /// アイコンだけのときとは値が違う。積むと字形の余白の出方が変わるため。
    /// 省略時は <see cref="FaceOffset"/> を使う。
    /// </summary>
    public double? PairedFaceOffset { get; init; }

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
    /// ラベルもアイコンも描かない。
    ///
    /// 結合したキーの片側に使う。両方に文字を出すと 1 つのキーに 2 つ並んでしまう。
    /// ラベル自体は残るので、送出の記録には引き続き使われる。
    /// </summary>
    public bool HideFace { get; init; }

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

    /// <summary>
    /// 押すと文字が出るキーか。
    ///
    /// 判定は塗り分けと同じ根拠に置く。色が違って見えるキーと、
    /// 字の大きさが違うキーが食い違うと、区別の意味が分からなくなる。
    /// </summary>
    [JsonIgnore]
    public bool IsCharacter =>
        !IsModifier
        && !string.Equals(Tone, "modifier", StringComparison.OrdinalIgnoreCase);

    /// <summary>実際にリピートさせてよいか。</summary>
    [JsonIgnore]
    public bool CanRepeat => Repeatable && !IsModifier && !Spacer;

    /// <summary>Fn ラッチ中に別のキーを送出する定義を持つか。</summary>
    [JsonIgnore]
    public bool HasFnLayer => FnScanCode.HasValue;
}
