using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using TouchKeyboard.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TouchKeyboard.Views;

/// <summary>
/// クリップボードの履歴。
///
/// Win+V を送るのではなく自前で並べる。あちらは標準タッチキーボードの上に出る前提の
/// 位置に開き、こちらのキーボードとは重なり方が合わない。
/// 履歴そのものは OS が持っているものをそのまま読む。自前で溜め込むと二重になる。
///
/// ピン留めだけは自前で持つ。OS の履歴には読み書きする API が無い。
/// </summary>
public sealed partial class KeyboardWindow
{
    /// <summary>履歴 1 件の中身の種類。</summary>
    private enum ClipboardKind
    {
        Text,
        Image,
        Files,

        /// <summary>読めるが中身を示せないもの。書式の名前だけ出す。</summary>
        Other,
    }

    /// <summary>
    /// 並べた 1 件分。押されたときに内容へ戻すために元の項目も持つ。
    /// </summary>
    /// <param name="Label">札に出す文字。文字の項目では本文そのもの。</param>
    /// <param name="Item">OS の履歴にある実体。溢れて消えたピン留めでは null。</param>
    /// <param name="Image">画像の項目の中身。それ以外では null。</param>
    private sealed record ClipboardEntry(
        ClipboardKind Kind,
        string Label,
        ClipboardHistoryItem? Item,
        bool Pinned,
        RandomAccessStreamReference? Image);

    private const string PinGlyph = "";
    private const string UnpinGlyph = "";
    private const string DeleteGlyph = "";

    /// <summary>画像の札の高さの上限。並びが 1 件で埋まらない程度に留める。</summary>
    private const double ImagePreviewHeight = 120;

    /// <summary>札の角の丸み。帯を札の下へ潜り込ませる量にも使う。</summary>
    private const double CardCornerDip = 6;

    /// <summary>
    /// いま帯を出したままにしている札を閉じる手続き。開いていなければ null。
    ///
    /// 同時に開くのは 1 枚だけにする。何枚も開いたままだと、
    /// どれに対する操作なのか分からなくなる。
    /// </summary>
    private Action? _openSwipe;

    private void PrepareClipboard()
    {
        // 幅は本体の 5 ユニットぶん。狭すぎると何が入っているか読めない。
        var unit = KeyRoot.ActualWidth / (_layout?.RowUnits ?? 15.5);
        ClipboardPanel.Width = (unit * 5) + (PanelInset * 2);

        ClipboardTitle.Foreground = _theme.SecondaryForeground;

        _ = LoadClipboardAsync();
    }

    /// <summary>
    /// 履歴を読み込む。
    ///
    /// 取得は非同期で、失敗することがある。履歴が無効な場合と、OS が
    /// アクセスを認めない場合があり、いずれも例外ではなく状態として返る。
    ///
    /// ピン留めは履歴の有無にかかわらず先に並べる。取得に失敗しても
    /// 留めたものは使えるようにしておく。
    /// </summary>
    private async System.Threading.Tasks.Task LoadClipboardAsync()
    {
        var pins = _settings.ClipboardPins.ToList();
        var history = await ReadHistoryAsync();

        // 取得できず、留めたものも無いなら理由を出して終わり。
        if (history is null && pins.Count == 0) return;

        var entries = new List<ClipboardEntry>();

        // 留めたものは文字だけ。画像やファイルは中身を持ち出せないため、
        // 履歴から溢れた時点で復元できない。
        foreach (var text in pins)
        {
            var found = history?.FirstOrDefault(
                entry => entry.Kind == ClipboardKind.Text && entry.Label == text);

            entries.Add(new ClipboardEntry(
                ClipboardKind.Text, text, found?.Item, Pinned: true, Image: null));
        }

        if (history is not null)
        {
            foreach (var entry in history)
            {
                if (entry.Kind == ClipboardKind.Text && pins.Contains(entry.Label)) continue;
                entries.Add(entry);
            }
        }

        if (entries.Count == 0)
        {
            ShowClipboardMessage("履歴がありません。");
            return;
        }

        // 組み直すので、開いたままの札の記憶も捨てる。
        _openSwipe = null;
        ClipboardList.Children.Clear();

        foreach (var entry in entries)
        {
            ClipboardList.Children.Add(BuildCard(entry));
        }
    }

    /// <summary>OS の履歴を読む。取得できなければ理由を出して null。</summary>
    private async System.Threading.Tasks.Task<List<ClipboardEntry>?> ReadHistoryAsync()
    {
        ClipboardHistoryItemsResult result;
        try
        {
            result = await Clipboard.GetHistoryItemsAsync();
        }
        catch (Exception ex)
        {
            ShowClipboardMessage($"履歴を取得できません: {ex.Message}");
            return null;
        }

        if (result.Status != ClipboardHistoryItemsResultStatus.Success)
        {
            ShowClipboardMessage(result.Status == ClipboardHistoryItemsResultStatus.ClipboardHistoryDisabled
                ? "クリップボードの履歴が無効です。Windows の設定で有効にしてください。"
                : $"履歴を取得できません（{result.Status}）");
            return null;
        }

        var entries = new List<ClipboardEntry>();

        foreach (var item in result.Items)
        {
            var entry = await ReadEntryAsync(item);
            if (entry is not null) entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// 履歴の 1 件を読む。読めなければ null。
    ///
    /// 文字を先に見る。書式を複数持つ項目が多く、文字で表せるならそれがいちばん短い。
    /// 1 件が読めなくても一覧ごと失わないよう、例外はここで止める。
    /// </summary>
    private static async System.Threading.Tasks.Task<ClipboardEntry?> ReadEntryAsync(
        ClipboardHistoryItem item)
    {
        try
        {
            var content = item.Content;

            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text)) return null;

                return new ClipboardEntry(ClipboardKind.Text, text, item, false, null);
            }

            if (content.Contains(StandardDataFormats.Bitmap))
            {
                var image = await content.GetBitmapAsync();
                return new ClipboardEntry(ClipboardKind.Image, "画像", item, false, image);
            }

            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var files = await content.GetStorageItemsAsync();
                var names = string.Join("\n", files.Select(NameOf));

                return new ClipboardEntry(
                    ClipboardKind.Files,
                    names.Length > 0 ? names : "ファイル",
                    item, false, null);
            }

            // 中身は示せないが、貼り付けることはできる。何が入っているかだけ出す。
            var formats = string.Join(", ", content.AvailableFormats);

            return new ClipboardEntry(
                ClipboardKind.Other,
                formats.Length > 0 ? formats : "その他のデータ",
                item, false, null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string NameOf(IStorageItem item) =>
        string.IsNullOrEmpty(item.Name) ? item.Path : item.Name;

    // ------------------------------------------------------------------
    // 1 件分の見た目
    // ------------------------------------------------------------------

    /// <summary>
    /// 札の幅に対してここまで払ったら実行する。
    ///
    /// 固定の距離にしない。札の幅はパネルの広さで変わるため、
    /// 同じ距離でも払った割合が変わってしまう。
    /// </summary>
    private const double SwipeCommitRatio = 0.5;

    /// <summary>帯の幅がまだ測れていないときに使う値（DIP）。</summary>
    private const double SwipeFallbackWidthDip = 96;

    /// <summary>
    /// 帯が出きったところで踏みとどまる量（DIP）。
    ///
    /// ここで一度動きが止まり、何が起きるかを読む間ができる。
    /// さらに押し込めばまた動き出す。止めたままにすると帯が伸びず、
    /// どこまで来たのか分からなくなる。
    /// </summary>
    private const double SwipeDetentDip = 40;


    /// <summary>
    /// 札 1 枚を組む。
    ///
    /// 左から払うとピン留め、右から払うと削除。
    ///
    /// <c>SwipeControl</c> は使わない。一覧の縦スクロールや、この窓の
    /// 押下の扱いと取り合いになり、反応が安定しなかった。
    /// 横方向の動きだけを自分で受け、札をずらして下の操作を見せる。
    /// </summary>
    private UIElement BuildCard(ClipboardEntry entry)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(BuildPreview(entry));

        // 留めたものには印を出す。並び順だけでは、上にあるのが新しいのか
        // 留めたものなのか分からない。
        if (entry.Pinned)
        {
            var mark = new TextBlock
            {
                Text = PinGlyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = _theme.SecondaryForeground,
            };

            Grid.SetColumn(mark, 1);
            content.Children.Add(mark);
        }

        var slide = new TranslateTransform();

        var card = new Border
        {
            Child = content,

            // 下の帯を隠すため、必ず塗りを持たせる。
            // 帯の上に載っているように見せるには、透けないことが前提になる。
            Background = _theme.BackgroundFor(new Layout.KeyDefinition()),
            BorderBrush = _theme.KeyBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(CardCornerDip),
            Padding = new Thickness(12, 10, 12, 10),
            RenderTransform = slide,

            // 横方向は自分で受け、縦は一覧のスクロールに渡す。
            //
            // System を混ぜないと縦に送れない。TranslateX だけを指定した時点で
            // その札が触れられている間の操作をすべて引き受けてしまい、
            // 一覧はスクロールしなくなる。System は「残りは既定の動きに任せる」指定で、
            // 一覧の横スクロールは止めてあるので、横はこちらに残る。
            ManipulationMode = ManipulationModes.TranslateX
                               | ManipulationModes.TranslateInertia
                               | ManipulationModes.System,
        };

        card.Tapped += (_, e) =>
        {
            e.Handled = true;

            // 帯を出したままなら、まず閉じる。
            // 開いている札を押して貼り付いてしまうと、消したつもりの操作と食い違う。
            if (_openSwipe is not null)
            {
                _openSwipe();
                return;
            }

            Paste(entry);
        };

        // ピン留めは文字だけ。画像やファイルは中身を持ち出せず、
        // 履歴から溢れた時点で復元できないので、留めても約束を守れない。
        var canPin = entry.Kind == ClipboardKind.Text;

        var pin = BuildAction(
            entry.Pinned ? UnpinGlyph : PinGlyph,
            entry.Pinned ? "ピン留めを外す" : "ピン留め",
            _theme.ModifierLocked,
            HorizontalAlignment.Left);

        var remove = BuildAction(
            DeleteGlyph, "削除", DeleteBrush, HorizontalAlignment.Right);

        // 開いたままの帯は押しても実行できるようにする。
        // 出したのに触れないと、いったん閉じて払い直すことになる。
        pin.Tapped += (_, e) => { e.Handled = true; TogglePin(entry); };
        remove.Tapped += (_, e) => { e.Handled = true; DeleteEntry(entry); };

        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        if (canPin) row.Children.Add(pin);
        row.Children.Add(remove);
        row.Children.Add(card);

        WireSwipe(card, slide, pin, remove, canPin, entry);

        return row;
    }

    /// <summary>削除の地の色。取り消せない操作なので、他と混ぜない。</summary>
    private static readonly Brush DeleteBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));

    /// <summary>
    /// 札の下に敷く操作の見出し。払った先に何が起きるかを示す。
    ///
    /// 幅は払った量に合わせて伸ばす。中身は外側の辺に寄せておき、
    /// 帯が短いうちは札の下に隠れ、伸びるにつれて現れる。
    /// </summary>
    private static Border BuildAction(
        string glyph, string label, Brush background, HorizontalAlignment side)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = side,
            Margin = new Thickness(16, 0, 16, 0),
        };

        stack.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 角を丸めるのは外側だけ。内側を丸めると、札の隣に別の札が
        // 並んでいるように見える。角を落とすと札の下へ続いて見える。
        var radius = side == HorizontalAlignment.Left
            ? new CornerRadius(CardCornerDip, 0, 0, CardCornerDip)
            : new CornerRadius(0, CardCornerDip, CardCornerDip, 0);

        return new Border
        {
            Child = stack,
            Background = background,
            CornerRadius = radius,
            HorizontalAlignment = side,
            Width = 0,
        };
    }

    /// <summary>
    /// 横方向の動きを受けて札をずらす。
    ///
    /// 帯の中身が読める幅まで出たところで一度止まる。何が起きるかを読む間になる。
    /// さらに押し込めばまた動き出し、札の端まで払える。
    ///
    /// 離したときの行き先は 3 通り。
    ///   ・読める幅に届いていない  … 戻す
    ///   ・読める幅を超えている    … その位置で開いたまま留める
    ///   ・半分を超えている        … 実行する
    ///
    /// 端まで払いきった場合は、離すのを待たずに実行する。
    /// </summary>
    private void WireSwipe(
        FrameworkElement card, TranslateTransform slide,
        Border pin, Border remove, bool canPin, ClipboardEntry entry)
    {
        // 自分を閉じる手続き。開いている札を見分けるために実体を 1 つに固定する。
        // 毎回 Reset を渡すと別物になり、同じ札かどうか比べられない。
        Action closeSelf = null!;

        // 指の動きの総量。
        var pushed = 0.0;

        // 押し込みで実行済みか。離すまでに二度走らせない。
        var done = false;

        // 帯の中身が読みきれる幅。掴んだ時点で測る。
        var pinWidth = SwipeFallbackWidthDip;
        var removeWidth = SwipeFallbackWidthDip;

        closeSelf = CloseAnimated;

        // 札の幅。払える上限であり、実行に至る位置の基準でもある。
        var width = SwipeFallbackWidthDip;
        var commit = SwipeFallbackWidthDip;

        card.ManipulationStarted += (_, e) =>
        {
            // 別の札が開いたままなら、ここで閉じる。開き終わってからではなく
            // 触れた時点で閉じないと、2 枚が開いた状態が見えてしまう。
            if (_openSwipe is not null && !ReferenceEquals(_openSwipe, closeSelf))
            {
                _openSwipe();
                _openSwipe = null;
            }

            // 開いたままの位置から続ける。0 に戻すと、触れた瞬間に閉じてしまう。
            pushed = slide.X;
            done = false;

            pinWidth = ContentWidthOf(pin);
            removeWidth = ContentWidthOf(remove);

            width = card.ActualWidth > 0 ? card.ActualWidth : SwipeFallbackWidthDip;
            commit = width * SwipeCommitRatio;

            e.Handled = true;
        };

        card.ManipulationDelta += (_, e) =>
        {
            e.Handled = true;
            if (done) return;

            pushed += e.Delta.Translation.X;

            // 留められない札は右へ動かさない。動く先に何も無い。
            if (!canPin && pushed > 0) pushed = 0;

            // 帯が読める幅まで出たところで一度止まり、押し込めばまた動き出す。
            // 端まで払えるが、それ以上は動かない。
            slide.X = pushed >= 0
                ? Math.Min(Detent(pushed, canPin ? pinWidth : 0), canPin ? width : 0)
                : -Math.Min(Detent(-pushed, removeWidth), width);

            // 帯は札と同じだけ伸ばし、さらに角の丸みぶん潜り込ませる。
            // ちょうどで止めると、札の丸い角の外側に地の色が三角に覗く。
            //
            // 中身は外側の辺に寄せてあるので、短いうちは札の下に隠れ、
            // 伸びるにつれて現れる。
            pin.Width = Reveal(slide.X);
            remove.Width = Reveal(-slide.X);

            // 端まで払いきった。離すのを待たずに実行する。
            if (canPin && slide.X >= width)
            {
                done = true;
                Reset();
                TogglePin(entry);
                return;
            }

            if (slide.X <= -width)
            {
                done = true;
                Reset();
                DeleteEntry(entry);
            }
        };

        card.ManipulationCompleted += (_, e) =>
        {
            e.Handled = true;
            if (done) return;

            var moved = slide.X;

            // 半分まで来ていれば実行する。
            if (canPin && moved >= commit)
            {
                Reset();
                TogglePin(entry);
                return;
            }

            if (moved <= -commit)
            {
                Reset();
                DeleteEntry(entry);
                return;
            }

            // 帯が読める幅まで出ているなら、開いたまま留める。
            // ここで閉じると、読んで手を離した瞬間に消えることになる。
            if (canPin && moved >= pinWidth)
            {
                Latch(pinWidth);
                return;
            }

            if (moved <= -removeWidth)
            {
                Latch(-removeWidth);
                return;
            }

            Reset();
        };

        void Reset()
        {
            pushed = 0;
            slide.X = 0;
            pin.Width = 0;
            remove.Width = 0;

            if (ReferenceEquals(_openSwipe, closeSelf)) _openSwipe = null;
        }

        // 開いたまま留める。開いている札は常に 1 枚で、
        // 前のものは触れた時点で閉じてある。
        void Latch(double at)
        {
            pushed = at;
            slide.X = at;
            pin.Width = Reveal(at);
            remove.Width = Reveal(-at);

            _openSwipe = closeSelf;
        }

        // 動きを見せて閉じる。
        //
        // 消えるのではなく戻ったことが分かるようにする。別の札を開いたときは
        // そちらへ指が向いているので、瞬時に消えると何が起きたか掴めない。
        void CloseAnimated()
        {
            if (slide.X == 0)
            {
                Reset();
                return;
            }

            var duration = new Duration(TimeSpan.FromMilliseconds(150));
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var story = new Storyboard();

            var offset = new DoubleAnimation
            {
                To = 0,
                Duration = duration,
                EasingFunction = easing,
            };

            Storyboard.SetTarget(offset, slide);
            Storyboard.SetTargetProperty(offset, "X");
            story.Children.Add(offset);

            AddWidth(pin);
            AddWidth(remove);

            // 途中の値が残らないよう、終わりに確定させる。
            story.Completed += (_, _) => Reset();
            story.Begin();

            void AddWidth(Border strip)
            {
                if (strip.Width <= 0) return;

                var width = new DoubleAnimation
                {
                    To = 0,
                    Duration = duration,
                    EasingFunction = easing,

                    // 幅は配置に関わるため、明示しないと動かない。
                    EnableDependentAnimation = true,
                };

                Storyboard.SetTarget(width, strip);
                Storyboard.SetTargetProperty(width, "Width");
                story.Children.Add(width);
            }
        }

        // 見えている量に、札の角へ潜り込ませるぶんを足した幅。
        // 出ていないときは 0。わずかでも幅を持たせると縁が覗く。
        static double Reveal(double shown) =>
            shown > 0 ? shown + CardCornerDip : 0;

        // 帯が出きるまではそのまま。そこで踏みとどまり、押し込めばまた進む。
        static double Detent(double amount, double width)
        {
            if (width <= 0) return 0;
            if (amount <= width) return amount;
            if (amount <= width + SwipeDetentDip) return width;

            return amount - SwipeDetentDip;
        }
    }

    /// <summary>
    /// 帯の中身がちょうど収まる幅。
    ///
    /// 帯そのものは払った量に合わせて伸び縮みするので、その幅は基準にならない。
    /// 中に入れた字を測る。
    /// </summary>
    private static double ContentWidthOf(Border strip)
    {
        if (strip.Child is not FrameworkElement content) return SwipeFallbackWidthDip;

        content.Measure(new Windows.Foundation.Size(
            double.PositiveInfinity, double.PositiveInfinity));

        var width = content.DesiredSize.Width;
        return width > 0 ? width : SwipeFallbackWidthDip;
    }

    /// <summary>札の中身。種類ごとに見せ方を変える。</summary>
    private UIElement BuildPreview(ClipboardEntry entry)
    {
        if (entry.Kind == ClipboardKind.Image && entry.Image is not null)
        {
            return BuildImagePreview(entry.Image);
        }

        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };

        // 文字以外は、何が入っているかを添える。本文だけでは判断が付かない。
        if (entry.Kind != ClipboardKind.Text)
        {
            stack.Children.Add(new TextBlock
            {
                Text = entry.Kind == ClipboardKind.Files ? "ファイル" : "データ",
                FontSize = 11,
                Foreground = _theme.SecondaryForeground,
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = entry.Label,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 4,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 13,
        });

        return stack;
    }

    /// <summary>
    /// 画像を出す。
    ///
    /// 読み込みは非同期。札を先に返し、届いた時点で差し込む。
    /// 待ってから並べると、画像が 1 枚あるだけで一覧全体が出てこない。
    /// </summary>
    private static UIElement BuildImagePreview(RandomAccessStreamReference reference)
    {
        var image = new Image
        {
            MaxHeight = ImagePreviewHeight,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _ = LoadImageAsync(image, reference);

        return image;
    }

    private static async System.Threading.Tasks.Task LoadImageAsync(
        Image target, RandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            target.Source = bitmap;
        }
        catch (Exception)
        {
            // 出せないだけで、貼り付けはできる。札は空のまま残す。
        }
    }

    // ------------------------------------------------------------------
    // 操作
    // ------------------------------------------------------------------

    /// <summary>
    /// ピン留めを付け外しする。
    ///
    /// 内容そのものを覚える。OS の履歴の項目には、次に開いたときも同じものだと
    /// 言い切れる目印が無い。
    /// </summary>
    private void TogglePin(ClipboardEntry entry)
    {
        if (entry.Pinned) _settings.ClipboardPins.Remove(entry.Label);
        else _settings.ClipboardPins.Insert(0, entry.Label);

        _settings.Save();
        _ = LoadClipboardAsync();
    }

    /// <summary>
    /// 履歴から消す。留めてあれば留めも外す。
    ///
    /// 留めたまま履歴だけ消すと、消したはずのものが残って見える。
    /// </summary>
    private void DeleteEntry(ClipboardEntry entry)
    {
        _settings.ClipboardPins.Remove(entry.Label);
        _settings.Save();

        try
        {
            if (entry.Item is not null) Clipboard.DeleteItemFromHistory(entry.Item);
        }
        catch (Exception ex)
        {
            Log($"履歴から消せませんでした: {ex.Message}", isError: true);
        }

        _ = LoadClipboardAsync();
    }

    private void ShowClipboardMessage(string message)
    {
        ClipboardList.Children.Clear();

        ClipboardList.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            IsHitTestVisible = false,
        });
    }

    /// <summary>
    /// 選ばれた内容を貼り付ける。
    ///
    /// 履歴の項目を現在の内容に戻してから Ctrl+V を送る。文字を 1 つずつ
    /// 送出する方式は採らない。改行や長文で崩れるうえ、IME を経由して変換されてしまう。
    /// 画像やファイルもこの経路なら書式ごと戻る。
    /// </summary>
    private void Paste(ClipboardEntry entry)
    {
        try
        {
            if (!PutOnClipboard(entry)) return;

            ClosePanel();

            // 内容が行き渡るまでわずかに待つ。直後に送ると前の内容が貼られる。
            DispatcherQueue.TryEnqueue(() =>
                KeySender.TapWithModifiers(
                    0x2F, extended: false, stackalloc[] { KeyEvent.Down(0x1D) }));
        }
        catch (Exception ex)
        {
            Log($"貼り付けに失敗: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// 内容をクリップボードに載せる。
    ///
    /// 履歴に残っていればその項目を戻す。書式をそのまま扱えるのはこちらだけ。
    /// 留めた文字が履歴から溢れている場合に限り、文字として置き直す。
    /// </summary>
    private bool PutOnClipboard(ClipboardEntry entry)
    {
        if (entry.Item is not null)
        {
            if (Clipboard.SetHistoryItemAsContent(entry.Item) == SetHistoryItemAsContentStatus.Success)
            {
                return true;
            }

            Log("クリップボードに戻せませんでした", isError: true);
            return false;
        }

        var package = new DataPackage();
        package.SetText(entry.Label);

        Clipboard.SetContent(package);
        Clipboard.Flush();

        return true;
    }
}
