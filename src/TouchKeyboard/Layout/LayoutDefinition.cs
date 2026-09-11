using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace TouchKeyboard.Layout;

/// <summary>キーの 1 行。</summary>
public sealed class KeyRow
{
    public IReadOnlyList<KeyDefinition> Keys { get; init; } = [];

    /// <summary>
    /// 行の高さ。他の行に対する倍率。既定 1.0。
    /// ファンクション段のように控えめに見せたい行で 0.5 などを指定する。
    /// </summary>
    public double Height { get; init; } = 1.0;

    /// <summary>
    /// この行が縦にまたがる行数。既定 1。
    ///
    /// 2 以上にすると次の行にも重なる。矢印クラスタのように、
    /// 主キー列の中に別のキーを差し込みたい場合に使う。
    /// またがられた側の行は、重なる位置をスペーサーで空けておくこと。
    /// </summary>
    public int RowSpan { get; init; } = 1;

    /// <summary>
    /// 直前の行と同じ位置に重ねる。新しい行を消費しない。
    ///
    /// またがっている行の内側にキーを差し込むために使う。
    /// 重なる位置は、またがっている側がスペーサーで空けておくこと。
    /// </summary>
    public bool OverlayPrevious { get; init; }

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
    /// Shift 側の記号を、基の字の真上に縦一列で刻む。
    ///
    /// 物理のキーボードの刻み方。記号を左上に貼って基の字を中央に置く流儀は
    /// 標準のタッチキーボードのもので、こちらを立てない配列はそちらになる。
    /// </summary>
    public bool StackedLabels { get; init; }

    /// <summary>
    /// 端の列でも字を寄せず、すべて中央に置く。
    ///
    /// テンキーのように幅の狭い盤で使う。列が数えるほどしかないため、
    /// 端へ寄せると盤全体が左右に割れて見える。
    /// </summary>
    public bool CenterLabels { get; init; }

    /// <summary>
    /// 盤が小さくなって行が細くなりすぎたときに、代わりに出す配列のファイル名。
    ///
    /// クラシックのファンクション段のように、他より低い行を持つ配列で使う。
    /// その行だけ先に読めなくなるため、段ごと落とした派生に差し替える。
    /// 指定が無ければ切り替えない。
    /// </summary>
    public string? CompactAlternative { get; init; }

    /// <summary>
    /// 置かれた行ごとの高さ倍率。重ねた行は場所を消費しないので数えない。
    /// </summary>
    public IReadOnlyList<double> PlacedRowHeights()
    {
        var placements = RowPlacements();
        var heights = new List<double>();

        for (var i = 0; i < Rows.Count; i++)
        {
            // 重ねた行。高さは重なる先の行が決める。
            if (placements[i] < heights.Count) continue;

            heights.Add(Rows[i].Height);
        }

        return heights;
    }

    /// <summary>全行の高さの合計。キー段の高さをこれで割ると 1 倍ぶんの高さになる。</summary>
    [JsonIgnore]
    public double TotalHeightUnits => PlacedRowHeights().Sum();

    /// <summary>最も低い行の高さ倍率。</summary>
    [JsonIgnore]
    public double ShortestRowUnits => PlacedRowHeights().DefaultIfEmpty(1.0).Min();

    /// <summary>
    /// 各行が実際に置かれる位置。<see cref="KeyRow.OverlayPrevious"/> の行は
    /// 直前と同じ位置を返し、新しい位置を消費しない。
    /// 描画と検証で同じ結果を使うため、ここで一度だけ計算する。
    /// </summary>
    public IReadOnlyList<int> RowPlacements()
    {
        var placements = new int[Rows.Count];
        var index = -1;

        for (var i = 0; i < Rows.Count; i++)
        {
            if (!Rows[i].OverlayPrevious || index < 0) index++;
            placements[i] = index;
        }

        return placements;
    }

    /// <summary>実際に必要な行数。重ねた行は数に含めない。</summary>
    public int PlacedRowCount()
    {
        var placements = RowPlacements();
        return placements.Count == 0 ? 0 : placements[^1] + 1;
    }

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

            if (Rows[i].Height <= 0)
            {
                errors.Add($"{i + 1} 行目の height が {Rows[i].Height} です。");
            }

            if (Rows[i].RowSpan < 1)
            {
                errors.Add($"{i + 1} 行目の rowSpan が {Rows[i].RowSpan} です。");
            }

            if (RowPlacements()[i] + Rows[i].RowSpan > PlacedRowCount())
            {
                errors.Add($"{i + 1} 行目の rowSpan({Rows[i].RowSpan}) が行数を超えています。");
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

        errors.AddRange(FindOverlaps());

        return errors;
    }

    /// <summary>
    /// またがる行と、またがられる行のキーが横位置で衝突していないかを調べる。
    ///
    /// 幅の合計だけを見ても検出できない。またがる側は相手の行に重なって描かれるため、
    /// 同じ横位置に実キーがあると 2 つのキーが重なって表示される。
    /// </summary>
    private IEnumerable<string> FindOverlaps()
    {
        var placements = RowPlacements();

        for (var i = 0; i < Rows.Count; i++)
        {
            var startRow = placements[i];
            var endRow = startRow + Rows[i].RowSpan;

            for (var j = i + 1; j < Rows.Count; j++)
            {
                // 縦に重なっていない行同士は比べる必要がない。
                var otherStart = placements[j];
                var otherEnd = otherStart + Rows[j].RowSpan;
                if (startRow >= otherEnd || endRow <= otherStart) continue;

                foreach (var (labelA, startA, endA) in Spans(Rows[i]))
                {
                    foreach (var (labelB, startB, endB) in Spans(Rows[j]))
                    {
                        // 端が接するだけなら重なりではない。
                        if (startA >= endB - 0.001 || endA <= startB + 0.001) continue;

                        yield return
                            $"{i + 1} 行目の「{labelA}」({startA:0.##}〜{endA:0.##}) と "
                            + $"{j + 1} 行目の「{labelB}」({startB:0.##}〜{endB:0.##}) が重なります。";
                    }
                }
            }
        }
    }

    /// <summary>行内の実キーの占有範囲。スペーサーは場所を空けるためのものなので除く。</summary>
    private static IEnumerable<(string Label, double Start, double End)> Spans(KeyRow row)
    {
        var offset = 0.0;

        foreach (var key in row.Keys)
        {
            if (!key.Spacer) yield return (key.Label, offset, offset + key.Width);
            offset += key.Width;
        }
    }
}
