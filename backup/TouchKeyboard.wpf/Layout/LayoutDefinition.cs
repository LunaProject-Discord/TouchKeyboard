using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace TouchKeyboard.Layout;

/// <summary>キーの 1 行。</summary>
public sealed class KeyRow
{
    public IReadOnlyList<KeyDefinition> Keys { get; init; } = [];

    /// <summary>この行に置かれたキーの幅の合計。</summary>
    [JsonIgnore]
    public double UsedUnits => Keys.Sum(k => k.Width);
}

/// <summary>
/// レイアウト全体。キーの物理配置とスキャンコードの対応を持つ。
/// </summary>
public sealed class LayoutDefinition
{
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 1 行あたりのユニット総数。全行をこの値で割って配置するため、
    /// 行ごとのキー数が違っても縦の位置が揃う。
    /// 行のキー幅の合計がこれに満たない場合、右端に隙間ができる。
    /// </summary>
    public double RowUnits { get; init; } = 15.0;

    public IReadOnlyList<KeyRow> Rows { get; init; } = [];

    /// <summary>
    /// 定義の整合性を確認する。行の幅が <see cref="RowUnits"/> を超えていると
    /// キーがはみ出して見切れるため、読み込み時に弾く。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Rows.Count == 0)
        {
            errors.Add("行が 1 つも定義されていません。");
        }

        for (var i = 0; i < Rows.Count; i++)
        {
            var used = Rows[i].UsedUnits;
            if (used > RowUnits + 0.001)
            {
                errors.Add($"{i + 1} 行目の幅の合計が {used} で、rowUnits({RowUnits}) を超えています。");
            }

            foreach (var key in Rows[i].Keys)
            {
                if (key.Width <= 0)
                {
                    errors.Add($"{i + 1} 行目の「{key.Label}」の width が {key.Width} です。");
                }

                // Fn は送出しないためスキャンコード 0 を許す。それ以外の 0 は定義漏れ。
                if (key.ScanCode == 0 && !key.Spacer && key.Modifier != ModifierKind.Fn)
                {
                    errors.Add($"{i + 1} 行目の「{key.Label}」に scanCode が設定されていません。");
                }
            }
        }

        return errors;
    }
}
