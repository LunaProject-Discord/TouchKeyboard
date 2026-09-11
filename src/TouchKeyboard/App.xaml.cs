using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using TouchKeyboard.Automation;
using TouchKeyboard.Diagnostics;
using TouchKeyboard.Interop;
using TouchKeyboard.Settings;
using TouchKeyboard.Views;

namespace TouchKeyboard;

public partial class App : Application
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_DISPLAYCHANGE = 0x007E;

    /// <summary>
    /// サインアウト・シャットダウン。ABM_REMOVE を確実に通す経路その 3。
    /// Microsoft.Win32.SystemEvents を参照する代わりにここで受ける。
    /// あちらは専用のメッセージポンプスレッドを持ち、終了を妨げることがある。
    /// </summary>
    private const int WM_ENDSESSION = 0x0016;

    private AppSettings _settings = null!;

    // 二重起動で即終了する経路があるため、生成されないまま Teardown に入りうる。
    private KeyboardWindow? _window;
    private TrayIcon? _tray;
    private SingleInstance? _instance;
    private WindowSubclass? _subclass;
    private FocusWatcher? _focus;
    private PhysicalKeyWatcher? _physicalKeys;
    private PointerSourceWatcher? _pointer;
    private SettingsWindow? _settingsWindow;
    private TrayMenuWindow? _trayMenu;

    /// <summary>後片付けを二重に走らせないためのフラグ。</summary>
    private bool _tornDown;

    /// <summary>
    /// 最後の操作が物理キーボードだったか。
    ///
    /// 打鍵で隠すだけでは足りない。Tab で入力欄へ移ると、そのキーが起こした
    /// フォーカス移動で出し直してしまう。マウスと同じく種別を保持して見送る。
    /// </summary>
    private bool _lastInputWasPhysicalKey;

    /// <summary>
    /// 直近に確定タッチの報告を見た時刻。0 なら該当なし。
    ///
    /// これより後の報告との間隔が <see cref="TapDebounceMs"/> 以上空いていれば、
    /// 別の新しい接触が始まったとみなす。触れている間、スクロールなどで
    /// 動かしていても生の報告は途切れず届き続ける（実測では 1 回のドラッグで
    /// 数十回単位）ため、この間隔で「同じ接触の続き」と「新しい接触」を分ける。
    ///
    /// 「触れていない → 触れている」の遷移そのもので判定する方式も試したが、
    /// 実機では正しく検出できず反応しなくなった。生の HID 報告は必ずしも
    /// 途切れなく理想的な順序で届くとは限らないため、状態の遷移ではなく
    /// 時間の間隔で新しい接触を見分ける。
    /// </summary>
    private long _lastTouchAt;

    /// <summary>これより短い間隔で続く確定タッチは、同じ接触の続きとみなす。</summary>
    private const int TapDebounceMs = 250;

    /// <summary>
    /// 新しい接触が始まってから、タップと確定するまでの遅延。
    ///
    /// 触れた瞬間だけを見てタップ扱いにすると、スワイプやドラッグの
    /// 開始点とも区別が付かない（実機で確認済み）。一般的なタップ認識と同じく、
    /// 少し待って動きを確かめ、この間に大きく動かなかったものだけをタップとする。
    /// </summary>
    private const int TapConfirmDelayMs = 120;

    /// <summary>
    /// タップとみなす移動量の上限（物理ピクセル、2乗で保持）。
    /// これを超えて動いたらスワイプ・ドラッグとみなし、タップとしては扱わない。
    ///
    /// 高解像度パネルでの自然な指のぶれを吸収できる値を仮に置いている。
    /// 小さすぎるとタップが弾かれ、大きすぎると短いスワイプがタップと誤認される。
    /// // TODO: 実機確認。
    /// </summary>
    private const long TapMoveToleranceSquaredPx = 32L * 32;

    private DispatcherQueueTimer? _tapConfirmTimer;

    /// <summary>タップかどうかを確かめている最中の接触があるか。</summary>
    private bool _tapCandidateActive;

    /// <summary>その接触が始まった位置。</summary>
    private (int X, int Y) _tapStartPoint;

    /// <summary>その接触の直近の位置。タップと確定した時点でこれを押された位置として使う。</summary>
    private (int X, int Y) _tapLatestPoint;

    /// <summary>その接触が自分のキーボードへのものか。始まった時点で決める。</summary>
    private bool _tapCandidateIsOwnTouch;


    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 二重起動を防ぐ。既に動いていれば、そちらを表示させて自分は終了する。
        // 複数動くと AppBar が多重に登録され、作業領域が余計に確保される。
        _instance = SingleInstance.Acquire();
        if (!_instance.IsFirst)
        {
            Terminate();
            return;
        }

        // INPUT 構造体のレイアウト誤りは SendInput を黙って失敗させ、
        // 原因の分かりにくい不具合になる。起動時に検出する。
        KeySender.VerifyLayout();

        _settings = AppSettings.Load();

        // 既定値のまま書き戻す。新しく増えた項目がファイルに現れないと、
        // 利用者が編集できる設定であることに気付けない。
        _settings.Save();

        // 記録の可否は設定より先に決める。以降の TraceLog.Write を効かせるため。
        TraceLog.Enabled = _settings.Trace;
        _window = new KeyboardWindow(_settings);

        // WinUI 3 には HwndSource.AddHook が無いのでサブクラス化で受ける。
        _subclass = new WindowSubclass(_window.Handle);
        _subclass.MessageReceived += OnMessage;

        _tray = new TrayIcon(_settings);

        // メニューは WinUI の部品で描く。UI スレッドで組む必要がある。
        _tray.MenuRequested += (_, request) =>
            _window.DispatcherQueue.TryEnqueue(() => ShowTrayMenu(request));

        _tray.ToggleRequested += (_, _) => _window.DispatcherQueue.TryEnqueue(_window.ToggleKeyboard);
        _tray.ExitRequested += (_, _) => _window.DispatcherQueue.TryEnqueue(Terminate);
        _tray.MonitorSelected += (_, name) => _window.DispatcherQueue.TryEnqueue(() => _window.SetMonitor(name));
        _tray.SettingsRequested += (_, _) => _window.DispatcherQueue.TryEnqueue(ShowSettings);
        _window.SettingsRequested += (_, _) => ShowSettings();

        // 自分のメニューを開いている間は、相手のフォーカスが外れる。
        // それを入力終了と解釈して引っ込まないようにする。
        _window.MenuActiveChanged += (_, active) => _focus?.SuspendHiding(active);

        // 隠すと AppBar が外れ、相手の再レイアウトで入力欄がフォーカスを
        // 取り直すことがある。隠した事実と、自分の意思で隠したか（remember）を
        // 伝え、直後や後々の再表示を自分のせいと見分けてもらう
        // （スワイプで隠しても出し直されてしまう不具合への対処）。
        _window.Hidden += (_, manual) => _focus?.NotifyHidden(manual);

        // 入力欄へのフォーカス移動でキーボードを出す（Phase 4）。
        // 自動での開閉は設定に残さない。設定は利用者が選んだ状態を表すものにする。
        _focus = new FocusWatcher(_window.DispatcherQueue);
        _focus.ShowRequested += (_, _) =>
        {
            // 物理キーボードが使えるなら自動では出さない。手動での表示は妨げない。
            if (InputDevices.HasPhysicalKeyboard())
            {
                TraceLog.Write("  -> 表示せず（物理キーボードあり）");
                return;
            }

            // マウスで入力欄を選んだなら、打つのもマウスの持ち主の手元だろう。
            // タッチとペンは署名で見分けられるので、それらは従来どおり出す。
            // 出さないだけでなく、出ているものは引っ込める。
            // 別の手段に持ち替えた後も残り続けると、画面を占有したままになる。
            if (_pointer?.LastClickWasMouse == true)
            {
                TraceLog.Write("  -> 非表示（マウス操作によるフォーカス）");
                _window?.HideKeyboard(remember: false);
                return;
            }

            if (_lastInputWasPhysicalKey)
            {
                TraceLog.Write("  -> 非表示（物理キー操作によるフォーカス）");
                _window?.HideKeyboard(remember: false);
                return;
            }

            TraceLog.Write("  -> 表示");
            _window?.ShowKeyboard(remember: false);
        };
        // フォーカスが外れただけの非表示。手動で出しているときは見送られる。
        _focus.HideRequested += (_, _) => _window?.HideKeyboard(remember: false, focusLost: true);
        _focus.Failed += (_, ex) => _window?.ReportError($"自動表示の監視に失敗: {ex.Message}");

        _tray.AutoShowChanged += (_, enabled) =>
        {
            _settings.AutoShow = enabled;
            _settings.Save();

            // 通知領域のアイコンの薄さも自動表示の有無で決まる。切り替えた
            // その場で映すため、設定を保存した直後にここで作り直す。
            _tray.RefreshIcon();

            _window.DispatcherQueue.TryEnqueue(() =>
            {
                if (enabled) _focus?.Start();
                else _focus?.Stop();
            });
        };
        _tray.CoverTaskbarChanged += (_, cover) =>
            _window.DispatcherQueue.TryEnqueue(() => _window.SetCoverTaskbar(cover));

        // 2 回目以降の起動を「表示する」操作として扱う。
        // リスナースレッドから来るため、ディスパッチャ経由で UI を触る。
        _instance.ShowRequested += (_, _) =>
            _window.DispatcherQueue.TryEnqueue(() => _window?.ShowKeyboard());

        // 前回終了時の表示状態を復元する（要件 F-4）。
        if (_settings.Visible)
        {
            _window.ShowKeyboard();
        }
        else if (!_settings.AutoShow)
        {
            // 自動表示が無ければ復帰手段が分かりにくいので、常駐している旨を知らせる。
            _tray.NotifyRunningHidden();
        }

        if (_settings.AutoShow) _focus.Start();

        // 物理キーボードで打ち始めたら引っ込む。画面を占有し続ける理由が無くなるため。
        // フックの中で AppBar を触ると入力経路を止めてしまうので、UI へ回して処理する。
        _physicalKeys = new PhysicalKeyWatcher();
        _physicalKeys.Pressed += (_, _) =>
            _window?.DispatcherQueue.TryEnqueue(() =>
            {
                TraceLog.Write("物理キー -> 非表示");
                _lastInputWasPhysicalKey = true;
                _window?.HideKeyboard(remember: false);
            });
        _physicalKeys.Start();

        _pointer = new PointerSourceWatcher();

        // タッチからマウスへ持ち替えたら引っ込む。同じ入力欄を触り直した場合は
        // フォーカスが変化しないため、フォーカス側からは適用の機会が来ない。
        _pointer.SwitchedToMouse += (_, point) =>
        {
            // 自分のキーをマウスで押したときは対象外。押した瞬間に消えてしまう。
            if (IsOwnWindowAt(point)) return;

            _window?.DispatcherQueue.TryEnqueue(() =>
            {
                TraceLog.Write("マウスに持ち替え -> 非表示");
                _window?.HideKeyboard(remember: false);
            });
        };

        _pointer.Start();

        // タップと確定するまでの遅延。詳細は _tapConfirmTimer 関連フィールドの説明を参照。
        _tapConfirmTimer = _window.DispatcherQueue.CreateTimer();
        _tapConfirmTimer.Interval = TimeSpan.FromMilliseconds(TapConfirmDelayMs);
        _tapConfirmTimer.IsRepeating = false;
        _tapConfirmTimer.Tick += (_, _) => ConfirmTapCandidate();

        // 表示直後の揺れと利用者の操作を見分けるため、ポインタの最終操作時刻を渡す。
        _focus.LastPointerInputAt = LastForeignPointerAt;
        _focus.IsKeyboardShown = () => _window?.IsShown ?? false;
        _focus.PolicyFor = path => _settings.PolicyFor(path);

        // タッチはマウスフックに届かないので、Raw Input で別に受け取る。
        var registered = _pointer.RegisterTouch(_window.Handle);
        TraceLog.Write(
            $"タッチ登録  結果={registered} 対象=0x{_window.Handle:X} "
            + $"エラー={Marshal.GetLastWin32Error()}");

        TraceLog.Write(
            $"起動  autoShow={_settings.AutoShow} 物理キーボード={InputDevices.HasPhysicalKeyboard()}");

        // ABM_REMOVE を確実に通す経路その 2。
        // 呼ばずに終了すると他アプリの作業領域が縮んだまま残る。
        // 他プロセスが登録した AppBar を外部から解除する API は無いため、
        // 残骸を後から掃除することはできない。ここで確実に通す。
        // 経路その 3（サインアウト・シャットダウン）は WM_ENDSESSION で受ける。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Teardown();
    }

    /// <summary>
    /// 通知領域のメニューを出す。
    ///
    /// ウィンドウは作り直さず使い回す。毎回作ると、初回だけ間が空いて
    /// 押しても出てこないように見える。
    /// </summary>
    private void ShowTrayMenu(TrayMenuRequest request)
    {
        if (_settings is null) return;

        _trayMenu ??= new TrayMenuWindow(_settings);
        _trayMenu.ShowAt(request.X, request.Y, request.Items);
    }

    /// <summary>
    /// 設定画面を開く。すでに開いていれば前面に出すだけ。
    /// 二重に開くと、それぞれが同じ設定を書き戻して食い違う。
    /// </summary>
    private void ShowSettings()
    {
        if (_window is null || _settings is null) return;

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_settings, _window);

        window.AutoShowChanged += (_, enabled) =>
        {
            if (enabled) _focus?.Start();
            else _focus?.Stop();
        };

        window.Closed += (_, _) => _settingsWindow = null;

        _settingsWindow = window;
        window.Activate();
    }

    /// <summary>
    /// 入力先のアプリに対して、利用者が最後にポインタを使った時刻。
    ///
    /// 自分のキーを押した操作は数えない。フォーカスの判定では「利用者が
    /// 画面を触ったか」を、アプリが自分でフォーカスを動かしたのか本人の操作かの
    /// 見分けに使っている。キーをタップした時刻をそこに混ぜると、
    /// 打つたびに「本人が別の場所を触った」と読めてしまう。
    ///
    /// 実例: Edge のアドレスバーで下矢印を押すと候補一覧へフォーカスが移る。
    /// 押した操作を数えていたため、本人が一覧を選びに行ったと判定して隠れていた。
    /// </summary>
    private long LastForeignPointerAt()
    {
        var pointer = _pointer?.LastInputAt ?? 0;
        var own = _window?.LastOwnPointerAt ?? 0;

        // 同じ 1 回のタップを、生の入力と自分の面の通知で二度控えている。
        // 届く順は決まっていない。生の入力は投函された通知として届き、
        // ポインタの通知（入力の通知）より先に処理されるのが普通なので、
        // 「生の入力のほうが古い」場合も自分の操作として扱う。
        //
        // ここを「自分の記録より後にあること」だけで見ていたときは、
        // ほぼ常に生の入力が先に立ってしまい、この判定が働かなかった。
        return pointer - own <= OwnTouchGraceMs ? 0 : pointer;
    }

    /// <summary>自分の面への操作とみなす猶予（ミリ秒）。</summary>
    private const long OwnTouchGraceMs = 250;

    /// <summary>その画面座標にあるのが自分のウィンドウか。</summary>
    private bool IsOwnWindowAt((int X, int Y) point)
    {
        if (_window is null) return false;

        var hwnd = NativeMethods.WindowFromPoint(
            new NativeMethods.POINT { X = point.X, Y = point.Y });

        if (hwnd == 0) return false;

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        return (root == 0 ? hwnd : root) == _window.Handle;
    }

    /// <summary>
    /// タップの確定。<see cref="TapConfirmDelayMs"/> 経った時点で呼ばれる。
    ///
    /// この間に動きの上限を超えていれば <see cref="_tapCandidateActive"/> は
    /// 既に false になっており、ここでは何もしない。
    /// </summary>
    private void ConfirmTapCandidate()
    {
        if (!_tapCandidateActive) return;
        _tapCandidateActive = false;

        // 自分のキーボードへのタップは対象外。
        //
        // スワイプで隠す操作もこの生の入力を通る。除外しないと、隠した直後の
        // その接触自体が「確かめ直せ」の合図になり、相手アプリのフォーカスは
        // まだ入力欄のままなので即座に表示し直してしまう（隠せない不具合になる）。
        if (_tapCandidateIsOwnTouch) return;

        TraceLog.Write("触れ直しを確認。フォーカスを確認し直す");
        _focus?.ReevaluateSoon();

        // 対象外のアプリの入力欄を、利用者が自分から押した場合は出す。
        _focus?.NotifyTouched(_tapLatestPoint);
    }

    private bool OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (_window is null) return false;

        // AppBar からの通知。位置が変わったら取り直す。
        if (_window.AppBarCallbackMessage != 0 && msg == _window.AppBarCallbackMessage)
        {
            if ((int)wParam == NativeMethods.ABN_POSCHANGED) _window.RefreshDock();
            return true;
        }

        // 登録したのはタッチとペンだけなので、届いた時点で種別が分かる。
        // 中身は読まない。DefWindowProc へ通す必要があるため false を返す。
        if (msg == NativeMethods.WM_INPUT)
        {
            // マウス／物理キーから持ち替えた瞬間、または実際に触れた瞬間に
            // いまのフォーカスを確かめ直す。
            //
            // すでにフォーカスがある入力欄を触っても、フォーカスは「変化」しないので
            // UI Automation は通知しない。マウスでクリックして抑制した直後に
            // 同じ欄をタッチしても、判定は解除されているのに適用する機会が来ない。
            // 実機では、これがマウスとの持ち替えに限らず、タッチのまま同じ欄を
            // 触り直した場合（例: Windows Terminal をいったん離してまた触る）にも
            // 起きることが分かった。接触が確定した報告であれば、持ち替えに関わらず
            // 毎回確かめ直す。ReevaluateSoon 側で 150ms に間引かれるため、
            // 連打や指を滑らせたときの負荷は増えない。
            //
            // ここはウィンドウプロシージャの中。相手のアプリはまだタップを処理していない。
            // 即座に表示すると、AppBar が作業領域を狭めた結果、指の下でウィンドウが
            // リサイズされ、タップの行き先が変わってフォーカスが外れる。少し待つ。
            // 押された場所は生の報告から読む。カーソルは動かないので他に手段が無い。
            // 目盛りの読み替えは実機で確かめてある。画面の四隅を触ったとき、
            // 左上が 7,21、右下が 2846,1907（画面は 2880×1920）だった。
            // 指の接地は隅そのものには届かないので、この差で合っている。
            //
            // 接触していない報告（ペンのホバーなど）では確かめ直さない。位置が読めない
            // ことと接触していないことを同じに扱うと、ホバー中の頻繁な報告のたびに
            // 「触ったが位置が分からない」の扱いに落ち、かざしただけで表示されてしまう。
            var touch = TouchDigitizer.Read(lParam);

            // ペンも対象にする。以前はペンでの接触を一律に除外していたが、それでは
            // 手動で隠したあとにペンで入力欄を触り直しても表示されなくなってしまった。
            // ペンでの書き始めとペンでのタップの違いはデバイス種別ではなく動き量で
            // 決まる話であり、下のタップ判定（動いたらスワイプ・ドラッグとみなして
            // 取り消す）がそのまま両方を正しく見分ける。書き文字は接触直後から
            // 大きく動くのでタップとしては確定せず、単なるタップは動かないまま
            // 確定する。
            var isConfirmedTouch = touch is { IsTouching: true };

            // マウス／物理キーから持ち替えたかは、NotifyTouch で LastClickWasMouse が
            // 書き換わる前の値を見る。
            var wasOtherSource = _pointer?.LastClickWasMouse == true || _lastInputWasPhysicalKey;

            // 確定した接触のときだけ「直近のポインタ操作時刻」を進める。
            //
            // ペンのホバーは接触より遥かに高頻度で報告が届く。ここを毎回更新すると、
            // ペンが画面付近にあるだけで「ついさっき操作した」ことになってしまい、
            // 表示直後のゆれを吸収する猶予（SettleAfterShowMs 等、FocusWatcher 側）が
            // 実質的に効かなくなる。実機では、ペンをかざしているだけで本物の
            // フォーカス変化に対する抑制が外れ、意図しない非表示につながっていた。
            if (isConfirmedTouch)
            {
                _pointer?.NotifyTouch();
                _lastInputWasPhysicalKey = false;
            }

            // 同じ接触の続きか、新しい接触かを時間の間隔で見分ける。
            var now = Environment.TickCount64;
            var isNewTouch = isConfirmedTouch && now - _lastTouchAt >= TapDebounceMs;
            if (isConfirmedTouch) _lastTouchAt = now;

            if (isNewTouch && touch!.Value.Point is { } startPoint)
            {
                // 新しい接触の始まり。ここではまだタップかスワイプか分からないので
                // 反応しない。少し待って動きを確かめてから確定させる
                // （ConfirmTapCandidate、TapConfirmDelayMs 参照）。
                _tapCandidateActive = true;
                _tapStartPoint = startPoint;
                _tapLatestPoint = startPoint;
                _tapCandidateIsOwnTouch = IsOwnWindowAt(startPoint);

                _tapConfirmTimer?.Stop();
                _tapConfirmTimer?.Start();
            }
            else if (_tapCandidateActive && isConfirmedTouch && touch!.Value.Point is { } movedPoint)
            {
                _tapLatestPoint = movedPoint;

                var dx = (long)(movedPoint.X - _tapStartPoint.X);
                var dy = (long)(movedPoint.Y - _tapStartPoint.Y);

                if ((dx * dx) + (dy * dy) > TapMoveToleranceSquaredPx)
                {
                    // スワイプ・ドラッグと判断。タップとしては扱わない。
                    _tapCandidateActive = false;
                    _tapConfirmTimer?.Stop();
                }
            }

            // マウス／物理キーから持ち替えた瞬間は、動きを確かめずにすぐ確かめ直す。
            // 位置に関わらず「持ち替えた」という事実そのものが合図になる。
            if (wasOtherSource)
            {
                TraceLog.Write("タッチに持ち替え。少し待ってフォーカスを確認し直す");
                _focus?.ReevaluateSoon();
            }

            return false;
        }

        switch (msg)
        {
            // サインアウト・シャットダウン。ここで確保領域を返さないと縮んだまま残る。
            case WM_ENDSESSION:
                if (wParam != 0) Teardown();
                break;

            case WM_SETTINGCHANGE:
                _window.RefreshTheme();

                // 通知領域のアイコンも作り直す。
                // テーマの切り替えでは寸法は変わらないが、
                // 拡大率の変更もこの通知で来ることがある。
                _tray?.RefreshIcon();

                // キーボードの着脱で通知される。
                if (lParam != 0 && Marshal.PtrToStringUni(lParam) == "ConvertibleSlateMode")
                {
                    TraceLog.Write(
                        $"キーボード着脱  物理キーボード={InputDevices.HasPhysicalKeyboard()}");

                    if (InputDevices.HasPhysicalKeyboard())
                    {
                        // 使えるようになったので引っ込む。
                        _window.HideKeyboard(remember: false);
                    }
                    else
                    {
                        // 外された。フォーカスは動いていないので、
                        // いま入力欄に居るかどうかを自分で確かめ直す。
                        _focus?.Reevaluate();
                    }
                }

                break;

            // モニタ間移動やスケーリング変更。AppBar が確保した物理領域とずれるため取り直す。
            case WM_DPICHANGED:
            case WM_DISPLAYCHANGE:
                _window.RefreshDock();

                // 通知領域のアイコンの寸法も拡大率で変わる。作り直さないと引き伸ばされる。
                _tray?.RefreshIcon();
                break;
        }

        return false;
    }

    private void Teardown()
    {
        if (_tornDown) return;
        _tornDown = true;

        // 低レベルフックを真っ先に外す。
        //
        // これを残したまま止まると、OS の入力がこのプロセスの応答を待つ。
        // マウスの動きまで引っかかるようになり、影響がアプリの外へ出る。
        // 後片付けの中でいちばん害が大きいので先に片づける。
        _physicalKeys?.Dispose();
        _physicalKeys = null;

        _pointer?.Dispose();
        _pointer = null;

        // 通知領域のアイコン。目に見える残骸で、他の後片付けに依存しない。
        _tray?.Dispose();
        _tray = null;

        // ウィンドウを畳む前に監視を止める。解除しないと UI Automation の
        // コールバックが後片付け中のウィンドウを触りに来る。
        _focus?.Dispose();
        _focus = null;

        // 作業領域を返す。
        _window?.Teardown();

        _settingsWindow?.Close();
        _settingsWindow = null;

        // 閉じずに畳むだけにする。
        // 閉じると戻ってこないことがあり、その先の後片付けが走らない。
        // このあとプロセスごと落とすので、閉じる必要はない。
        _trayMenu?.Hide();
        _trayMenu = null;

        _subclass?.Dispose();
        _subclass = null;

        _window = null;

        _instance?.Dispose();
        _instance = null;
    }

    /// <summary>
    /// 後片付けをしてプロセスを落とす。
    ///
    /// 素直な終了では返ってこない場合があり、ゾンビが残ると Mutex も解放済みのため
    /// 二重起動の抑止が効かなくなる。ここまでで OS から見える後片付けは終わっている。
    ///   ABM_REMOVE 済み / 設定保存済み / トレイアイコン削除済み / Mutex 解放済み
    /// </summary>
    private void Terminate()
    {
        // 後片付けが返ってこなくても必ず落とす。
        //
        // 残ると WinUI のウィンドウが居座り、フックの解除も済んでいない場合は
        // OS の入力まで巻き添えになる。時間を切って別の糸から落とす。
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TerminateTimeoutMs);
            Kill();
        })
        {
            IsBackground = true,
        };

        watchdog.Start();

        Teardown();
        Kill();
    }

    /// <summary>後片付けを待つ上限。これを過ぎたら落とす。</summary>
    private const int TerminateTimeoutMs = 2000;

    private static void Kill()
    {
        try
        {
            Process.GetCurrentProcess().Kill();
        }
        catch (Exception)
        {
            Environment.Exit(0);
        }
    }
}
