using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;
using TouchKeyboard.Input;

// 暗黙の using が取り込む System.IO.Path と名前が衝突するため、図形側に別名を付ける。
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using TouchKeyboard.Layout;

namespace TouchKeyboard.Views;

/// <summary>キーが盤のどちらの端に接しているか。</summary>
public enum RowEdge
{
    None,
    Left,
    Right,
}

/// <summary>
/// キー 1 つ分の表示。描画とタップの受付のみを担い、入力ロジックを持たない。
///
/// 長押しリピートと押下・ホバーの視覚状態は <see cref="RepeatButton"/> に任せる。
/// テンプレートは差し替えない。差し替えると WinUI 標準の押下表現も失われる。
/// 塗り・枠・角丸はボタン自身に持たせ、ラッチ状態だけをこちらで上書きする。
///
/// WinUI の RepeatButton は sealed なので継承せず内側に持たせる。
/// </summary>
public sealed partial class KeyButton : UserControl
{
    private const double Gap = 3;
    private const double Radius = 6;

    /// <summary>
    /// 結合したキーの段差にできる入隅の半径。
    ///
    /// 入隅なので丸みは隣のキーとは逆向きに膨らむ。隣との隙間
    /// （<see cref="Gap"/> の 2 倍）と同じ値まで取っても、線が隣に触れることはない。
    /// </summary>
    private const double InnerRadius = Radius;

    /// <summary>
    /// アイコン用。盤面・タイトルバー・通知領域アイコンなど、アイコンとして描く
    /// グリフはこれで揃える（Caps Lock の南京錠を除く）。私用領域（U+E000〜U+F8FF）の
    /// グリフを描く。OS 同梱の Segoe Fluent Icons を使う。
    ///
    /// WinUI の FontFamily はカンマ区切りのフォールバックを解釈しない。
    /// WPF のように "A, B, C" と書いても先頭しか使われないため、単一のファミリ名で持つ。
    /// </summary>
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons");

    /// <summary>
    /// 記号用のフォールバック。私用領域の外にあるグリフをもし使うことになった場合に使う。
    /// いまのところ、盤面のアイコンはすべて上の 2 つで賄えており、出番は無い。
    /// </summary>
    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol");

    /// <summary>
    /// 通常のラベル用。同梱のフォントを使う。
    ///
    /// 英数と和文を 1 つのフォントで賄えるので、字種によって書体が変わらない。
    /// OS のフォントフォールバックに任せていたときは、和文だけ別のフォントで
    /// 描かれ、行送りや太さが揃わなかった。
    ///
    /// アンパッケージ実行では ms-appx:/// が exe のあるディレクトリを指す。
    /// # の後ろはフォント内部の書体名で、ファイル名とは別物。
    /// </summary>
    private static readonly FontFamily TextFont =
        new("ms-appx:///Assets/Fonts/GenInterfaceJP-Regular.ttf#Gen Interface JP");

    /// <summary>グリフの位置に応じてフォントを選ぶ。</summary>
    private static FontFamily FontForGlyph(string glyph) =>
        glyph.Length > 0 && glyph[0] is >= '\uE000' and <= '\uF8FF' ? IconFont : SymbolFont;

    /// <summary>
    /// Windows キーの 4 マス印字を組み立てる。
    ///
    /// Surface Type Cover の実機刻印を基準にした比率。正方形どうしの隙間は、
    /// 一辺の約 1/6（列・行を 1 : 0.16 : 1 の星型比率で割る）。角丸は付けない。
    /// 大きさは <see cref="SizeWindowsGlyph"/> が呼び出しのたびに決める。
    /// </summary>
    private static (Grid Grid, Border[] Squares) BuildWindowsGlyph()
    {
        var grid = new Grid
        {
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.16, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.16, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var squares = new Border[4];
        var positions = new (int Row, int Column)[] { (0, 0), (0, 2), (2, 0), (2, 2) };

        for (var i = 0; i < squares.Length; i++)
        {
            var square = new Border();
            Grid.SetRow(square, positions[i].Row);
            Grid.SetColumn(square, positions[i].Column);
            grid.Children.Add(square);
            squares[i] = square;
        }

        return (grid, squares);
    }

    /// <summary>Windows キーの 4 マス印字の大きさを、アイコンと同じ寸法に合わせる。</summary>
    private void SizeWindowsGlyph(double size)
    {
        _windowsGlyph.Width = size;
        _windowsGlyph.Height = size;
    }

    /// <summary>
    /// Caps Lock キーの印字（矢印＋南京錠バッジ）の輪郭データ。
    /// <c>Assets/Icons/capslock.svg</c> と同じ値を持つ。形を直すときは
    /// まず SVG 側を直し、確定した座標・行列をここへ反映すること。
    ///
    /// 矢印は Segoe Fluent Icons の UpArrowShiftKey（U+E752）、南京錠は
    /// Fluent System Icons Filled の lock_closed（U+E79E）の輪郭を
    /// <see cref="System.Drawing.Drawing2D.GraphicsPath.AddString"/> で実際の
    /// グリフから抽出した値が元（手描きではない）。ただし矢印は GDI+ の抽出値
    /// そのままだと実機の Shift キー（WinUI/DirectWrite の実際の描画）より
    /// 横に細く出る不具合があり、実機のデバッグ撮影
    /// （<see cref="KeyboardWindow.CaptureDebugScreenshotAsync"/>）で測った
    /// Shift キーの実測比に合わせて、x=500 を軸に x だけ 1.10 倍している
    /// （最初は 1.18 倍にしたが「若干横に広がりすぎている」との指摘で落とした。
    /// detail は capslock.svg のコメント参照）。
    /// </summary>
    private const string CapsLockArrowPathData =
        "M328.13,875 C314.15,875 300.91,872.56 288.38,867.68 C275.84,862.79 264.83,856.04 255.35,847.41 C245.86,838.79 238.43,828.78 233.05,817.38 C227.68,805.99 225,793.95 225,781.25 L225,625 L152.49,625 C137.81,625 123.84,622.4 110.6,617.19 C97.34,611.98 85.79,604.9 75.95,595.95 C66.1,587 58.32,576.58 52.59,564.7 C46.85,552.82 43.99,540.2 43.99,526.86 C43.99,503.42 52.23,482.58 68.7,464.36 L473.15,11.72 C476.37,8.14 480.4,5.37 485.23,3.42 C490.07,1.46 494.98,0.49 500,0.49 C505.02,0.49 509.93,1.46 514.77,3.42 C519.6,5.37 523.63,8.14 526.85,11.72 L931.3,464.36 C947.77,482.58 956.01,503.42 956.01,526.86 C956.01,540.53 953.15,553.3 947.41,565.19 C941.68,577.07 933.9,587.4 924.05,596.19 C914.21,604.98 902.74,611.98 889.68,617.19 C876.61,622.4 862.55,625 847.51,625 L775,625 L775,781.25 C775,793.95 772.32,805.99 766.95,817.38 C761.57,828.78 754.14,838.79 744.65,847.41 C735.17,856.04 724.16,862.79 711.62,867.68 C699.09,872.56 685.85,875 671.88,875 Z " +
        "M671.88,812.5 C681.18,812.5 689.24,809.41 696.04,803.22 C702.85,797.04 706.25,789.71 706.25,781.25 L706.25,593.75 C706.25,585.29 709.65,577.96 716.46,571.78 C723.26,565.59 731.32,562.5 740.63,562.5 L847.51,562.5 C858.25,562.5 867.57,558.92 875.44,551.76 C883.32,544.6 887.26,536.13 887.26,526.37 C887.26,522.14 886.45,518.15 884.84,514.4 C883.23,510.66 880.99,507.16 878.13,503.91 L500,80.57 L121.87,503.91 C119.01,507.16 116.77,510.66 115.16,514.4 C113.55,518.15 112.74,522.14 112.74,526.37 C112.74,536.13 116.68,544.6 124.56,551.76 C132.44,558.92 141.75,562.5 152.49,562.5 L259.38,562.5 C268.68,562.5 276.74,565.59 283.54,571.78 C290.35,577.96 293.75,585.29 293.75,593.75 L293.75,781.25 C293.75,789.71 297.15,797.04 303.96,803.22 C310.76,809.41 318.82,812.5 328.13,812.5 Z " +
        "M259.38,1000 C250.07,1000 242.01,996.91 235.21,990.72 C228.4,984.54 225,977.21 225,968.75 C225,960.29 228.4,952.96 235.21,946.78 C242.01,940.59 250.07,937.5 259.38,937.5 L740.63,937.5 C749.93,937.5 757.99,940.59 764.79,946.78 C771.6,952.96 775,960.29 775,968.75 C775,977.21 771.6,984.54 764.79,990.72 C757.99,996.91 749.93,1000 740.63,1000 Z";

    /// <summary>
    /// 南京錠の輪郭（弦の穴・鍵穴を含む evenodd）。バッジ本体と、縁取り（halo）の
    /// 下敷きにする外側だけの輪郭の両方の元になる。
    /// </summary>
    private const string CapsLockLockPathData =
        "M500,42 C537.33,42 572,51.33 604,70 C636,88.67 661.33,114 680,146 C698.67,178 708,212.67 708,250 L708,334 C744,336.67 774,351 798,377 C822,403 834,433.33 834,468 L834,782 C834,819.33 820.67,851 794,877 C767.33,903 735.33,916 698,916 L302,916 C264.67,916 232.67,903 206,877 C179.33,851 166,819.33 166,782 L166,468 C166,433.33 178,403 202,377 C226,351 256,336.67 292,334 L292,250 C292,212.67 301.33,178 320,146 C338.67,114 364,88.67 396,70 C428,51.33 462.67,42 500,42 Z " +
        "M500,572 C480,572 465,581 455,599 C445,617 445,634.67 455,652 C465,669.33 480,678 500,678 C514.67,678 527,672.67 537,662 C547,651.33 552,639 552,625 C552,611 547,598.67 537,588 C527,577.33 514.67,572 500,572 Z " +
        "M500,104 C460,104 425.67,118.33 397,147 C368.33,175.67 354,210 354,250 L354,334 L646,334 L646,250 C646,210 631.67,175.67 603,147 C574.33,118.33 540,104 500,104 Z";

    /// <summary>南京錠の外側だけの輪郭（弦の穴・鍵穴を含まない、縁取り専用）。</summary>
    private const string CapsLockHaloPathData =
        "M500,42 C537.33,42 572,51.33 604,70 C636,88.67 661.33,114 680,146 C698.67,178 708,212.67 708,250 L708,334 C744,336.67 774,351 798,377 C822,403 834,433.33 834,468 L834,782 C834,819.33 820.67,851 794,877 C767.33,903 735.33,916 698,916 L302,916 C264.67,916 232.67,903 206,877 C179.33,851 166,819.33 166,782 L166,468 C166,433.33 178,403 202,377 C226,351 256,336.67 292,334 L292,250 C292,212.67 301.33,178 320,146 C338.67,114 364,88.67 396,70 C428,51.33 462.67,42 500,42 Z";

    /// <summary>
    /// 縁取り（1.3 倍）でも消えない矢印のかけら 1 か所だけを個別に消す小さな四角形。
    /// 座標は capslock.svg のコメントに書いたとおり、Browser 上でのピクセル走査による
    /// 実測値（弦の右上のごく一部、x はおよそ 942.5〜955.5、y はおよそ 480〜549.5）を
    /// 元にした外接矩形＋余白。手描きの目分量ではない。
    /// </summary>
    private const string CapsLockPatchPathData = "M938,476 L959,476 L959,554 L938,554 Z";

    /// <summary>SVG/XAML のパス・ミニ言語の座標・コマンドを拾うためのトークン切り出し。</summary>
    private static readonly System.Text.RegularExpressions.Regex PathTokenPattern =
        new(@"[MLCZ]|-?\d+(?:\.\d+)?", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// SVG/XAML のパス・ミニ言語の文字列（M/L/C/Z のみ。円弧は使わない）から
    /// <see cref="PathGeometry"/> を作る。<paramref name="transform"/> を渡すと、
    /// 読み取った座標をその場でアフィン変換してから図形に積む。
    ///
    /// 変換を <see cref="Microsoft.UI.Xaml.UIElement.RenderTransform"/> や
    /// <see cref="Geometry.Transform"/> で後掛けにせず、ここで座標そのものに
    /// 焼き込んでいるのは、<see cref="Microsoft.UI.Xaml.UIElement.Clip"/> が
    /// どちらの座標系（変換前/変換後）で効くかを実機で確かめずに済ませるため。
    /// 全部の座標が最初から最終的な 1000x1000 のキャンバス座標になっていれば、
    /// <see cref="Microsoft.UI.Xaml.Shapes.Path.Clip"/> に渡す矩形も同じ最終
    /// キャンバス座標で書けばよい。ただし <c>Assets/Icons/capslock.svg</c> 側の
    /// clipPath は SVG の仕様上ローカル（変換前）座標で書く決まりなので、
    /// 数値は SVG からそのままコピーできない。「最終的に欲しいキャンバス座標の
    /// 矩形」を計算し、それをここでは直接、SVG 側では transform の逆変換で
    /// ローカル座標に戻して書く（<see cref="BuildCapsLockGlyph"/> のコメント参照）。
    ///
    /// WinUI には WPF の <c>Geometry.Parse</c> に相当する API が無い。実行時に
    /// <see cref="XamlReader"/> で XAML 断片を読み込む案も試したが、アンパッケージ・
    /// 自己完結・uiAccess 署名のこのアプリでは実機で読み込みに失敗し、
    /// 盤面全体が表示されなくなる不具合を起こした（全キーのコンストラクタから
    /// 呼ばれるため、1 回でも失敗すると全キーが構築できなくなる）。そのため
    /// XAML パーサーを経由せず、座標を直接 <see cref="PathFigure"/>／
    /// <see cref="BezierSegment"/>／<see cref="LineSegment"/> に組み立てる。
    /// </summary>
    private static PathGeometry ParsePathGeometry(string data, Matrix? transform = null)
    {
        var tokens = PathTokenPattern.Matches(data);
        var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
        PathFigure? figure = null;

        Point NextPoint(ref int i)
        {
            var x = double.Parse(tokens[i++].Value, System.Globalization.CultureInfo.InvariantCulture);
            var y = double.Parse(tokens[i++].Value, System.Globalization.CultureInfo.InvariantCulture);

            if (transform is not { } m) return new Point(x, y);

            return new Point(
                (m.M11 * x) + (m.M21 * y) + m.OffsetX,
                (m.M12 * x) + (m.M22 * y) + m.OffsetY);
        }

        var index = 0;
        while (index < tokens.Count)
        {
            var command = tokens[index++].Value;
            switch (command)
            {
                case "M":
                    figure = new PathFigure { StartPoint = NextPoint(ref index), IsClosed = false };
                    geometry.Figures.Add(figure);
                    break;

                case "L":
                    figure!.Segments.Add(new LineSegment { Point = NextPoint(ref index) });
                    break;

                case "C":
                    var p1 = NextPoint(ref index);
                    var p2 = NextPoint(ref index);
                    var p3 = NextPoint(ref index);
                    figure!.Segments.Add(new BezierSegment { Point1 = p1, Point2 = p2, Point3 = p3 });
                    break;

                case "Z":
                    figure!.IsClosed = true;
                    break;
            }
        }

        return geometry;
    }

    /// <summary>
    /// Caps Lock キーの印字を組み立てる。<c>Assets/Icons/capslock.svg</c> をそのまま
    /// WinUI の図形に移したもの。1000x1000 の <see cref="Canvas"/> に、
    /// 矢印 → 縁取り（halo）→ 南京錠本体の順に重ね、<see cref="Viewbox"/> で
    /// 均等に拡縮する。行列・切り抜き矩形は SVG 側の transform / clipPath と
    /// 対応する値（ただし clipPath の数値そのものはコピーしない。下記参照）。
    ///
    /// 縁取りは背景色（キーの地色）で塗り、矢印の上に重ねて溶け込ませることで、
    /// 南京錠と矢印の線が接する場所でも境目を保つ（利用者の指示：
    /// 「お互いの線が重ならないようにしろ」）。
    ///
    /// SVG の clipPath の rect は、その `<path>` 自身の transform を適用する
    /// 前のローカル座標（= d 属性と同じ座標系）で書く決まりになっている
    /// （Browser で単独レンダリングして確認した仕様）。一方この
    /// <see cref="ParsePathGeometry"/> は変換後の最終 1000x1000 キャンバス座標を
    /// 直接 <see cref="PathGeometry"/> に焼き込むので、<see cref="Path.Clip"/> は
    /// その焼き込み済みの座標と同じ最終キャンバス座標でよく、SVG の rect の値を
    /// そのまま数値コピーしてはいけない（座標系が違う）。ここでの値は
    /// 「最終的に欲しいキャンバス座標の矩形」そのものを直接書いている。
    /// </summary>
    private void BuildCapsLockGlyph(out Viewbox glyph, out Path arrow, out Path halo, out Path patch, out Path lockBody)
    {
        arrow = new Path { Data = ParsePathGeometry(CapsLockArrowPathData) };

        // 南京錠の外側の輪郭だけを 1.3 倍に拡大し、矢印の上に重ねて縁取りにする。
        // 拡大の中心は南京錠本体と同じ中心・右下角固定（SVG のコメント参照）。
        // 倍率は元 1.2 倍だったが、矢印の縦横比補正（1.18→1.10 倍）で肩の位置が
        // 変わり、肩の外側にかけらが再び見えるようになったため 1.25 倍に、
        // それでも残っていたため 1.3 倍に段階的に上げた。
        //
        // 縁取りだけを平行移動してかけらを覆う案も試したが、南京錠本体（黒）と
        // 縁取り（白）の中心がずれて「縁取りがずれている」と分かるようになり、
        // 利用者に指摘された。位置は本体と同じ中心のまま、倍率だけで対応する。
        var haloMatrix = new Matrix(0.9374999999999999, 0, 0, 0.9374999999999999, 278.35131868131907, 235.79330067058135);
        halo = new Path
        {
            Data = ParsePathGeometry(CapsLockHaloPathData, haloMatrix),
            Clip = new RectangleGeometry
            {
                Rect = new Rect(423.97631868131907, 265.16830067058135, 569.9903846153845, 740.8317307692307),
            },
        };

        // 1.3 倍の縁取りでもまだ消えなかった、弦の右上のかけら 1 か所だけを個別に消す。
        patch = new Path { Data = ParsePathGeometry(CapsLockPatchPathData) };

        var lockMatrix = new Matrix(0.721153846153846, 0, 0, 0.721153846153846, 386.524395604396, 339.423108362889);
        lockBody = new Path { Data = ParsePathGeometry(CapsLockLockPathData, lockMatrix) };

        var canvas = new Canvas { Width = 1000, Height = 1000 };
        canvas.Children.Add(arrow);
        canvas.Children.Add(halo);
        canvas.Children.Add(patch);
        canvas.Children.Add(lockBody);

        glyph = new Viewbox
        {
            Child = canvas,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
    }

    /// <summary>
    /// Caps Lock キーの印字の大きさを、アイコンと同じ寸法に合わせる。
    /// 内部は 1000x1000 の固定座標系を <see cref="Viewbox"/> が均等に拡縮するだけなので、
    /// 一辺の長さを指定すればよい。
    /// </summary>
    private void SizeCapsLockGlyph(double size)
    {
        _capsLockGlyph.Width = size;
        _capsLockGlyph.Height = size;
    }

    /// <summary>
    /// アイコンと併記する価値のあるラベルか。
    /// Tab や Enter のような語には併記し、矢印のように記号 1 文字のものには併記しない。
    /// </summary>
    private static bool IsWordLabel(string label) =>
        label.Length >= 2 && label.Any(char.IsLetter);

    /// <summary>
    /// 数字か記号の 1 文字か。英字と区別する。
    /// 縦積みの配列では、この 2 つは左に寄せ、英字は中央に置く。
    /// </summary>
    private static bool IsSymbolOrDigit(string label) =>
        label.Length == 1 && !char.IsLetter(label[0]);

    /// <summary>区切りの罫線として扱う字。</summary>
    private static bool IsRuleChar(char c) => c is '―' or '─' or '—' or '–' or '-';

    /// <summary>
    /// 罫線の行でラベルを上下に分ける。罫線が無ければ下は null。
    ///
    /// 複数行のラベルでのみ罫線とみなす。1 行しかないラベルは、
    /// 「-」キーのように罫線と同じ字そのものが印字であることがある。
    /// </summary>
    private static (string Above, string? Below) SplitAtRule(string label)
    {
        if (!label.Contains('\n')) return (label, null);

        var lines = label.Split('\n');
        var at = Array.FindIndex(lines, line => line.Length > 0 && line.All(IsRuleChar));

        return at < 0
            ? (label, null)
            : (string.Join('\n', lines[..at]), string.Join('\n', lines[(at + 1)..]));
    }

    /// <summary>リピート・押下判定・視覚状態を担う。</summary>
    private readonly RepeatButton _button;

    /// <summary>アイコン。文字と併記する場合は左に置く。</summary>
    private readonly TextBlock _icon;

    /// <summary>
    /// Windows キーの 4 マス印字。<see cref="KeyDefinition.IsWindowsLogo"/> のキーだけ
    /// <see cref="_icon"/> の代わりに使う。本物のロゴはフォントに収録されていないため、
    /// 図形として描く（<see cref="BuildWindowsGlyph"/> 参照）。
    /// </summary>
    private readonly Grid _windowsGlyph;

    /// <summary>4 マスの実体。左上・右上・左下・右下の順。</summary>
    private readonly Border[] _windowsGlyphSquares;

    /// <summary>
    /// Caps Lock キーの印字。<see cref="KeyDefinition.IsCapsLock"/> のキーだけ
    /// <see cref="_icon"/> の代わりに使う。実機（日本語配列 Surface Type Cover）の刻印は
    /// 上向き矢印に南京錠を右下へ重ねた形。<c>Assets/Icons/capslock.svg</c> の図形を
    /// そのまま WinUI の <see cref="Path"/> で描く（<see cref="BuildCapsLockGlyph"/> 参照）。
    /// </summary>
    private readonly Viewbox _capsLockGlyph;

    /// <summary>矢印本体。Segoe Fluent Icons の UpArrowShiftKey の輪郭。</summary>
    private readonly Path _capsLockArrow;

    /// <summary>
    /// 南京錠と矢印の線が重なって見えないよう、矢印の上を背景色で覆う縁取り。
    /// 南京錠の外側の輪郭だけを拡大した形。
    /// </summary>
    private readonly Path _capsLockHalo;

    /// <summary>縁取りでも消えない、弦の右上のかけら 1 か所だけを個別に消す図形。</summary>
    private readonly Path _capsLockPatch;

    /// <summary>南京錠本体。Fluent System Icons Filled の lock_closed の輪郭。</summary>
    private readonly Path _capsLockLock;

    private readonly TextBlock _text;

    /// <summary>罫線より下に置く字。区切りを持たないキーでは使わない。</summary>
    private readonly TextBlock _textBelow;

    /// <summary>字と字の間に引く区切り線。</summary>
    private readonly Rectangle _rule;

    /// <summary>Shift 時の文字を左上に小さく併記する。英字キーには付けない。</summary>
    private readonly TextBlock? _shiftHint;

    /// <summary>縦に積んで刻むキーか。刻印は入れ替えず、置き方も変わる。</summary>
    private readonly bool _quad;

    /// <summary>
    /// 刻印を左の列に積むか。記号のキーとかなを刻むキーがこれにあたる。
    /// 数字は字をキーの中央に置き、記号だけを左上に浮かせる。
    /// </summary>
    private readonly bool _leftColumn;

    /// <summary>かなの刻印。右下に置く。</summary>
    private readonly TextBlock? _kana;

    /// <summary>Shift を伴うかなの刻印。右上に置く。</summary>
    private readonly TextBlock? _kanaShift;

    /// <summary>ボタンの内側。ラベルとアイコンを載せる。</summary>
    private readonly Grid _content;

    /// <summary>キー名とアイコンを縦に積む入れ物。</summary>
    private readonly StackPanel _face;

    /// <summary>字面の位置を決める入れ物。囲みの枠もここが引く。</summary>
    private readonly Border _faceBox;

    /// <summary>ラッチ状態を示す灯り。</summary>
    private readonly Ellipse _led;

    /// <summary>灯りの直径。</summary>
    private const double LedSize = 7;

    /// <summary>
    /// 押している間に四辺を内側へ入れる幅。キーの高さに対する割合。
    /// 0.1 なら、正方形のキーで一辺が 8 割になる。
    /// </summary>
    private const double PressInset = 0.1;

    /// <summary>沈むまでと戻るまでの時間（ミリ秒）。</summary>
    private const int PressDownMs = 70;
    private const int PressUpMs = 140;

    /// <summary>押下の縮み。作り直さず、この 1 つを動かし続ける。</summary>
    private readonly ScaleTransform _scale = new();

    /// <summary>
    /// 文字を出さないキーの左右の余白。
    /// 外側へ寄せた字が縁に貼り付かないように取る。
    /// </summary>
    private const double FacePadX = 4;

    /// <summary>
    /// 同じく上下の余白。
    /// 字は上下中央に置くため縁には寄らない。段の多い印字に高さを譲る。
    /// </summary>
    private const double FacePadY = 0;

    /// <summary>
    /// 文字を出すキーの余白。四方とも同じ。
    ///
    /// 字が 1 つ入るだけなので余白を広く取れる。役割の名前が入るキーは
    /// 字数があり、同じだけ空けると窮屈になる。
    /// </summary>
    private const double CharacterPad = 8;

    /// <summary>灯りと Shift 併記を縁から離す幅。上下の余白が足りない分を自分で持つ。</summary>
    private const double IndicatorPad = 4;

    /// <summary>
    /// 字とアイコンを積むときに、キーの縁との間に残す余白。
    /// 左右の余白と同じだけ取る。
    /// </summary>
    private const double PairPad = FacePadX;

    /// <summary>このキーの左右の余白。</summary>
    private double PadX => Definition.IsCharacter ? CharacterPad : FacePadX;

    /// <summary>このキーの上下の余白。</summary>
    private double PadY => Definition.IsCharacter ? CharacterPad : FacePadY;

    /// <summary>
    /// 行の右半分にあるキーか。併記するときの寄せ方を決める。
    ///
    /// 盤の外側へ向けて寄せると、キー名の並びが端で揃う。
    /// すべて中央に寄せると、幅の違うキーが並んだときに字がばらける。
    /// </summary>
    public bool OnRightHalf { get; set; }

    /// <summary>
    /// 盤の端に接しているか。接している側へ字面を寄せる。
    ///
    /// 端の列は幅がばらつく（半角/全角・Tab・Caps・Shift・Ctrl）。
    /// 中央に置くと字の位置が段ごとにずれ、盤の縁が揃って見えない。
    /// </summary>
    public RowEdge Edge { get; set; }

    /// <summary>
    /// 端の列で字の位置を決める基準のキーか。
    ///
    /// 基準は自分のキーの中央に字を置く。同じ列の残りは、その位置へ
    /// 合わせて端から押し出す。中央に置いたキーが 1 つあることで、
    /// 縦に揃った列が盤の縁に貼り付いて見えなくなる。
    /// </summary>
    public bool IsEdgeAnchor { get; set; }

    /// <summary>
    /// 端の列でも寄せず、常に中央に置くか。
    /// テンキーのように幅の狭い盤では、寄せるとかえって不揃いに見える。
    /// </summary>
    public bool CenterFace { get; set; }

    /// <summary>字の位置を合わせる先。同じ列の基準のキーを指す。</summary>
    private KeyButton? _edgeAnchor;

    /// <summary>自分を基準にしているキー。字面が変わったら知らせる。</summary>
    private readonly List<KeyButton> _edgeFollowers = [];

    /// <summary>
    /// ボタンの外側。結合部の枠線をここへ置く。
    ///
    /// 入隅の丸みは隣のキーとの隙間へ回り込むため、ボタンの内側に置くと
    /// はみ出した部分が切り落とされて線が途切れる。
    /// </summary>
    private readonly Grid _root;

    /// <summary>結合相手。押下・ホバーの表現を揃えるために持つ。</summary>
    private KeyButton? _mergePartner;

    // 結合したキーの視覚状態は、自分と相手の状態を合わせて決める。
    // 片側だけが反応すると 1 つのキーに見えない。
    private bool _selfHovered;
    private bool _partnerPressed;
    private bool _partnerHovered;

    /// <summary>
    /// 結合する辺のうち、相手に覆われず露出している左右の割合（このキーの幅に対する比）。
    /// JIS の L 字 Enter では、上段の下辺の左 1/8 ほどが段差として露出する。
    /// </summary>
    private double _exposedLeft;
    private double _exposedRight;

    /// <summary>
    /// 結合したキーの枠線。
    ///
    /// Border の一辺だけを消して矩形を継ぎ足す方法では、角丸の弧と継ぎ目の位置が合わない。
    /// L 字の輪郭を 1 本のパスとして引き、Border 自体は塗りだけを担当させる。
    /// </summary>
    private Path? _outline;

    /// <summary>
    /// 通常時とラッチ時の枠の太さ。
    /// 結合したキーでは 0 にし、枠線は <see cref="_outline"/> が 1 本で描く。
    /// </summary>
    private Thickness _normalBorder;
    private Thickness _latchedBorder;

    private Theme _theme;

    // 最後に反映した表示状態。ラッチの切り替えだけで描き直せるよう保持する。
    private LatchState _latch;

    /// <summary>いま Fn 段の割り当てを出しているか。</summary>
    private bool _onFnLayer;

    public KeyDefinition Definition { get; }

    /// <summary>押下時に 1 回、その後 Delay を置いて Interval ごとに上がる。</summary>
    public event RoutedEventHandler? Click;

    /// <summary>指が離れた・キーの外へ出た・キャンセルされた。</summary>
    public event EventHandler? Released;

    /// <param name="rowHeight">
    /// 行の高さ倍率。字の大きさには使わない。
    /// 背の低い行はキー自体が低くなり、割合で決めているぶん字も一緒に小さくなる。
    /// </param>
    public KeyButton(KeyDefinition definition, Theme theme, double rowHeight = 1.0, bool stacked = false)
    {
        Definition = definition;
        _theme = theme;

        // 上下の行と繋げるキーは、繋ぐ側の余白を詰めて 1 つのキーに見せる。
        //
        // 行と行の間の隙間 (Gap * 2) はどちらかの段が飲み込むしかない。これを下段に寄せる。
        // 上段側で飲み込むと上段だけが隣のキーより高くなり、段差の線が隣のキーの
        // 下辺より下にずれて見える。上段を隣と揃え、下段が上へ食い込む形にする。
        Margin = new Thickness(
            Gap,
            definition.MergeUp ? -Gap : Gap,
            Gap,
            Gap);

        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        // 上下の行と繋げる辺には枠を引かない。引くと結合部に継ぎ目の線が出る。
        _normalBorder = new Thickness(1, definition.MergeUp ? 0 : 1, 1, definition.MergeDown ? 0 : 1);
        _latchedBorder = new Thickness(2, definition.MergeUp ? 0 : 2, 2, definition.MergeDown ? 0 : 2);

        // 行の高さは字の外形に詰める。
        //
        // 既定では行送りぶんの高さを持ち、その中で字は上下の中央に来ない。
        // フォントは大文字の上端より上に、ベースラインの下より広く余地を取る。
        // その箱ごと中央に置くと、字は下へ寄って見える。
        // 大文字の上端からベースラインまでに詰めれば、見た目の中央と一致する。
        _icon = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextLineBounds = TextLineBounds.Tight,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        (_windowsGlyph, _windowsGlyphSquares) = BuildWindowsGlyph();
        BuildCapsLockGlyph(out _capsLockGlyph, out _capsLockArrow, out _capsLockHalo, out _capsLockPatch, out _capsLockLock);

        // 字は欧文のフォントを指定する。
        //
        // 指定しないと、日本語環境では UI 既定の和文フォントで描かれる。
        // 和文フォントは U+005C を円記号の字形で持つため、バックスラッシュのキーが
        // ¥ に見え、隣の ¥ キーと区別が付かなくなる。
        // 日本語の字はこのフォントに無いので、OS のフォールバックが和文で描く。
        _text = new TextBlock
        {
            Text = definition.Label,
            FontFamily = TextFont,
            VerticalAlignment = VerticalAlignment.Center,
            TextLineBounds = TextLineBounds.Tight,
            IsHitTestVisible = false,
        };

        // 罫線より下に置く字。半角/全角 の「漢字」のように、
        // 1 枚のキーで区切って印字するものに使う。
        _textBelow = new TextBlock
        {
            FontFamily = TextFont,
            VerticalAlignment = VerticalAlignment.Center,
            TextLineBounds = TextLineBounds.Tight,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        // 区切りの罫線。字では引かない。
        // 罫線の字は自分の行送りぶんの高さを持ち、上下に余白が空く。
        // 字の間に挟むには広すぎるので、線そのものを引く。
        _rule = new Rectangle
        {
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        // 併記するときはキー名の下にアイコンを置く。横に並べると、
        // 幅の狭いキーで両方が縮み、どちらも読めなくなる。
        _face = new StackPanel
        {
            Orientation = Orientation.Vertical,

            // 間隔は字の大きさに応じて UpdateFontSize が入れる。
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _face.Children.Add(_text);
        _face.Children.Add(_rule);
        _face.Children.Add(_textBelow);
        _face.Children.Add(_icon);
        _face.Children.Add(_windowsGlyph);
        _face.Children.Add(_capsLockGlyph);

        // 字面の入れ物。位置と、囲みの枠を持つ。
        //
        // 枠は入れ物側に持たせる。積み木（StackPanel）は枠を引けないので、
        // 囲うには 1 枚挟むしかない。指定の無いキーでは太さ 0 で、何も変わらない。
        _faceBox = new Border
        {
            Child = _face,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        // 縦に積んで刻むキーか。
        //
        // かなを刻むキーは 4 隅に分かれる。かなを刻まない配列でも、
        // 物理のキーに倣う指定があれば、記号を基の字の真上に積む。
        //
        // Shift 側を持たない数字・記号も同じ枠に入れる。字の位置は変わらないが、
        // 数字段を通して同じ組み方になり、後から記号を足したときにずれない。
        var stackable = definition.ShowsShiftHint
                        || (definition.IsCharacter && IsSymbolOrDigit(definition.Label));

        var hasKana = definition.Kana is { Length: > 0 }
                      || definition.KanaShift is { Length: > 0 };

        var quad = hasKana || (stacked && stackable);
        _quad = quad;

        // 左の列に積むか、字を中央に置くか。
        //
        // 実機は数字だけ字がキーの中央にあり、Shift 側の記号がその左上に浮く。
        // 記号のキー（-、^、¥、@、[、;、:、]、,、.、/、\）は違い、
        // Shift 側の記号と縦に積んで左の列に収まっている。かなを刻むキーも同じで、
        // 左が英数、右がかなの 2 列になる。
        var digitFace = definition.Label is { Length: 1 } face && char.IsAsciiDigit(face[0]);

        _leftColumn = quad && (definition.Kana is { Length: > 0 } || !digitFace);

        if (definition.ShowsShiftHint)
        {
            // 積むキーでは記号と基の字を互いの中央で揃える。組を左に寄せるのは
            // 入れ物の側（BuildQuad）。左端で揃えると、| のような細い字が
            // 下の字より左に出て見える。実測で ¥ の中心より 4px 左だった。
            _shiftHint = Corner(
                definition.ShiftLabel!,
                _leftColumn ? HorizontalAlignment.Center : HorizontalAlignment.Left);
        }

        // かなの刻印。物理のキーと同じく右側に置く。
        //
        // 4 隅に分かれる。左上が Shift の記号、左下が通常の字、
        // 右上が Shift を伴うかな、右下がかな。
        //
        // 片方だけのキーもある。かなを刻まない配列でも、句読点や鉤括弧は
        // 右上に刻まれている（「、」「。」「・」「「」「」」）。
        if (definition.Kana is { Length: > 0 } kana)
        {
            _kana = Corner(kana, HorizontalAlignment.Right);
        }

        if (definition.KanaShift is { Length: > 0 } kanaShift)
        {
            _kanaShift = Corner(kanaShift, HorizontalAlignment.Right);
        }

        // ボタンと同じ大きさで重ねる。余白はここが持つ。
        _content = new Grid
        {
            Margin = new Thickness(PadX, PadY, PadX, PadY),
            IsHitTestVisible = false,
        };

        // 4 隅に刻むキーは、揃えるための枠に入れてから重ねる。
        // それ以外は字面をそのまま重ね、併記は隅に置く。
        if (quad)
        {
            _content.Children.Add(BuildQuad());
        }
        else
        {
            _content.Children.Add(_faceBox);
            if (_shiftHint is not null) _content.Children.Add(_shiftHint);
        }

        // ラッチの表示。塗りではなく小さな灯りで示す。
        // 面全体を塗ると、字が読みにくくなるうえ押下の表示と紛れる。
        _led = new Ellipse
        {
            Width = LedSize,
            Height = LedSize,
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,

            // 左右はボタンの余白が決める。上下は余白を外したので自分で持つ。
            Margin = new Thickness(0, Math.Max(0, IndicatorPad - PadY), 0, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        _content.Children.Add(_led);

        TextBlock Corner(string text, HorizontalAlignment side)
        {
            var inset = Math.Max(0, IndicatorPad - PadY);

            return new TextBlock
            {
                Text = text,
                FontFamily = TextFont,

                // 大きさは配置が決まってから UpdateFontSize が入れる。
                Opacity = 0.65,
                HorizontalAlignment = side,

                // 4 隅では下端で揃える。外形に詰めてあるので、下端はベースラインと
                // 一致する。字種が違っても字が同じ線に並ぶ。
                // 隅に 1 つだけ置く場合は従来どおり上端に貼る。
                VerticalAlignment = quad ? VerticalAlignment.Bottom : VerticalAlignment.Top,
                Margin = quad ? new Thickness(0) : new Thickness(0, inset, 0, 0),

                IsHitTestVisible = false,
            };
        }

        /// <summary>隅に刻む枠。左右 2 列。かなを刻むかで段の組み方が変わる。</summary>
        Grid BuildQuad()
        {
            var grid = new Grid { IsHitTestVisible = false };

            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            // かなは右の隅に置く。Shift 側が上、そうでない側が下。
            Place(_kanaShift, 0, 1, VerticalAlignment.Top);
            Place(_kana, 0, 1, VerticalAlignment.Bottom);

            // 記号のキーは、記号と基の字を左の列にひと組で置く。
            // 上下は隅（記号が上端、字が下端）、左右は互いの中央で揃える。
            //
            // 組を作らず両方を左端で揃えると、細い字が下の字より左に出て見える。
            // 実測では ¥ の上の | が、¥ の中心より 4px 左にあった。
            // 組の幅は広いほうの字で決まり、狭いほうがその中央に来る。
            if (_leftColumn)
            {
                var pair = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    IsHitTestVisible = false,
                };

                if (_shiftHint is not null)
                {
                    _shiftHint.VerticalAlignment = VerticalAlignment.Top;
                    pair.Children.Add(_shiftHint);
                }

                _faceBox.VerticalAlignment = VerticalAlignment.Bottom;
                pair.Children.Add(_faceBox);

                grid.Children.Add(pair);

                return grid;
            }

            // 数字と英字は字がキーの中央。記号だけが左上の隅に浮く。
            Place(_shiftHint, 0, 0, VerticalAlignment.Top);
            Place(_faceBox, 0, 0, VerticalAlignment.Center);

            Grid.SetColumnSpan(_faceBox, 2);

            return grid;

            void Place(FrameworkElement? element, int row, int column, VerticalAlignment side)
            {
                if (element is null) return;

                element.VerticalAlignment = side;

                Grid.SetRow(element, row);
                Grid.SetColumn(element, column);
                grid.Children.Add(element);
            }
        }

        // 字面はボタンの中に入れない。
        //
        // 角丸を持つボタンは中身をその形に切り落とす。結合したキーでは
        // 字面を相手の段まではみ出させて中央に合わせるので、中に入れると
        // はみ出した部分が消える。ボタンには塗りと枠だけを任せ、
        // 字面は同じ大きさで重ねて置く。
        _button = new RepeatButton
        {
            CornerRadius = new CornerRadius(
                definition.MergeUp ? 0 : Radius,
                definition.MergeUp ? 0 : Radius,
                definition.MergeDown ? 0 : Radius,
                definition.MergeDown ? 0 : Radius),

            BorderThickness = _normalBorder,

            // 既定のボタンが持つ最小サイズは消す。大きさはレイアウトが決める。
            // 余白は字面側（_content）が持つ。中身を持たないので効かない。
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,

            // フォーカスを持たせない。入力先のフォーカスを奪わないための保険。
            IsTabStop = false,
            UseSystemFocusVisuals = false,
        };

        // 修飾キーとスペーサーはリピートさせない。遅延を実質無限にして発火させない。
        if (!definition.CanRepeat) _button.Delay = int.MaxValue;

        _button.Click += (_, e) => Click?.Invoke(this, e);

        // 押下の塗りは WinUI に任せる。縮みだけこちらで足す。
        _button.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, (_, _) =>
        {
            if (!_button.IsPressed) Released?.Invoke(this, EventArgs.Empty);

            ApplyPressScale();
            SyncMergedState();
        });

        // ホバーの追跡。WinUI の ButtonBase は IsPointerOver を公開していないため自前で持つ。
        // 結合していないキーでは何もしないので、通常のキーの挙動は変わらない。
        _button.PointerEntered += (_, _) => SetHovered(true);
        _button.PointerExited += (_, _) => SetHovered(false);
        _button.PointerCanceled += (_, _) => SetHovered(false);
        _button.PointerCaptureLost += (_, _) => SetHovered(false);

        // 結合したキーの枠線は実寸から組み立てる。レイアウトが決まるたびに引き直す。
        // 枠線のグラデーションは相手の高さも要るので、相手の分も引き直す。
        _button.SizeChanged += (_, e) =>
        {
            UpdateOutline(e.NewSize.Width, e.NewSize.Height);
            _mergePartner?.UpdateOutline(
                _mergePartner._button.ActualWidth, _mergePartner._button.ActualHeight);

            // 字の大きさはキーの高さから決める。高さが変わったら取り直す。
            UpdateFontSize();

            // 結合したキーは、両方が揃わないと中心が決まらない。
            CenterMergedFace();
            _mergePartner?.CenterMergedFace();

            // 縮める基準もその中心に合わせる。
            ApplyPressScale();

            // 端の列の押し出しは、基準のキーと自分のキーの幅で決まる。
            ApplyEdgeInset();
            NotifyEdgeFollowers();
        };

        // 字面が変われば基準の位置も動く。Shift でラベルが入れ替わる列もある。
        _faceBox.SizeChanged += (_, _) =>
        {
            ApplyEdgeInset();
            NotifyEdgeFollowers();
        };

        _root = new Grid { RenderTransform = _scale };
        _root.Children.Add(_button);
        _root.Children.Add(_content);
        Content = _root;

        if (definition.Spacer)
        {
            _button.Visibility = Visibility.Collapsed;
            _content.Visibility = Visibility.Collapsed;
            IsHitTestVisible = false;
        }

        ApplyTheme(theme);
    }

    /// <summary>長押しリピートの開始遅延と間隔（ミリ秒）を設定する。</summary>
    public void SetRepeatTiming(int delayMs, int intervalMs)
    {
        // リピート対象外のキーは遅延を無限のままにしておく。
        if (!Definition.CanRepeat) return;

        _button.Delay = delayMs;
        _button.Interval = intervalMs;
    }

    /// <summary>
    /// 上下に隣り合うキーと結合し、1 つのキーとして見せる。
    ///
    /// 幅が揃っていない場合は L 字になる。JIS の Enter は上段が左へ 0.25 ユニット
    /// 張り出すため、その段差の下辺にだけ枠線が要る。覆われている側には引かない。
    /// </summary>
    /// <param name="partner">結合する相手。押下表現を揃えるために使う。</param>
    /// <param name="exposedLeft">結合する辺のうち、相手に覆われず左に露出した割合。</param>
    /// <param name="exposedRight">同じく右に露出した割合。</param>
    public void MergeWith(KeyButton partner, double exposedLeft, double exposedRight)
    {
        _mergePartner = partner;
        _exposedLeft = exposedLeft;
        _exposedRight = exposedRight;

        // 露出した端は外側の角なので丸める。覆われている端は相手と地続きなので丸めない。
        var left = exposedLeft > 0.001 ? Radius : 0;
        var right = exposedRight > 0.001 ? Radius : 0;

        _button.CornerRadius = Definition.MergeDown
            ? new CornerRadius(Radius, Radius, right, left)
            : new CornerRadius(left, right, Radius, Radius);

        // 枠線はパスに任せる。Border に引かせると、結合部を消した辺と
        // 角丸の弧が噛み合わず継ぎ目が出る。
        _normalBorder = _latchedBorder = new Thickness(0);

        _outline = new Path { StrokeThickness = 1, IsHitTestVisible = false };
        _root.Children.Add(_outline);

        UpdateOutline(_button.ActualWidth, _button.ActualHeight);
        ApplyVisual();
    }

    private void SetHovered(bool hovered)
    {
        if (_selfHovered == hovered) return;

        _selfHovered = hovered;
        SyncMergedState();
    }

    /// <summary>
    /// 自分と相手の状態を合わせて、両方に同じ視覚状態を適用する。
    ///
    /// 結合していないキーでは何もしない。WinUI 既定の押下・ホバー表現をそのまま使う。
    /// </summary>
    private void SyncMergedState()
    {
        if (_mergePartner is null) return;

        _mergePartner._partnerPressed = _button.IsPressed;
        _mergePartner._partnerHovered = _selfHovered;

        ApplyMergedState();
        _mergePartner.ApplyMergedState();
    }

    /// <summary>
    /// どちらかが押されていれば押下、どちらかに乗っていればホバー。
    ///
    /// WinUI 自身も同じポインタ操作で状態を変えるが、その処理はこのハンドラより先に走る。
    /// したがって、ここで上書きした結合後の状態が最終的に残る。
    /// </summary>
    private void ApplyMergedState()
    {
        var state =
            _button.IsPressed || _partnerPressed ? "Pressed" :
            _selfHovered || _partnerHovered ? "PointerOver" :
            "Normal";

        VisualStateManager.GoToState(_button, state, useTransitions: true);
        ApplyPressScale();
    }

    /// <summary>
    /// 押している間はキーをわずかに縮める。標準のタッチキーボードと同じ振る舞い。
    ///
    /// 縮めるのはキー全体。塗りだけが変わるより、指で押し込んだ感じが出る。
    ///
    /// 結合したキーは、繋がった全体の中心を基準に縮める。
    /// 段ごとに自分の中心で縮めると、境目に隙間ができて 2 つに割れて見える。
    /// </summary>
    private void ApplyPressScale()
    {
        var own = _button.ActualHeight;
        var width = _button.ActualWidth;

        var originY = own > 0 ? 0.5 + (_mergeShift / own) : 0.5;
        _root.RenderTransformOrigin = new Point(0.5, originY);

        var pressed = _button.IsPressed || _partnerPressed;

        var x = 1.0;
        var y = 1.0;

        if (pressed && own > 0 && width > 0)
        {
            // 四辺とも同じ幅だけ内側へ入れる。
            // 縦横に同じ倍率を掛けると、幅の広いキーほど左右が大きく削れ、
            // Space と 1 ユニットのキーとで縮み方が揃わない。
            //
            // 基準は結合を含めた高さ。結合した両段が同じ倍率になり、境目がずれない。
            var height = own + (_mergePartner?._button.ActualHeight ?? 0);
            var inset = height * PressInset;

            x = Math.Max(0.1, (width - (inset * 2)) / width);
            y = Math.Max(0.1, (height - (inset * 2)) / height);
        }

        // 押し引きが変わっていないなら、大きさだけ合わせる。
        // 配置が変わるたびに動かすと、盤の大きさを変えている間ずっと震える。
        if (pressed == _pressShown)
        {
            _scale.ScaleX = x;
            _scale.ScaleY = y;
            return;
        }

        _pressShown = pressed;

        // 押すときは速く、戻すときは少し緩める。
        // 指の動きに遅れずに沈み、離した後に余韻が残る。
        AnimateScale(x, y, pressed ? PressDownMs : PressUpMs);
    }

    /// <summary>押している状態を反映済みか。動かすのは変わった瞬間だけ。</summary>
    private bool _pressShown;

    private Storyboard? _pressAnimation;

    private void AnimateScale(double x, double y, int milliseconds)
    {
        _pressAnimation?.Stop();

        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var board = new Storyboard();

        board.Children.Add(Track(x, "ScaleX"));
        board.Children.Add(Track(y, "ScaleY"));

        _pressAnimation = board;
        board.Begin();

        DoubleAnimation Track(double to, string property)
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = duration,

                // 変形の値は合成側では動かせない。明示して依存アニメーションにする。
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };

            Storyboard.SetTarget(animation, _scale);
            Storyboard.SetTargetProperty(animation, property);

            return animation;
        }
    }

    private static LineSegment Line(double x, double y) => new() { Point = new Point(x, y) };

    private static ArcSegment Arc(double x, double y, double r, SweepDirection direction) => new()
    {
        Point = new Point(x, y),
        Size = new Size(r, r),
        SweepDirection = direction,
    };

    /// <summary>
    /// 結合したキーの枠線を実寸から組み立てる。
    ///
    /// 相手と接する辺には線を引かない。相手からはみ出した部分（段差）にだけ引く。
    /// 露出した端の角は外側の角なので丸め、接している端は地続きなので丸めない。
    /// </summary>
    private void UpdateOutline(double w, double h)
    {
        if (_outline is null || w <= 1 || h <= 1) return;

        var r = Radius;

        // 線の中心を半分だけ内側へ寄せる。外縁をキーの縁にちょうど合わせるため。
        var t = _outline.StrokeThickness / 2;

        // 段差の位置は隣のキーとの相対で決まるので、余白を含めた枠の幅で測る。
        // 上下のキーは同じ左右余白を持つため、余白は相殺されてこの式で一致する。
        var cell = w + Gap * 2;
        var stepLeft = _exposedLeft * cell;
        var stepRight = w - _exposedRight * cell;

        var hasLeft = _exposedLeft > 0.001;
        var hasRight = _exposedRight > 0.001;

        // 相手がこちらより外へ張り出している側には入隅ができる。
        // 丸みは段差を描く側が引くので、こちらはその分だけ辺を短くして譲る。
        var notchLeft = (_mergePartner?._exposedLeft ?? 0) > 0.001 ? InnerRadius : 0;
        var notchRight = (_mergePartner?._exposedRight ?? 0) > 0.001 ? InnerRadius : 0;

        var figure = new PathFigure { IsClosed = false, IsFilled = false };
        var segments = figure.Segments;

        if (Definition.MergeDown)
        {
            // 下辺で繋がる側。段差 → 左 → 上 → 右 と時計回りに回る。
            var bottom = h - t;

            if (hasLeft)
            {
                // 入隅の丸みは下段に描かせる。行は上から順に描かれるので、
                // ここで描いても下段の塗りに塗り潰される。丸みの分だけ手前で止める。
                figure.StartPoint = new Point(stepLeft + t - InnerRadius, bottom);
                segments.Add(Line(t + r, bottom));
                segments.Add(Arc(t, bottom - r, r, SweepDirection.Clockwise));
            }
            else
            {
                // 接している端は相手の線に続く。丸めず縁まで引く。
                // 相手が外へ張り出している側は、下段が描く丸みの分だけ短くする。
                figure.StartPoint = new Point(t, notchLeft > 0 ? h - InnerRadius + t : h);
            }

            segments.Add(Line(t, t + r));
            segments.Add(Arc(t + r, t, r, SweepDirection.Clockwise));
            segments.Add(Line(w - t - r, t));
            segments.Add(Arc(w - t, t + r, r, SweepDirection.Clockwise));

            if (hasRight)
            {
                segments.Add(Line(w - t, bottom - r));
                segments.Add(Arc(w - t - r, bottom, r, SweepDirection.Clockwise));

                segments.Add(Line(stepRight - t + InnerRadius, bottom));
            }
            else
            {
                segments.Add(Line(w - t, notchRight > 0 ? h - InnerRadius + t : h));
            }
        }
        else
        {
            // 上辺で繋がる側。段差 → 左 → 下 → 右 と反時計回りに回る。
            if (hasLeft)
            {
                figure.StartPoint = new Point(stepLeft + t, t - InnerRadius);
                segments.Add(Arc(stepLeft + t - InnerRadius, t, InnerRadius,
                    SweepDirection.Clockwise));

                segments.Add(Line(t + r, t));
                segments.Add(Arc(t, t + r, r, SweepDirection.Counterclockwise));
            }
            else if (notchLeft > 0)
            {
                // 入隅。上段が段差を止めた位置から丸めて自分の左辺へ繋ぐ。
                // 自分の塗りより後に描かれるので、ここで描けば隠れない。
                figure.StartPoint = new Point(t - InnerRadius, -t);
                segments.Add(Arc(t, InnerRadius - t, InnerRadius, SweepDirection.Clockwise));
            }
            else
            {
                figure.StartPoint = new Point(t, 0);
            }

            segments.Add(Line(t, h - t - r));
            segments.Add(Arc(t + r, h - t, r, SweepDirection.Counterclockwise));
            segments.Add(Line(w - t - r, h - t));
            segments.Add(Arc(w - t, h - t - r, r, SweepDirection.Counterclockwise));

            if (hasRight)
            {
                segments.Add(Line(w - t, t + r));
                segments.Add(Arc(w - t - r, t, r, SweepDirection.Counterclockwise));

                segments.Add(Line(stepRight - t + InnerRadius, t));
                segments.Add(Arc(stepRight - t, t - InnerRadius, InnerRadius,
                    SweepDirection.Clockwise));
            }
            else if (notchRight > 0)
            {
                segments.Add(Line(w - t, InnerRadius - t));
                segments.Add(Arc(w - t + InnerRadius, -t, InnerRadius, SweepDirection.Clockwise));
            }
            else
            {
                segments.Add(Line(w - t, 0));
            }
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _outline.Data = geometry;
        _outline.Stroke = MergedBorderBrush();
    }

    /// <summary>
    /// 結合したキー全体を 1 本のグラデーションが貫くよう、自分が占める区間を切り出す。
    ///
    /// 枠線は上辺が明るく下辺が暗いグラデーションで立体感を出している。
    /// これを段ごとに 0→1 で繰り返すと、継ぎ目で暗から明へ飛んで境目が見えてしまう。
    /// </summary>
    private Brush MergedBorderBrush()
    {
        var brush = _button.BorderBrush;
        if (brush is not LinearGradientBrush source || _mergePartner is null) return brush;

        var mine = _button.ActualHeight;
        var theirs = _mergePartner._button.ActualHeight;
        var total = mine + theirs;

        // 相手がまだ配置されていない。相手の SizeChanged で引き直される。
        if (mine <= 0 || theirs <= 0) return brush;

        var from = Definition.MergeDown ? 0 : theirs / total;
        var to = Definition.MergeDown ? mine / total : 1;

        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Sample(source, from), Offset = 0 },
                new GradientStop { Color = Sample(source, to), Offset = 1 },
            },
        };
    }

    /// <summary>グラデーションの指定位置の色を線形補間で求める。</summary>
    private static Color Sample(LinearGradientBrush brush, double offset)
    {
        GradientStop? lower = null;
        GradientStop? upper = null;

        foreach (var stop in brush.GradientStops)
        {
            if (stop.Offset <= offset && (lower is null || stop.Offset >= lower.Offset)) lower = stop;
            if (stop.Offset >= offset && (upper is null || stop.Offset <= upper.Offset)) upper = stop;
        }

        if (lower is null) return upper?.Color ?? Microsoft.UI.Colors.Transparent;
        if (upper is null) return lower.Color;

        var span = upper.Offset - lower.Offset;
        var t = span <= 0 ? 0 : (offset - lower.Offset) / span;

        return Color.FromArgb(
            Lerp(lower.Color.A, upper.Color.A, t),
            Lerp(lower.Color.R, upper.Color.R, t),
            Lerp(lower.Color.G, upper.Color.G, t),
            Lerp(lower.Color.B, upper.Color.B, t));
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

    public void ApplyTheme(Theme theme)
    {
        _theme = theme;
        if (Definition.Spacer) return;

        Refresh(LatchState.Off, string.Empty);
    }

    /// <summary>
    /// 修飾キーのラッチ状態と、Fn / Shift を考慮した表示を反映する。
    /// </summary>
    /// <param name="latch">このキーが修飾キーの場合のラッチ状態。それ以外は Off を渡す。</param>
    /// <param name="label">表示するラベル。空文字なら定義のラベルを使う。</param>
    /// <param name="icon">アイコン文字。null ならラベルのみを描く。</param>
    /// <param name="onFnLayer">
    /// いま Fn 段の割り当てを出しているか。出している間は塗りを変え、
    /// 通常の段とは別のキーになっていることを見せる。
    /// </param>
    /// <param name="shiftActive">
    /// Shift が効いているか。4 隅に刻むキーで、いま打てる字を濃く出すために使う。
    /// </param>
    /// <param name="imeOpen">入力先の IME が入っているか。かなの刻印の濃さに使う。</param>
    public void Refresh(
        LatchState latch,
        string label,
        string? icon = null,
        bool onFnLayer = false,
        bool shiftActive = false,
        bool imeOpen = true)
    {
        if (Definition.Spacer) return;

        _latch = latch;
        _onFnLayer = onFnLayer;

        var text = string.IsNullOrEmpty(label) ? Definition.Label : label;

        // 4 隅に刻むキーは、押しても刻印が入れ替わらない。物理のキーと同じ。
        // 4 つとも出しているので入れ替える必要が無く、同じ字が 2 か所に並んでしまう。
        if (_quad) text = Definition.Label;

        // 結合したキーの片側は何も描かない。1 つのキーに文字が 2 つ並ばないようにする。
        //
        // 併記の判定より先に決める。判定は積んだ高さを測るために字を出すので、
        // 後から隠すつもりでいると、その字が出たまま残る。
        var hidden = Definition.HideFace;

        // アイコンがあり、ラベルが語として意味を持つときは併記できる。
        // 矢印のように記号 1 文字のラベルはアイコンと重複するので併記しない。
        // 実際に併記するかは高さ次第で、UpdateFontSize が積んでみて決める。
        //
        // Windows キーの 4 マス印字、Caps Lock キーの矢印+南京錠は、文字を持つ
        // アイコン（_icon）とは別の見た目だが、扱いは「アイコンを持つキー」と同じにする。
        var showIcon = (icon is not null || Definition.IsWindowsLogo || Definition.IsCapsLock) && !hidden;

        _pairable = showIcon && !Definition.IconOnly && IsWordLabel(text);

        var showText = !hidden && (!showIcon || _pairable);

        if (Definition.IsWindowsLogo)
        {
            _icon.Visibility = Visibility.Collapsed;
            _windowsGlyph.Visibility = showIcon ? Visibility.Visible : Visibility.Collapsed;
            _capsLockGlyph.Visibility = Visibility.Collapsed;
        }
        else if (Definition.IsCapsLock)
        {
            _icon.Visibility = Visibility.Collapsed;
            _windowsGlyph.Visibility = Visibility.Collapsed;
            _capsLockGlyph.Visibility = showIcon ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            if (showIcon)
            {
                _icon.Text = icon!;
                _icon.FontFamily = FontForGlyph(icon!);
            }

            _icon.Visibility = showIcon ? Visibility.Visible : Visibility.Collapsed;
            _windowsGlyph.Visibility = Visibility.Collapsed;
            _capsLockGlyph.Visibility = Visibility.Collapsed;
        }

        // 罫線を挟む指定なら、その行で上下に分ける。
        var (above, below) = SplitAtRule(text);

        _text.Text = above;
        _text.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;

        _textBelow.Text = below ?? string.Empty;
        _textBelow.Visibility = showText && below is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

        _rule.Visibility = _textBelow.Visibility;

        // 端の列の基準は自分のキーの中央。残りは接している端へ寄せたうえで、
        // 基準の位置まで余白で押し出す（ApplyEdgeInset）。
        // 端に接しないキーは、併記するときだけ外側へ寄せ、片方だけなら中央に置く。
        // かなを刻むキーは 4 隅に分けて置く。字は左下。
        // 物理のキーと同じ並びで、左が英数、右がかなになる。
        // 隅に刻むキーは中央。記号のキーでは組の中での中央、
        // 数字と英字ではキーの中央になる（BuildQuad が入れ物を分けている）。
        var side =
            _quad ? HorizontalAlignment.Center :
            CenterFace ? HorizontalAlignment.Center :
            IsEdgeAnchor ? HorizontalAlignment.Center :
            Edge == RowEdge.Left ? HorizontalAlignment.Left :
            Edge == RowEdge.Right ? HorizontalAlignment.Right :
            showIcon && showText
                ? (OnRightHalf ? HorizontalAlignment.Right : HorizontalAlignment.Left)
                : HorizontalAlignment.Center;

        _faceBox.HorizontalAlignment = side;

        // 記号のキーは基の字も隅（左下）に置く。それ以外は中央。
        // 枠の中での置き方は BuildQuad が決めているので、そこに合わせる。
        // ここで一律に中央へ戻すと、隅へ置いた指定が打ち消される。
        _faceBox.VerticalAlignment = _leftColumn
            ? VerticalAlignment.Bottom
            : VerticalAlignment.Center;

        _icon.HorizontalAlignment = side;
        _windowsGlyph.HorizontalAlignment = side;
        _capsLockGlyph.HorizontalAlignment = side;
        _text.HorizontalAlignment = side;
        _textBelow.HorizontalAlignment = side;

        // 灯りは字とは逆、盤の内側へ寄せる。
        // 字を外側へ寄せているので、同じ側に置くと重なる。
        var ledSide = OnRightHalf ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        // ただし左上は Shift 時の併記が使う。重なるなら反対側へ回す。
        if (_shiftHint is not null && ledSide == HorizontalAlignment.Left)
        {
            ledSide = HorizontalAlignment.Right;
        }

        _led.HorizontalAlignment = ledSide;

        _showIcon = showIcon;
        _showText = showText;

        UpdateFontSize();

        // 左上の併記を消す場面は 2 つある。
        // Shift が効いているときは主ラベルがその文字に入れ替わり、同じ字が 2 つ並ぶ。
        // Fn 段を出しているときは別のキーになっており、Shift の文字は入らない。
        //
        // 4 隅に刻むキーでは入れ替えないので、Shift 中も出したままにする。
        if (_shiftHint is not null)
        {
            var duplicated = !_quad && text == Definition.ShiftLabel;

            _shiftHint.Visibility = onFnLayer || duplicated
                ? Visibility.Collapsed
                : Visibility.Visible;

            _shiftHint.Foreground = _theme.KeyForeground;
        }

        if (_kana is not null) _kana.Foreground = _theme.KeyForeground;
        if (_kanaShift is not null) _kanaShift.Foreground = _theme.KeyForeground;

        ApplyReachable(shiftActive, imeOpen);

        ApplyVisual();
    }

    /// <summary>
    /// いま打てる刻印を濃く、打てないものを淡くする。
    ///
    /// 物理のキーは刻印が変わらないので、どれが出るかは指の側で判断している。
    /// 画面の上ではそれが分からないため、濃さで示す。
    ///
    /// 淡くするのは Shift の有無で出る字が変わるものだけ。英字のように
    /// Shift でも同じ字が出るキーは、掛けても濃いままにする。
    ///
    /// かなは IME が切なら出ないので、まとめて淡くする。入のときは
    /// ローマ字入力かかな入力かで実際に出るかが変わるが、そこは判別できない。
    /// 判別できないものは打てる側として扱う。
    /// </summary>
    private void ApplyReachable(bool shiftActive, bool imeOpen)
    {
        // 記号のキーは隅の刻印も基の字も同じ濃さで置く。大きさを揃えてあるので、
        // 濃さだけ違うと書体の太さが違うように見える。
        if (_leftColumn)
        {
            _text.Opacity = 1;

            if (_shiftHint is not null) _shiftHint.Opacity = 1;
            if (_kana is not null) _kana.Opacity = 1;
            if (_kanaShift is not null) _kanaShift.Opacity = 1;

            return;
        }

        var hasShiftLabel = Definition.ShiftLabel is { Length: > 0 }
                            && Definition.ShiftLabel != Definition.Label;

        _text.Opacity = shiftActive && hasShiftLabel ? Dimmed : 1;

        if (_shiftHint is not null) _shiftHint.Opacity = shiftActive ? 1 : Dimmed;

        if (_kana is not null)
        {
            var unreachable = !imeOpen || (shiftActive && _kanaShift is not null);
            _kana.Opacity = unreachable ? Dimmed : 1;
        }

        if (_kanaShift is not null)
        {
            _kanaShift.Opacity = imeOpen && shiftActive ? 1 : Dimmed;
        }
    }

    /// <summary>いま打てない刻印の濃さ。</summary>
    private const double Dimmed = 0.65;

    /// <summary>
    /// 塗りと枠をラッチ状態から決める。
    /// 押下とホバーは WinUI の視覚状態が上書きするため、ここでは触らない。
    /// </summary>
    private void ApplyVisual()
    {
        // ラッチとロックを視覚的に区別する（要件 F-2）。
        // 灯りで示す。ラッチ = 輪郭だけ、ロック = 塗りつぶし。
        //
        // 面ごと塗る方式はやめた。字が読みにくくなるうえ、
        // 押下の表示と紛れて、押しているのか掛かっているのか分からない。
        // Fn 段を出している間は、文字キーでも役割のキーとして塗る。
        // 押しても数字が出ないので、通常の段と同じ見た目では紛らわしい。
        _button.Background = _onFnLayer
            ? _theme.ModifierBackground
            : _theme.BackgroundFor(Definition);
        _button.BorderBrush = _theme.KeyBorder;
        _button.BorderThickness = _normalBorder;

        switch (_latch)
        {
            case LatchState.Latched:
                _led.Visibility = Visibility.Visible;
                _led.Fill = null;
                _led.Stroke = _theme.ModifierLocked;
                break;

            case LatchState.Locked:
                _led.Visibility = Visibility.Visible;
                _led.Fill = _theme.ModifierLocked;
                _led.Stroke = _theme.ModifierLocked;
                break;

            default:
                // 消灯。位置は見せる。掛かっていないことが分かればよい。
                // 灯りを持たないキーには何も出さない。丸が並ぶと意味を失う。
                _led.Visibility = Definition.IsModifier || Definition.IsCapsLock
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                _led.Fill = _theme.LedOff;
                _led.Stroke = _theme.LedOff;
                break;
        }

        _icon.Foreground = _theme.KeyForeground;
        _text.Foreground = _theme.KeyForeground;
        _textBelow.Foreground = _theme.KeyForeground;
        _rule.Fill = _theme.KeyForeground;

        foreach (var square in _windowsGlyphSquares) square.Background = _theme.KeyForeground;

        _capsLockArrow.Fill = _theme.KeyForeground;
        _capsLockLock.Fill = _theme.KeyForeground;

        // 縁取り・個別消しはキーの地色で塗り、矢印の上を覆って溶け込ませる。
        // ちょうど直前で _button.Background に入れた値と同じものを使う。
        _capsLockHalo.Fill = _button.Background;
        _capsLockPatch.Fill = _button.Background;

        // 囲みの枠。太さと余白は字の大きさから決まるので UpdateFontSize が入れる。
        _faceBox.BorderBrush = Definition.BoxedLabel ? _theme.KeyForeground : null;

        // 結合したキーは Border ではなくパスが枠線を担う。太さは常に同じ。
        if (_outline is not null)
        {
            _outline.StrokeThickness = 1;
            UpdateOutline(_button.ActualWidth, _button.ActualHeight);
        }
    }

    // 字の大きさは決め打ちの値を基本とし、ウィンドウが小さいときだけ割合で抑える。
    //
    // 普段は同じ大きさで揃っていてほしい。高さを変えるたびに字まで変わると、
    // 見慣れた大きさが定まらない。
    // ただし小さくしたときに決め打ちのままだと、字がキーからはみ出す。

    /// <summary>文字を出すキーの字。文字数によらず同じ大きさにする。</summary>
    private const double LabelSize = 20;
    private const double LabelRatio = 0.065;

    /// <summary>
    /// アイコン。キーの種類によらずこの大きさで描く。
    /// 字より少し大きくする。細い線で描かれるぶん、同じ寸法では小さく見える。
    /// </summary>
    private const double IconSize = 22;
    private const double IconRatio = LabelRatio * IconSize / LabelSize;

    /// <summary>
    /// 文字を出さないキーの字。
    ///
    /// Backspace や 無変換 のように役割の名前が入るぶん字数が多い。
    /// 文字キーと同じ大きさにすると幅を取り、狭いキーで詰まって見える。
    /// </summary>
    private const double CommandSize = 16;
    private const double CommandRatio = LabelRatio * CommandSize / LabelSize;

    /// <summary>
    /// 左上に添える Shift 時の文字。
    ///
    /// 4 隅に刻むキー（クラシック）では中央の字と同じ大きさを使うので、
    /// ここが効くのは 1 つだけ添えるキー。モダンの記号がそれにあたる。
    /// </summary>
    private const double HintSize = 13;
    private const double HintRatio = 0.042;

    /// <summary>
    /// キー自身の高さに対する上限。
    ///
    /// 割合の基準はウィンドウだが、段によって高さが違う。
    /// ファンクション段は半分の高さで、ウィンドウ基準の大きさでは収まらない。
    /// 見た目を決めるためではなく、はみ出させないために掛ける。
    /// </summary>
    private const double MaxKeyRatio = 0.5;

    /// <summary>字とアイコンの間隔。字の大きさに対する割合。</summary>
    private const double FaceGapRatio = 0.3;

    /// <summary>複数行のラベルの行送り。字の大きさに対する割合。</summary>
    private const double LineRatio = 1.15;

    /// <summary>罫線と字の間隔。詰めきると字に触れるので最小限だけ残す。</summary>
    private const double RuleGap = 1;

    /// <summary>囲みの枠の一辺。字の大きさに対する割合。</summary>
    private const double BoxSideRatio = 1.5;


    /// <summary>字の行数。</summary>
    private static int Lines(string text) => text.Count(c => c == '\n') + 1;

    /// <summary>いま面に積んでいる字の行数。罫線は薄いので数えない。</summary>
    private int LineCount()
    {
        if (!_showText) return 0;

        var rows = Lines(_text.Text);

        if (_textBelow.Visibility == Visibility.Visible) rows += Lines(_textBelow.Text);

        return rows;
    }

    /// <summary>いま面に出しているもの。大きさを取り直すときに要る。</summary>
    private bool _showIcon;
    private bool _showText;

    /// <summary>アイコンとラベルを積む余地があれば積むキーか。</summary>
    private bool _pairable;

    /// <summary>
    /// 字を幅にも収める。
    ///
    /// 大きさは高さから決めているので、幅の狭いキーでは字数の多いラベルが
    /// 縁からはみ出す。「無変換」のように全角 3 文字が 1 ユニットに入る配列では、
    /// 盤を細くしたときに必ず起きる。
    ///
    /// 縮むのは収まらないキーだけで、他のキーの大きさは変えない。
    /// 幅は大きさに比例するので、一度測れば必要な倍率が出る。
    /// </summary>
    private double FitWidth(double size, double keyWidth)
    {
        var room = keyWidth - (PadX * 2);
        if (room <= 0 || size <= 0) return size;

        _text.FontSize = size;
        _textBelow.FontSize = size;

        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _textBelow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var width = Math.Max(_text.DesiredSize.Width, _textBelow.DesiredSize.Width);
        if (width <= room || width <= 0) return size;

        return size * (room / width);
    }

    /// <summary>
    /// 4 隅の刻印を幅に収める。
    ///
    /// 隅は左右に 2 つ並ぶので、1 つぶんの幅で決めた大きさでは足りない。
    /// 収まらないときは 4 つとも同じ割合で縮める。片方だけ縮めると、
    /// 同じ大きさで刻むという前提が崩れる。
    /// </summary>
    private void FitCorners(double keyWidth)
    {
        var room = keyWidth - (PadX * 2);
        var size = _text.FontSize;

        if (room <= 0 || size <= 0) return;

        var top = Width(_shiftHint) + Width(_kanaShift);
        var bottom = Width(_text) + Width(_kana);

        // 左右の間を少し空ける。詰めると 1 つの語のように読める。
        var need = Math.Max(top, bottom) + (size * CornerGapRatio);
        if (need <= room) return;

        var scaled = size * (room / need);

        _text.FontSize = scaled;
        if (_shiftHint is not null) _shiftHint.FontSize = scaled;
        if (_kana is not null) _kana.FontSize = scaled;
        if (_kanaShift is not null) _kanaShift.FontSize = scaled;

        static double Width(TextBlock? block)
        {
            if (block is null || block.Visibility != Visibility.Visible) return 0;

            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return block.DesiredSize.Width;
        }
    }

    /// <summary>4 隅の左右の間に空ける幅。字の大きさに対する割合。</summary>
    private const double CornerGapRatio = 0.4;

    /// <summary>
    /// 字とアイコンを積めるかを決める。積めなければ字を落とす。
    ///
    /// 判定は素の大きさで積んで測る。実際に使う大きさは、狭いキーでは
    /// キーの高さの半分まで縮む。その縮んだ大きさで測ると、どんなに小さくても
    /// 収まる計算になり、いつまでも併記のままになる。
    ///
    /// 縮めてでも 2 つ載せるより、アイコン 1 つを読める大きさで出すほうがよい。
    ///
    /// 字形ごとに高さが違うので、寸法の計算では決めない。
    /// 測るときだけ字を出す。隠したまま測ると、常に収まると出てしまう。
    /// </summary>
    private void UpdatePairing(double key, double basis)
    {
        _text.Visibility = Visibility.Visible;
        _text.FontSize = basis;
        _icon.FontSize = IconSize;

        // Windows キーは常に IconOnly（併記しない）なのでここには来ない。
        // Caps Lock キーは「Caps」と併記するので、測るときの大きさも要る。
        if (Definition.IsCapsLock) SizeCapsLockGlyph(IconSize);

        _faceBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // 縁との間に余白を残せることまで求める。文字を出さないキーの上下の余白は
        // 0 にしてあるが、積んだ印字が縁いっぱいまで来ると、キーが字で埋まって見える。
        var room = key - ((PadY + PairPad) * 2);

        var fits = _faceBox.DesiredSize.Height <= room;

        _text.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
        _showText = fits;
    }

    /// <summary>
    /// 字面の大きさを決める。
    ///
    /// ラベルは文字数によらず同じ大きさにする。文字数ごとに変えると、
    /// 隣り合うキーで字の大きさが違って見え、並びが落ち着かない。
    /// </summary>
    private void UpdateFontSize()
    {
        var key = _button.ActualHeight;

        // まだ配置されていない。SizeChanged で取り直される。
        if (key <= 0) return;

        // 割合の基準はウィンドウの高さ。取れないうちはキーの高さで代用する。
        var window = _button.XamlRoot?.Size.Height ?? 0;
        if (window <= 0) window = key;

        // 字は文字を出すかどうかで基準を変える。アイコンは変えない。
        // 絵は字数を持たないので小さくする理由が無く、縮めると読み取りにくい。
        //
        // Fn 段を出している間は、文字キーでも役割のキーとして扱う。
        // 出しているのは F1〜F12 のような役割の名前で、押しても文字は出ない。
        // 塗りをそちらに寄せているので、字の大きさも揃える。
        var asCharacter = Definition.IsCharacter && !_onFnLayer;

        var basis = asCharacter ? LabelSize : CommandSize;
        var ratio = asCharacter ? LabelRatio : CommandRatio;

        var label = Fit(basis, ratio, window, key);

        // 複数行のラベルは、積んだ高さがキーに収まるところまで小さくする。
        // 1 行と同じ大きさのままでは、行数ぶんそのまま溢れる。
        var rows = LineCount();
        if (rows > 1)
        {
            // 罫線とその前後の隙間もわずかに高さを取る。そのぶんを見込む。
            var stack = rows * LineRatio + (_rule.Visibility == Visibility.Visible ? 0.3 : 0);
            label = Math.Min(label, (key - PadY * 2) / stack);
        }

        // 幅にも収める。狭いキーでは字数の多いラベルが縁からはみ出す。
        // 4 隅に刻むキーは、隅ごと揃えて縮める FitCorners が後で見る。
        if (_showText && !_quad) label = FitWidth(label, _button.ActualWidth);

        // 併記できるかどうかを先に決める。字の大きさはその後で入れる。
        if (_pairable) UpdatePairing(key, basis);

        if (_showIcon)
        {
            var size = Fit(IconSize, IconRatio, window, key);
            _icon.FontSize = size;
            if (Definition.IsWindowsLogo) SizeWindowsGlyph(size);
            if (Definition.IsCapsLock) SizeCapsLockGlyph(size);
        }

        if (_showText)
        {
            _text.FontSize = label;
            _textBelow.FontSize = label;

            // 行送りを指定するのは、自分が 2 行以上ある側だけにする。
            //
            // 行送りを決めた行は、その高さの中で下端に置かれる。余ったぶんは
            // 字の上に付く。1 行しかない側に指定すると、その余りが罫線との間に
            // 空いて見える。1 行なら外形に詰める指定（Tight）に任せる。
            _text.LineHeight = Lines(_text.Text) > 1 ? label * LineRatio : 0;
            _textBelow.LineHeight = Lines(_textBelow.Text) > 1 ? label * LineRatio : 0;

            _text.LineStackingStrategy = _textBelow.LineStackingStrategy =
                LineStackingStrategy.BlockLineHeight;
        }

        // 字とアイコンの間隔。行の高さを外形まで詰めた分、間が空かなくなるため
        // ここで持たせる。字の大きさに追随させ、小さい盤でも比を保つ。
        var gap = Math.Round(label * FaceGapRatio);
        _face.Spacing = gap;

        // 罫線だけは間隔を打ち消す。区切りは字に寄っているほうが 1 枚に見える。
        // StackPanel の間隔は一律なので、負の余白で削る。
        _rule.Margin = new Thickness(0, RuleGap - gap, 0, RuleGap - gap);
        _rule.Height = Math.Max(1, Math.Round(label / 16));

        // 囲みの枠。字の大きさに合わせる。
        //
        // 縦横を同じにして真四角にする。字の外形に合わせて囲うと、
        // 「A」と「あ」で枠の形が変わり、並べたときに揃わない。
        if (Definition.BoxedLabel)
        {
            _faceBox.BorderThickness = new Thickness(Math.Max(1, Math.Round(label / 14)));

            var side = Math.Round(label * BoxSideRatio);
            _faceBox.Width = side;
            _faceBox.Height = side;
            _faceBox.CornerRadius = new CornerRadius(side * 0.15);
        }

        // 字形ごとのずれを実測値で補う。定義が持つ割合を字の大きさに掛ける。
        // 積んでいるときと積んでいないときで、余白の出方が違うぶん値も違う。
        var offset = _showIcon && _showText
            ? Definition.PairedFaceOffset ?? Definition.FaceOffset
            : Definition.FaceOffset;

        _faceShift = label * offset;
        ApplyFaceOffset();

        // 物理のキーに倣って刻むキーは、隅も基の字と同じ大きさにする。
        // 実機の写真では = と -、+ と ;、{ と [ がいずれも同じ大きさで、
        // Shift 側だけを小さく刻んだキーは無かった。
        //
        // 物理のキーに倣わない配列（モダン）は、主たる字を邪魔しない小ささにする。
        var corner = _quad ? label : Fit(HintSize, HintRatio, window, key);

        if (_shiftHint is not null) _shiftHint.FontSize = corner;
        if (_kana is not null) _kana.FontSize = corner;
        if (_kanaShift is not null) _kanaShift.FontSize = corner;

        if (_quad)
        {
            // 隅は左右に 2 つ並ぶ。基の字と同じ大きさなので、幅に収まらなければ
            // まとめて縮める。大きさが決まってから位置を出す。
            FitCorners(_button.ActualWidth);

            // 刻印を、同じ高さの帯の中央に置く。
            //
            // 字によって墨の位置が違う。行送りを揃えて基準線で並べると、
            // * のように em の上のほうに描かれる字が浮いて見える。
            // 実測では、同じ段の + の中心より 9px 上にあった。
            //
            // 帯の高さは行送りぶん。墨の高さを測って上下に振り分ける。
            // 上端に貼る刻印は上の余白が、下端に貼る刻印は下の余白が効く。
            var band = corner * LineRatio;

            InkCenter(_shiftHint, band);
            InkCenter(_kanaShift, band);
            InkCenter(_kana, band);

            // 記号のキーは基の字も記号なので同じ扱いにする。
            // 数字と英字は基準線で並べたほうが読みやすいので触らない。
            if (_leftColumn) InkCenter(_text, band);

            static void InkCenter(TextBlock? block, double band)
            {
                if (block is null || block.Visibility != Visibility.Visible) return;

                // 外形に詰めると、箱の高さがそのまま墨の高さになる。
                // 同梱のフォントで英数も和文も描くので、字種によらず効く。
                block.TextLineBounds = TextLineBounds.Tight;
                block.LineHeight = 0;
                block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                block.Margin = new Thickness(0);

                block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                var room = Math.Max(0, (band - block.DesiredSize.Height) / 2);
                block.Margin = new Thickness(0, room, 0, room);
            }
        }
    }

    /// <summary>
    /// 決め打ちの大きさ。ただしウィンドウが小さいときは割合で抑える。
    /// 縮める側にだけ効かせる。大きくしたときに字まで大きくはしない。
    /// </summary>
    private static double Fit(double size, double ratio, double window, double key) =>
        Math.Min(Math.Min(size, window * ratio), key * MaxKeyRatio);

    /// <summary>
    /// 結合したキーの字面を、繋がった全体の中央へ寄せる。
    ///
    /// 字面は自分の段の中で中央に置かれる。L 字の Enter では片方の段にしか
    /// 字が無いため、そのままだと繋がった形の中では片寄って見える。
    /// 相手の段の高さの半分だけ寄せると、繋がった全体の中央に来る。
    ///
    /// 字を持たせるのは下段にする。寄せた字面は相手の段まではみ出すが、
    /// 行は上から順に描かれるため、上段に持たせるとはみ出した部分が
    /// 下段の塗りに隠れる。
    ///
    /// 余白では動かさない。中央寄せの要素に余白を足すと、その分だけ
    /// 置ける高さが減り、相手と同じ高さを足した時点で行き場が無くなって消える。
    /// 描く位置だけをずらす。
    /// </summary>
    private void CenterMergedFace()
    {
        if (_mergePartner is null) return;

        var partner = _mergePartner._button.ActualHeight;
        if (partner <= 0) return;

        // 相手が下にいるなら下へ、上にいるなら上へ、相手の半分だけ寄せる。
        _mergeShift = Definition.MergeDown ? partner / 2 : -partner / 2;
        ApplyFaceOffset();
    }

    /// <summary>結合したぶん下げる量。</summary>
    private double _mergeShift;

    /// <summary>字形のずれを補うぶん。正が下。</summary>
    private double _faceShift;

    /// <summary>字面を描く位置。ずらす要因をまとめて反映する。</summary>
    private void ApplyFaceOffset()
    {
        var y = _mergeShift + _faceShift;

        _faceBox.RenderTransform = y == 0 ? null : new TranslateTransform { Y = y };
    }

    /// <summary>
    /// 同じ列の基準のキーに字の位置を合わせる。
    /// </summary>
    public void FollowEdge(KeyButton anchor)
    {
        if (ReferenceEquals(anchor, this)) return;

        _edgeAnchor = anchor;
        anchor._edgeFollowers.Add(this);

        ApplyEdgeInset();
    }

    private void NotifyEdgeFollowers()
    {
        foreach (var follower in _edgeFollowers) follower.ApplyEdgeInset();
    }

    /// <summary>
    /// 基準のキーで字が始まる位置まで、端から余白で押し出す。
    ///
    /// 端の列はキーの幅がばらつく。同じだけ端から離せば字は縦に揃い、
    /// 基準のキーでは中央に来る。
    /// </summary>
    private void ApplyEdgeInset()
    {
        if (_edgeAnchor is null || Edge == RowEdge.None) return;

        var anchorKey = _edgeAnchor._button.ActualWidth;
        var anchorFace = _edgeAnchor.NaturalFaceWidth();
        var key = _button.ActualWidth;

        // まだ配置されていない。どちらかの SizeChanged で取り直される。
        if (anchorKey <= 0 || key <= 0) return;

        var inset = (anchorKey - anchorFace) / 2;

        // 反対側の余白は割り込ませない。狭いキーでは基準まで押し出せない。
        // 揃わなくなるが、字がキーからはみ出すよりはよい。
        inset = Math.Max(0, Math.Min(inset, key - PadX - NaturalFaceWidth()));

        // 字面はすでに PadX だけ内側にある。足りない分だけを余白で足す。
        var extra = Math.Max(0, inset - PadX);

        var margin = Edge == RowEdge.Left
            ? new Thickness(extra, 0, 0, 0)
            : new Thickness(0, 0, extra, 0);

        // 余白を入れると配置が走り、またここへ戻ってくる。値が同じなら止める。
        if (Math.Abs(margin.Left - _faceBox.Margin.Left) < 0.01 &&
            Math.Abs(margin.Right - _faceBox.Margin.Right) < 0.01)
        {
            return;
        }

        _faceBox.Margin = margin;
    }

    /// <summary>
    /// 字面の素の幅。
    ///
    /// 実寸では測らない。余白で押し出したぶん置ける幅が減り、
    /// 縮んだ実寸から余白を計算し直すと、際限なく縮んでいく。
    /// </summary>
    private double NaturalFaceWidth()
    {
        _faceBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        return Math.Max(0, _faceBox.DesiredSize.Width - _faceBox.Margin.Left - _faceBox.Margin.Right);
    }
}
