using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using TouchKeyboard.Interop;
using TouchKeyboard.Settings;
using TouchKeyboard.Views;

namespace TouchKeyboard;

public partial class App : Application
{
    private AppSettings _settings = null!;

    // 二重起動で即終了する経路があるため、生成されないまま Teardown に入りうる。
    private KeyboardWindow? _window;
    private TrayIcon? _tray;

    /// <summary>後片付けを二重に走らせないためのフラグ。</summary>
    private bool _tornDown;

    /// <summary>解除できるよう実体を保持する。ラムダを都度作ると -= が効かない。</summary>
    private SessionEndingEventHandler? _sessionEndingHandler;

    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 二重起動を防ぐ。既に動いていれば、そちらを表示させて自分は終了する。
        // 複数動くと AppBar が多重に登録され、作業領域が余計に確保される。
        _instance = SingleInstance.Acquire();
        if (!_instance.IsFirst)
        {
            Shutdown();
            return;
        }

        // INPUT 構造体のレイアウト誤りは SendInput を黙って失敗させ、
        // 原因の分かりにくい不具合になる。起動時に検出する。
        KeySender.VerifyLayout();

        _settings = AppSettings.Load();

        _window = new KeyboardWindow(_settings);
        MainWindow = _window;

        // ウィンドウを表示せずにハンドルだけ作る。
        // ハンドルが無いと AppBar もメッセージフックも用意できないが、
        // Show するとちらつくため EnsureHandle で SourceInitialized だけ走らせる。
        new WindowInteropHelper(_window).EnsureHandle();

        _tray = new TrayIcon(_settings);
        _tray.ToggleRequested += (_, _) => _window.ToggleKeyboard();
        _tray.ExitRequested += (_, _) => Shutdown();
        _tray.MonitorSelected += (_, deviceName) => _window.SetMonitor(deviceName);
        _tray.AutoShowChanged += (_, enabled) =>
        {
            _settings.AutoShow = enabled;
            _settings.Save();
        };
        _tray.CoverTaskbarChanged += (_, cover) => _window.SetCoverTaskbar(cover);

        // 2 回目以降の起動を「表示する」操作として扱う。
        // リスナースレッドから来るため、ディスパッチャ経由で UI を触る。
        _instance.ShowRequested += (_, _) =>
            Dispatcher.BeginInvoke(() => _window?.ShowKeyboard());

        // 前回終了時の表示状態を復元する（要件 F-4）。
        if (_settings.Visible)
        {
            _window.ShowKeyboard();
        }
        else
        {
            // 隠したまま起動する場合、ウィンドウは一度も表示しない。
            // Show してから Hide すると一瞬見えてしまい、落ちたように見える。
            // 自動表示（Phase 4）が入るまでは復帰手段が分かりにくいので、常駐している旨を知らせる。
            _tray.NotifyRunningHidden();
        }

        // ABM_REMOVE を確実に通す経路その 2 と 3。
        // 呼ばずに終了すると他アプリの作業領域が縮んだまま残る。
        // 他プロセスが登録した AppBar を外部から解除する API は無いため、
        // 残骸を後から掃除することはできない。ここで確実に通す。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Teardown();

        _sessionEndingHandler = (_, _) => Teardown();
        SystemEvents.SessionEnding += _sessionEndingHandler;
    }

    private void Teardown()
    {
        if (_tornDown) return;
        _tornDown = true;

        // 最初に解除する。SystemEvents は専用のメッセージポンプスレッドを持つため、
        // 購読したままだとプロセスが終了しきらずゾンビとして残る。
        if (_sessionEndingHandler is not null)
        {
            SystemEvents.SessionEnding -= _sessionEndingHandler;
            _sessionEndingHandler = null;
        }

        // 二重起動で即終了した場合、ウィンドウもトレイも作られていない。
        _window?.Teardown();

        // 隠しただけのウィンドウは閉じられていない。明示的に閉じる。
        _window?.Close();
        _window = null;

        _tray?.Dispose();
        _tray = null;

        _instance?.Dispose();
        _instance = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 経路その 1。
        Teardown();
        base.OnExit(e);

        // Environment.Exit はマネージドの終了処理を走らせるが、この構成では返ってこない
        // （実測: 後片付け完了後にハングし、スレッド 1 本のゾンビが残る）。
        //
        // この時点で OS から見える後片付けは全て終わっている。
        //   ABM_REMOVE 済み / 設定保存済み / トレイアイコン削除済み / Mutex 解放済み
        //
        // 残っても失うものはなく、逆にゾンビが Mutex を解放したまま居座ると
        // 二重起動の抑止が効かなくなる。ファイナライザを待たずに落とす。
        try
        {
            Process.GetCurrentProcess().Kill();
        }
        catch (Exception)
        {
            Environment.Exit(e.ApplicationExitCode);
        }
    }
}
