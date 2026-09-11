using System;
using System.Threading;

namespace TouchKeyboard;

/// <summary>
/// 二重起動を防ぎ、2 回目以降の起動を「表示する」操作として既存インスタンスへ伝える。
///
/// 複数インスタンスが同時に動くと、それぞれが AppBar を登録して作業領域が多重に確保される。
/// さらに非表示で起動した場合はウィンドウが出ないため、増えていることに気付けない。
///
/// 通知にウィンドウメッセージ（HWND_BROADCAST）を使わないのは、
/// ShowInTaskbar="False" の WPF ウィンドウが不可視の親を持つ「所有されたウィンドウ」になり、
/// ブロードキャストの配送対象から外れるため。名前付きイベントなら所有関係に左右されない。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\TouchKeyboard.SingleInstance";
    private const string ShowEventName = @"Local\TouchKeyboard.ShowRequest";
    private const string StopEventName = @"Local\TouchKeyboard.ListenerStop";

    private readonly Mutex _mutex;
    private readonly bool _owned;

    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _stopEvent;
    private Thread? _listener;
    private bool _disposed;

    /// <summary>このプロセスが唯一のインスタンスか。</summary>
    public bool IsFirst => _owned;

    /// <summary>
    /// 別のインスタンスが起動し、表示を要求した。
    /// リスナースレッドから発火するため、UI の操作はディスパッチャ経由で行うこと。
    /// </summary>
    public event EventHandler? ShowRequested;

    private SingleInstance(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    /// <summary>
    /// 取得を試みる。既に別のインスタンスが動いていれば、
    /// そちらへ表示要求を送ってから <see cref="IsFirst"/> が false の状態で返る。
    /// </summary>
    public static SingleInstance Acquire()
    {
        // Local\ 付きでセッション内に限定する。別ユーザーのセッションとは干渉させない。
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        var instance = new SingleInstance(mutex, created);

        if (created)
        {
            instance.StartListening();
        }
        else
        {
            instance.RequestShow();
        }

        return instance;
    }

    /// <summary>1 つ目のインスタンスとして、他からの表示要求を待ち受ける。</summary>
    private void StartListening()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);

        _listener = new Thread(Listen)
        {
            IsBackground = true,
            Name = "single-instance-listener",
        };

        _listener.Start();
    }

    private void Listen()
    {
        var handles = new WaitHandle[] { _showEvent!, _stopEvent! };

        while (true)
        {
            var index = WaitHandle.WaitAny(handles);

            // 1 = 停止要求。
            if (index != 0) return;

            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>2 つ目のインスタンスとして、既存インスタンスに表示を要求する。</summary>
    private void RequestShow()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception)
        {
            // 既存インスタンスが終了処理中でイベントが消えている場合など。
            // 表示できなくても、二重起動を防ぐという主目的は達成できている。
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // リスナーを止める。バックグラウンドスレッドなので待たなくてよい。
        try { _stopEvent?.Set(); } catch (Exception) { /* 終了時なので無視 */ }

        _showEvent?.Dispose();
        _stopEvent?.Dispose();

        if (_owned)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 別スレッドから解放された場合。終了時なので無視してよい。
            }
        }

        _mutex.Dispose();
    }
}
