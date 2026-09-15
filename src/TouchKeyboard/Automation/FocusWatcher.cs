using System;
using System.Collections.Concurrent;
using System.Windows.Automation;
using Microsoft.UI.Dispatching;
using TouchKeyboard.Diagnostics;
using TouchKeyboard.Interop;
using TouchKeyboard.Settings;

namespace TouchKeyboard.Automation;

/// <summary>
/// フォーカスの移動を監視し、入力欄に入ったらキーボードの表示を求める。
///
/// 判定の取りこぼしは前提。単一の条件では拾えないコントロールが必ずあるため、
/// 種別とパターンの両方を見たうえで、それでも漏れるものは手動表示に委ねる。
/// </summary>
public sealed class FocusWatcher : IDisposable
{
    /// <summary>
    /// 隠すまでの猶予。
    ///
    /// 隠すたびに ABM_REMOVE を通すため、フォーカスが短時間に動くと
    /// 全アプリの再レイアウトが連発する。表示は即時、非表示だけ遅らせる。
    ///
    /// 短くしすぎるとこのデバウンスが効かなくなる。実機で体感と再レイアウトの
    /// 連発具合を見ながら調整すること。
    /// </summary>
    private const int HideDelayMs = 100;

    /// <summary>
    /// タップを受けてから、フォーカスを確かめ直すまでの待ち。
    ///
    /// 押下の時点ではアプリがまだタップを処理していない。そこで表示すると、
    /// AppBar が作業領域を狭めた結果、指の下でウィンドウがリサイズされ、
    /// タップの行き先が変わってフォーカスが外れる。
    /// 相手が処理を終えるのを待ってから確かめる。
    /// </summary>
    private const int ReevaluateDelayMs = 150;

    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _reevaluateTimer;

    /// <summary>タップの行き先を確かめるための待ち。<see cref="NotifyTouched"/> から動かす。</summary>
    private readonly DispatcherQueueTimer _deliberateTimer;

    /// <summary>自分のウィンドウへの移動は無視する。</summary>
    private readonly int _ownProcessId = Environment.ProcessId;

    /// <summary>最後に入力欄を持っていた前面ウィンドウ。補助ウィンドウを見分けるために持つ。</summary>
    private nint _lastInputWindow;

    /// <summary>
    /// 直近に入力欄と認めた時刻。
    ///
    /// Omnibox のように、打つたびに同じ入力欄へ焦点変更を発火し直すアプリがある。
    /// そのため打っている間ずっと進み続ける。「一覧へ触らずに移った」判定
    /// （時間の窓を持たない）の基準に使う。表示直後のレイアウトのゆれの判定には
    /// 使わない。そちらは <see cref="_dockSettledAt"/> を見る。
    /// </summary>
    private long _shownAt;

    /// <summary>
    /// 実際にドッキングし直した時刻。自分が起こした揺れを見分けるために持つ。
    ///
    /// <see cref="_shownAt"/> と違い、キーボードが既に表示されている間の
    /// フォーカス移動では進めない。既に出ている状態への ShowRequested は
    /// AppBar を触らず Z 順を取り直すだけで、作業領域も相手のレイアウトも
    /// 動かないため、ゆれは起きない。ここを <see cref="_shownAt"/> と同じにすると、
    /// 打ち続けている間ずっと「表示直後」の猶予が伸び続け、打ち終えた直後に
    /// 起きる本物のフォーカス移動（ページ遷移など）まで自分のせいと誤認して
    /// 隠さなくなる。
    /// </summary>
    private long _dockSettledAt;

    /// <summary>
    /// 隠した直後、自分が起こしたレイアウト変化が収まるまでの猶予。
    ///
    /// 表示中と対称の問題が非表示側にもある。AppBar を外すと確保していた分だけ
    /// 他アプリの作業領域が広がり、相手が再レイアウトされる。その拍子に入力欄が
    /// フォーカスを取り直すと、監視側には「入力欄に来た」としか見えず、
    /// 隠した直後にすぐ出し直してしまう（スワイプで隠しても消えない不具合になる）。
    /// </summary>
    private const int SettleAfterHideMs = 500;

    /// <summary>実際に隠れた時刻。0 なら該当なし。</summary>
    private long _hiddenAt;

    /// <summary>
    /// 手動で隠したとき、フォーカスがあった要素そのもの。null なら該当なし。
    ///
    /// <see cref="SettleAfterHideMs"/> は再レイアウトのゆれを吸収する短い猶予に
    /// すぎず、隠したあとに画面へ触れ続ける操作（スクロールなど）には対応できない
    /// （フォーカスは動いていないので、猶予を過ぎたとたん「入力欄にいる」という
    /// 事実だけで出し直してしまう）。時間ではなく要素そのもので判定し、
    /// 本物のフォーカス変更（別の要素へ移る）か、まさにこの要素を触り直すまで、
    /// パッシブな再評価では出し直さないようにする。
    /// </summary>
    private AutomationElement? _manuallyHiddenElement;

    /// <summary>
    /// 対象外のアプリの入力欄にフォーカスが移った時刻。0 なら該当なし。
    /// あとから利用者が意図的に触ったかを、この時刻との前後で判断する。
    /// </summary>
    private long _excludedInputAt;

    /// <summary>対象外のアプリの入力欄を持っていたプロセス。</summary>
    private int _excludedProcessId;

    /// <summary>
    /// 対象外のアプリの入力欄の位置。物理ピクセル。
    /// 触られた場所がこの中かどうかで、その入力欄を押したのかを判断する。
    /// </summary>
    private System.Windows.Rect _excludedInputBounds;

    /// <summary>
    /// 意図的に出したときの相手プロセス。0 なら該当なし。
    ///
    /// このプロセスの間は除外を解く。押した結果さらに別の要素へフォーカスが移る
    /// アプリがあり（スタートメニューは検索パネルへ移る）、除外したままだと
    /// 出した直後に消えてしまう。相手から離れた時点で元に戻す。
    /// </summary>
    private int _deliberateProcessId;

    /// <summary>
    /// 対象外のアプリでも、この時間より後に触られたなら意図的な操作とみなす。
    ///
    /// スタートメニューは、スタートボタンを押した「あと」に検索ボックスへ
    /// フォーカスが移る。つまりタッチが先でフォーカスが後。
    /// 検索ボックスを自分から押した場合はフォーカスが先でタッチが後になる。
    /// 指を離すまでの分を見込んで待つ。
    /// </summary>
    private const int DeliberateTouchDelayMs = 400;

    private AutomationFocusChangedEventHandler? _handler;

    /// <summary>入力欄にフォーカスが入った。</summary>
    public event EventHandler? ShowRequested;

    /// <summary>入力欄から外れた。猶予を置いてから発火する。</summary>
    public event EventHandler? HideRequested;

    /// <summary>監視の開始や解除に失敗した。UI 側でログに出す。</summary>
    public event EventHandler<Exception>? Failed;

    public bool IsRunning => _handler is not null;

    /// <summary>
    /// 最後にポインタが操作された時刻を返す。App が差し込む。
    ///
    /// 表示直後のフォーカス移動が、自分が押しのけたせいなのか、利用者が
    /// 触ったせいなのかを見分けるために使う。触っていないなら自分のせい。
    /// </summary>
    public Func<long>? LastPointerInputAt { get; set; }

    /// <summary>
    /// いまキーボードが表示中か返す。App が差し込む。
    ///
    /// 入力欄への焦点変更を受けて表示を求めるとき、既に出ている場合は
    /// AppBar も相手のレイアウトも動かない。動くのは、今から新しく出す
    /// ときだけ。表示直後のレイアウトのゆれの猶予（<see cref="_dockSettledAt"/>）を
    /// 取り直すかどうかの判断に使う。
    /// </summary>
    public Func<bool>? IsKeyboardShown { get; set; }

    /// <summary>
    /// 実行ファイルのパスから、そのアプリの自動表示の扱いを返す。App が差し込む。
    /// 未登録のアプリは呼ばれた側で登録される。
    /// </summary>
    public Func<string, AutoShowPolicy>? PolicyFor { get; set; }

    public FocusWatcher(DispatcherQueue queue)
    {
        _queue = queue;

        _hideTimer = queue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(HideDelayMs);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            TraceLog.Write("  -> 非表示を実行");
            HideRequested?.Invoke(this, EventArgs.Empty);
        };

        _reevaluateTimer = queue.CreateTimer();
        _reevaluateTimer.Interval = TimeSpan.FromMilliseconds(ReevaluateDelayMs);
        _reevaluateTimer.IsRepeating = false;
        _reevaluateTimer.Tick += (_, _) => Reevaluate();

        _deliberateTimer = queue.CreateTimer();
        _deliberateTimer.Interval = TimeSpan.FromMilliseconds(ReevaluateDelayMs);
        _deliberateTimer.IsRepeating = false;
        _deliberateTimer.Tick += (_, _) => ConfirmDeliberate();
    }

    /// <summary>
    /// 少し待ってからフォーカスを確かめ直す。
    ///
    /// タッチへの持ち替えのように、相手のアプリがまだ処理を終えていない時点で
    /// 呼ばれる経路で使う。連続して呼ばれた場合は最後の 1 回だけが効く。
    /// </summary>
    /// <summary>
    /// タッチがあったときに呼ぶ。UI スレッドから。
    ///
    /// 対象外のアプリの入力欄でも、フォーカスが移ってから十分あとに触られたなら、
    /// 利用者が自分で押したものとみなして表示する。
    /// スタートメニューのように、開いた勢いでフォーカスが移るものとは順序が逆になる。
    /// </summary>
    /// <param name="point">
    /// 触られた画面上の位置。物理ピクセル。読めなかった場合は null。
    /// </param>
    public void NotifyTouched((int X, int Y)? point = null)
    {
        if (!IsRunning) return;

        // 手動で隠した、まさにその入力欄を触り直したなら、隠した意思より
        // いまの操作を優先する。触れた位置はここでしか分からないため、
        // Reevaluate 側の要素比較とは別に、ここで位置を突き合わせて判断する。
        if (TryShowManuallyHiddenTouched(point)) return;

        if (_excludedInputAt == 0) return;
        if (Environment.TickCount64 - _excludedInputAt < DeliberateTouchDelayMs) return;

        // 触り続けている間に何度も走らせない。次の判定の基準を今に進める。
        _excludedInputAt = Environment.TickCount64;

        // 位置が読めたなら、入力欄そのものを押したかで判断する。
        // 入力欄にフォーカスがあるだけで、押したのが別の場所ということがある。
        if (point is { } touched && !_excludedInputBounds.IsEmpty)
        {
            if (_excludedInputBounds.Contains(touched.X, touched.Y))
            {
                Show("対象外のアプリだが入力欄を自分で触った", _excludedProcessId);
            }
            else
            {
                TraceLog.Write(
                    $"  -> 表示せず（触った先が入力欄の外 {touched.X},{touched.Y}）");
            }

            return;
        }

        // 位置が読めない場合の備え。押下の時点では相手がまだタップを処理しておらず、
        // フォーカスの行き先も決まっていない。少し待ってから確かめる。
        _deliberateTimer.Stop();
        _deliberateTimer.Start();
    }

    /// <summary>
    /// 手動で隠した要素を、まさにその位置で触り直したかを見る。
    /// 該当すれば表示して抑制を解き、true を返す。
    /// </summary>
    private bool TryShowManuallyHiddenTouched((int X, int Y)? point)
    {
        if (_manuallyHiddenElement is null || point is not { } touched) return false;

        var bounds = BoundsOf(_manuallyHiddenElement);
        if (bounds.IsEmpty || !bounds.Contains(touched.X, touched.Y)) return false;

        int processId;

        try
        {
            processId = _manuallyHiddenElement.Current.ProcessId;
        }
        catch (ElementNotAvailableException)
        {
            _manuallyHiddenElement = null;
            return false;
        }

        _manuallyHiddenElement = null;
        Show("手動で隠した入力欄を自分で触り直した", processId);
        return true;
    }

    /// <summary>入力欄の位置。取れなければ空。</summary>
    private static System.Windows.Rect BoundsOf(AutomationElement element)
    {
        try
        {
            return element.Current.BoundingRectangle;
        }
        catch (ElementNotAvailableException)
        {
            return System.Windows.Rect.Empty;
        }
    }

    /// <summary>手動で隠したときの要素と同一か。UI Automation の要素同一性で比べる。</summary>
    private bool IsManuallyHiddenElement(AutomationElement element)
    {
        if (_manuallyHiddenElement is null) return false;

        try
        {
            return System.Windows.Automation.Automation.Compare(element, _manuallyHiddenElement);
        }
        catch (Exception)
        {
            // 比較できないなら別物として扱う。抑制を残し続けるより安全。
            return false;
        }
    }

    /// <summary>意図的に触られたものとして表示する。</summary>
    private void Show(string reason, int processId)
    {
        // このアプリの中にいる間は除外を解く。押した結果さらに別の要素へ
        // フォーカスが移っても、出した直後に消えないようにするため。
        _deliberateProcessId = processId;

        TraceLog.Write($"  -> 表示（{reason}）");

        _hideTimer.Stop();

        if (IsKeyboardShown?.Invoke() != true) _dockSettledAt = Environment.TickCount64;

        _lastInputWindow = WindowStyles.ForegroundWindow();
        _shownAt = Environment.TickCount64;
        ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// タップの行き先を確かめて、入力欄なら表示する。
    ///
    /// 触っただけで出していたときは、入力欄にフォーカスがある状態で
    /// 別の場所を押しても出てしまっていた。フォーカスが入力欄に残っているか、
    /// あるいは入力欄へ移ったときだけ出す。
    /// </summary>
    private void ConfirmDeliberate()
    {
        if (!IsRunning || _excludedProcessId == 0) return;

        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null) return;

            var processId = element.Current.ProcessId;
            if (processId != _excludedProcessId) return;

            if (!LooksLikeTextInput(element))
            {
                TraceLog.Write("  -> 表示せず（触った先が入力欄ではない）");
                return;
            }

            Show("対象外のアプリだが入力欄にフォーカスが残った", processId);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex);
        }
    }

    public void ReevaluateSoon()
    {
        if (!IsRunning) return;

        _reevaluateTimer.Stop();
        _reevaluateTimer.Start();
    }

    /// <summary>
    /// キーボードが実際に隠れた。
    /// </summary>
    /// <param name="manual">
    /// 利用者が自分の意思で隠したか（スワイプ、閉じるボタンなど）。
    /// 自動での非表示（フォーカスが外れた等）では false を渡す。
    ///
    /// true のときは、いまフォーカスがある要素を覚えておく。以後その要素の
    /// ままでは、本物のフォーカス変更か、まさにその要素を触り直すまで
    /// パッシブな再評価では出し直さない（<see cref="Apply"/> 側で判定する）。
    /// </param>
    public void NotifyHidden(bool manual)
    {
        _hiddenAt = Environment.TickCount64;

        // 「意図的に出した」扱いは今の表示中だけのもの。隠れた時点で終わりにする。
        //
        // これを残したままだと、厳密ポリシーのアプリを一度でも自分で触って
        // 出したあと、閉じてもそのアプリの中にいる限りずっと厳密の確認
        // （NotifyTouched 経由の実タッチ）を素通りしてしまう。実機の trace.log
        // で確認した例：OneNote の入力欄を触って表示 → スワイプで閉じる →
        // ペンで書く → 消しゴム側を近づけると、OneNote 自身が内部的に
        // ContentOutline へフォーカスを動かしており（触れてすらいない）、
        // 「意図的」がまだ残っていたせいでそのまま表示し直されていた
        // （利用者の報告：「OneNote で消しゴムボタンをホバーするだけで
        // キーボードが表示される」）。
        _deliberateProcessId = 0;

        if (!manual) return;

        try
        {
            var element = AutomationElement.FocusedElement;
            _manuallyHiddenElement = element?.Current.ProcessId == _ownProcessId ? null : element;
        }
        catch (Exception)
        {
            // 取れなくても実害は無い。この一件だけ抑制の対象から漏れる。
            _manuallyHiddenElement = null;
        }
    }

    /// <summary>自分の UI を開いている間か。開いている間は隠す判断をしない。</summary>
    private bool _suspended;

    /// <summary>
    /// 隠す判断を止める／再開する。
    ///
    /// 自前のメニューやフライアウトは別ウィンドウとして開くことがあり、
    /// そのとき相手のアプリはフォーカスを失う。戻ってきたときの移動先が
    /// 入力欄とは限らず、入力をやめたものとして扱われて引っ込んでしまう。
    ///
    /// 自分が起こした移動なので従わない。
    /// </summary>
    public void SuspendHiding(bool suspended)
    {
        _suspended = suspended;

        if (suspended) _hideTimer.Stop();
    }

    public void Start()
    {
        if (IsRunning) return;

        try
        {
            var handler = new AutomationFocusChangedEventHandler(OnFocusChanged);
            System.Windows.Automation.Automation.AddAutomationFocusChangedEventHandler(handler);

            // 登録できてから覚える。失敗したものを解除しに行かないため。
            _handler = handler;
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex);
        }
    }

    public void Stop()
    {
        _hideTimer.Stop();
        _reevaluateTimer.Stop();
        _deliberateTimer.Stop();

        if (_handler is null) return;

        try
        {
            System.Windows.Automation.Automation.RemoveAutomationFocusChangedEventHandler(_handler);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex);
        }
        finally
        {
            _handler = null;
        }
    }

    /// <summary>
    /// いま何にフォーカスがあるかを調べ直し、表示・非表示を求め直す。
    ///
    /// フォーカスが動かないまま状況が変わったときに使う。キーボードを外した瞬間などは
    /// フォーカス移動が起きないため、これを呼ばないと入力欄に居るのに出てこない。
    /// </summary>
    public void Reevaluate()
    {
        if (!IsRunning) return;

        var window = WindowStyles.ForegroundWindow();

        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null) return;
            if (element.Current.ProcessId == _ownProcessId) return;

            Apply(Classify(element), window, element, Describe(element));
        }
        catch (Exception)
        {
            // 取れないことがある。次のフォーカス移動で拾い直す。
        }
    }

    /// <summary>フォーカスの移動先をどう扱うか。</summary>
    private enum FocusKind
    {
        /// <summary>入力欄。表示する。</summary>
        Input,

        /// <summary>一覧やメニュー。補完候補の受け皿になるため、単独では判断しない。</summary>
        Selection,

        /// <summary>それ以外。入力をやめたとみなす。</summary>
        Other,
    }

    private FocusKind Classify(AutomationElement element)
    {
        // 対象外のアプリは入力欄であっても出さない。入力をやめたのと同じ扱いにして、
        // 出ているキーボードは引っ込める。
        var processId = element.Current.ProcessId;

        // 意図的に出した相手から離れたら、その扱いは終わり。
        if (_deliberateProcessId != 0 && processId != _deliberateProcessId) _deliberateProcessId = 0;

        var policy = PolicyOf(processId);

        // 抑制。手動表示のみ。出ているものは引っ込める。
        if (policy == AutoShowPolicy.Suppress)
        {
            _excludedInputAt = 0;
            return FocusKind.Other;
        }

        // 厳密。アプリが自分から移したフォーカスでは出さない。
        // 入力欄だったことは覚えておき、あとから利用者が触ったときに出す。
        // いちど意図的に出した相手は、そこを離れるまで通常どおり扱う。
        if (policy == AutoShowPolicy.Strict && processId != _deliberateProcessId)
        {
            var input = LooksLikeTextInput(element);

            _excludedInputAt = input ? Environment.TickCount64 : 0;
            _excludedProcessId = processId;

            // 入力欄の位置も覚えておく。あとで触られた場所と突き合わせる。
            _excludedInputBounds = input ? BoundsOf(element) : System.Windows.Rect.Empty;

            return FocusKind.Other;
        }

        _excludedInputAt = 0;

        if (LooksLikeTextInput(element)) return FocusKind.Input;

        var type = element.Current.ControlType;

        return type == ControlType.List
            || type == ControlType.ListItem
            || type == ControlType.Menu
            || type == ControlType.MenuItem
            || type == ControlType.Tree
            || type == ControlType.TreeItem
            || type == ControlType.DataItem
            ? FocusKind.Selection
            : FocusKind.Other;
    }

    /// <summary>
    /// UI Automation のスレッドで呼ばれる。
    ///
    /// ここでの処理が重いと、監視対象アプリのフォーカス移動そのものが詰まる。
    /// 判定だけを行い、UI の操作はディスパッチャへ渡す。
    /// </summary>
    private void OnFocusChanged(object? sender, AutomationFocusChangedEventArgs e)
    {
        FocusKind kind;
        string detail;
        AutomationElement element;

        // 要素からはウィンドウハンドルが取れないことが多い（Edge は常に 0 を返す）。
        // 前面ウィンドウなら確実に取れるので、そちらで所有関係を見る。
        var window = WindowStyles.ForegroundWindow();

        try
        {
            if (sender is not AutomationElement focused) return;

            // 自分のキーは WS_EX_NOACTIVATE でフォーカスを奪わないが、念のため弾く。
            if (focused.Current.ProcessId == _ownProcessId) return;

            element = focused;
            kind = Classify(focused);
            detail = Describe(focused);
        }
        catch (ElementNotAvailableException)
        {
            // 判定している間に対象が消えた。次のフォーカス移動で拾い直す。
            return;
        }
        catch (Exception)
        {
            // 監視は best-effort。1 回の失敗で監視ごと止めない。
            return;
        }

        _queue.TryEnqueue(() => Apply(kind, window, element, detail));
    }

    /// <summary>何が入力欄として通ったかを追えるようにするための説明。</summary>
    private static string Describe(AutomationElement element)
    {
        try
        {
            var c = element.Current;
            return $"type={c.ControlType.ProgrammaticName.Replace("ControlType.", "")} "
                + $"class=\"{c.ClassName}\" "
                + $"text={HasPattern(element, AutomationElement.IsTextPatternAvailableProperty)} "
                + $"readOnly={IsReadOnly(element)} "
                + $"kbFocusable={c.IsKeyboardFocusable}";
        }
        catch (Exception)
        {
            return "(取得できず)";
        }
    }

    private void Apply(FocusKind kind, nint window, AutomationElement element, string detail = "")
    {
        TraceLog.Write(
            $"focus  kind={kind,-9} window=0x{window:X} owner=0x{WindowStyles.RootOwner(window):X} "
            + $"lastInput=0x{_lastInputWindow:X}  {detail}");

        // 手動で隠した、まさにその要素のままなら出し直さない。
        //
        // フォーカスは動いていないので、これは UI Automation の本物の通知ではなく
        // ReevaluateSoon 経由のパッシブな再評価（Reevaluate 参照）。時間で区切ると、
        // 隠したあとに画面へ触れ続ける操作（スクロールなど）で猶予を過ぎたとたん
        // 出し直してしまう。本物のフォーカス変更が一度でも起きるか、まさにこの要素を
        // 触り直す（NotifyTouched 側）まで、要素そのもので抑制する。
        if (kind == FocusKind.Input && IsManuallyHiddenElement(element))
        {
            TraceLog.Write("  -> 中立（手動で隠した入力欄のまま）");
            return;
        }

        // ここまで来た以上、手動で隠した要素そのものではない。以後の抑制は不要。
        _manuallyHiddenElement = null;

        if (kind == FocusKind.Input)
        {
            _hideTimer.Stop();

            // 隠した直後、自分が広げた作業領域への再レイアウトで入力欄が
            // フォーカスを取り直しただけかもしれない。表示中の re-fire には関係なく、
            // これから新たに出す場合だけを見る。触ってから来たものなら本人の操作。
            //
            // ここを時間で区切らず「触れるまで中立」にしたことがあったが、
            // タッチパネルに一度も触れていないまま物理キーボードやマウスだけで
            // 操作しているとき、以後ずっと入力欄への焦点変更が拾えなくなる
            // 退行を起こした（LastPointerInputAt がタッチ・ペンの生入力しか
            // 見ていないため）。短い猶予に留める。
            //
            // 当初はここで OneNote の「消しゴムボタンにペンを近づけただけで
            // キーボードが表示される」不具合も抑え込もうとしたが、実際の原因は
            // 別にあった（TouchDigitizer.Touching 参照。消しゴム側のホバーが
            // 接触と誤認されていた）。そちらを直したので、ここは本来の
            // 再レイアウトのゆれを吸収する短い猶予のためだけに戻す。
            if (IsKeyboardShown?.Invoke() != true
                && Environment.TickCount64 - _hiddenAt < SettleAfterHideMs
                && (LastPointerInputAt?.Invoke() ?? 0) <= _hiddenAt)
            {
                TraceLog.Write("  -> 中立（非表示直後の再レイアウト）");
                return;
            }

            // 既に出ているなら、これは Omnibox のような焦点変更の re-fire。
            // AppBar も相手のレイアウトも動かさないので、ゆれの猶予は取り直さない。
            if (IsKeyboardShown?.Invoke() != true) _dockSettledAt = Environment.TickCount64;

            _lastInputWindow = window;
            _shownAt = Environment.TickCount64;
            ShowRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // 自分の UI を開いている間の移動は自分が起こしたもの。従わない。
        if (_suspended)
        {
            TraceLog.Write("  -> 中立（自分の UI を開いている）");
            return;
        }

        var stillInSameInput = window == _lastInputWindow
            || IsAuxiliaryOf(window, _lastInputWindow);

        // 補完候補が出ただけで隠さない。
        //
        // Edge は候補を、呼び出し元に所有された別ウィンドウとして出す。実測では
        // アドレスバーへの入力中に所有された WS_POPUP へフォーカスが移り、
        // その中身が読み取り専用の Document として見えていた。
        //
        // 一覧やメニューへの移動も候補の類とみなして中立にしていたが、これは外した。
        // エクスプローラーで入力をやめて一覧へ移っても隠れなくなるうえ、実測では
        // 一覧から入力欄へ戻るまで 1.5 秒あり、候補のちらつきではなく利用者の操作だった。
        if (IsAuxiliaryOf(window, _lastInputWindow))
        {
            TraceLog.Write("  -> 中立（所有された補助ウィンドウ）");
            return;
        }

        // 候補を同じウィンドウの中に出すアプリもある。Edge のアドレスバーがそれで、
        // 打っている間フォーカスは候補の一覧に移ったまま留まる。
        //
        // 一覧への移動を一律に中立とすると、エクスプローラーで入力をやめて
        // 一覧へ移っても隠れなくなる。触ったかどうかで分ける。
        // 入力欄に来てから画面を触っていないなら、移したのはアプリ自身であり、
        // 利用者は打ち続けている。触ってから移ったなら本人が一覧を選びに行った操作。
        if (kind == FocusKind.Selection
            && stillInSameInput
            && (LastPointerInputAt?.Invoke() ?? 0) <= _shownAt)
        {
            TraceLog.Write("  -> 中立（触らずに候補一覧へ移った）");
            return;
        }

        // ドッキングし直した後、同じウィンドウの中で起きた移動で、利用者がその後
        // 一度も画面を触っていないものは、利用者本人の操作ではない。
        // 押しのけたのが自分（表示直後の再レイアウト）なのか、相手のアプリ内部の
        // 動きなのかは問わない。時間で区切ると取りこぼす。
        //
        // 実機の trace.log で確認した例：バックグラウンドで再生中の動画の
        // コントロールバーが自動的に隠れるアニメーション（YouTube の autohide 等）が、
        // その要素へのフォーカス移動として UI Automation に見えており、表示から
        // 800ms 以上経ってから起きるとここで拾えず、何も触っていないのに
        // キーボードが引っ込んでいた（利用者の報告：「入力欄に触れて表示した後に
        // 勝手に非表示にされてしまいます」）。時間の窓をやめ、上の候補一覧の判定
        // （<see cref="FocusKind.Selection"/>）と同じく、触れるまでは一貫して
        // 中立に扱うようにした。
        //
        // 基準は _dockSettledAt。_shownAt を使うと、Omnibox のように打つたびに
        // 焦点変更を re-fire するアプリでは打っている間ずっと基準が進み続け、
        // 打ち終えた直後に起きる本物の移動（ページ遷移など）まで巻き込んでしまう。
        if (stillInSameInput && (LastPointerInputAt?.Invoke() ?? 0) <= _dockSettledAt)
        {
            TraceLog.Write("  -> 中立（画面に触れていない）");
            return;
        }

        // 猶予の間に入力欄へ戻れば取り消される。
        TraceLog.Write($"  -> {HideDelayMs}ms 後に非表示を予約");
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>
    /// <paramref name="window"/> が <paramref name="owner"/> に所有された補助ウィンドウか。
    ///
    /// 同じウィンドウそのものは対象外。同一ウィンドウ内で入力欄から別の要素へ移った
    /// 場合は、本当に入力をやめたとみなして隠す。
    /// </summary>
    private static bool IsAuxiliaryOf(nint window, nint owner)
    {
        if (window == 0 || owner == 0 || window == owner) return false;

        return WindowStyles.RootOwner(window) == owner;
    }

    /// <summary>
    /// 入力欄とみなせるか。
    ///
    /// 種別だけでは取りこぼす。Web ページやカスタムコントロールは
    /// 種別が Custom などになっていても、テキストパターンには対応している。
    /// </summary>
    private static bool LooksLikeTextInput(AutomationElement element)
    {
        var current = element.Current;
        if (!current.IsEnabled) return false;

        // 読み取り専用は種別によらず除外する。
        //
        // ここを種別より後に置いてはいけない。Edge の Web ページはメモ帳の編集領域と
        // 同じ ControlType.Document で、テキストパターンにも対応する。両者を分けている
        // のは読み取り専用かどうかだけで、これを先に見ないとブラウザで閲覧している間
        // ずっとキーボードが出たままになる。
        if (IsReadOnly(element)) return false;

        var type = current.ControlType;

        // 素直な入力欄。ほとんどのアプリはここで拾える。
        if (type == ControlType.Edit) return true;

        // 種別が Document や Custom でも、キーボードで入力できてテキストとして
        // 扱えるなら入力欄とみなす。
        //
        // Document を無条件に通してはいけない。Discord は本体のドキュメントを
        // Document かつ読み取り専用でないものとして申告するため、入力欄以外を
        // 触っただけで出てしまう。実測では次のように分かれた。
        //
        //   メモ帳の編集領域 : Document  text=True   kbFocusable=True
        //   Discord の本体   : Document  text=False  kbFocusable=False
        if (current.IsKeyboardFocusable
            && HasPattern(element, AutomationElement.IsTextPatternAvailableProperty))
        {
            return true;
        }

        // 編集できるコンボボックス。値パターンだけを持つ場合がある。
        return type == ControlType.ComboBox
            && HasPattern(element, AutomationElement.IsValuePatternAvailableProperty);
    }

    /// <summary>実行ファイルのパスを引くのは重いので覚えておく。監視スレッドから呼ばれる。</summary>
    private readonly ConcurrentDictionary<int, string> _processPaths = new();

    private AutoShowPolicy PolicyOf(int processId)
    {
        if (PolicyFor is null) return AutoShowPolicy.Auto;

        var path = _processPaths.GetOrAdd(processId, static id => ProcessPath.Of(id));

        // パスが取れないなら判断材料が無い。従来どおり出す。
        return path.Length == 0 ? AutoShowPolicy.Auto : PolicyFor(path);
    }

    private static bool HasPattern(AutomationElement element, AutomationProperty property) =>
        element.GetCurrentPropertyValue(property) is true;

    /// <summary>
    /// 読み取り専用か。値パターンを持たない要素では判定できないので false を返す。
    /// 表示できないより、余計に表示されるほうが害が小さい。
    /// </summary>
    private static bool IsReadOnly(AutomationElement element) =>
        element.GetCurrentPropertyValue(ValuePattern.IsReadOnlyProperty, ignoreDefaultValue: true) is true;

    public void Dispose() => Stop();
}
