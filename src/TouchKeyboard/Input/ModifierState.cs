using System;
using System.Collections.Generic;
using TouchKeyboard.Interop;
using TouchKeyboard.Layout;

namespace TouchKeyboard.Input;

/// <summary>修飾キーの状態。ラッチとロックを視覚的に区別できるよう 3 値で持つ。</summary>
public enum LatchState
{
    /// <summary>解除。</summary>
    Off = 0,

    /// <summary>ラッチ。次の通常キー入力まで保持し、その後自動で解除する。</summary>
    Latched,

    /// <summary>ロック。明示的に解除するまで保持する。</summary>
    Locked,
}

/// <summary>
/// 修飾キーのラッチ・ロック状態を管理する。UI を知らない。
///
/// タップでラッチ、続けてもう一度タップでロック、ロック中のタップで解除。
/// architecture.md の「ダブルタップでロック」は、ラッチ中に続けて押された場合として実装する。
/// 間を置いた押し直しはロックへ進めず、掛けたラッチを外す操作として扱う。
///
/// これとは別に、指で押さえ続けている間も有効として扱う。
/// 物理キーボードと同じく、Shift を押さえたまま別の指で複数の文字を打てるようにするため。
/// ラッチは 1 文字で消費されるので、押さえ続けだけでは足りない。
///
/// 押さえ続けた場合はラッチを残さない。掛かっていない状態から押さえたのなら
/// 同時押しのつもりであって、指を離した後まで効かせたいわけではない。
/// </summary>
public sealed class ModifierState
{
    private readonly Dictionary<ModifierKind, LatchState> _states = new()
    {
        [ModifierKind.Shift] = LatchState.Off,
        [ModifierKind.Ctrl] = LatchState.Off,
        [ModifierKind.Alt] = LatchState.Off,
        [ModifierKind.Win] = LatchState.Off,
        [ModifierKind.Fn] = LatchState.Off,
    };

    /// <summary>指で押さえたままになっている修飾キー。</summary>
    private readonly HashSet<ModifierKind> _held = [];

    /// <summary>押さえている間に実際に通常キーへ添えられた修飾キー。</summary>
    private readonly HashSet<ModifierKind> _usedWhileHeld = [];

    /// <summary>押さえ始めた時刻。タップと長押しを区別するために持つ。</summary>
    private readonly Dictionary<ModifierKind, long> _heldSince = [];

    /// <summary>前回このキーを押した時刻。二度押しの判定に使う。</summary>
    private readonly Dictionary<ModifierKind, long> _lastPressAt = [];

    /// <summary>前回の押下から続けて押されたキー。</summary>
    private readonly HashSet<ModifierKind> _consecutive = [];

    /// <summary>
    /// ここを超えて押さえていればタップではないとみなす境目（ミリ秒）。
    ///
    /// 押さえている間だけ効かせたいのに、指を離した後もラッチが残ると、
    /// 次に打った文字が意図せず修飾される。押さえ続けたなら、
    /// 同時押しのつもりだったと解釈する。
    /// </summary>
    private const long LongPressMs = 400;

    /// <summary>
    /// 押さえ始める直前の状態。
    ///
    /// タップと押さえ続けは、指を離すまで区別できない。押した時点では
    /// タップとして巡回させておき、押さえ続けだったと分かった時点でここへ戻す。
    /// </summary>
    private readonly Dictionary<ModifierKind, LatchState> _stateBeforeHold = new();

    /// <summary>いずれかの修飾キーの状態が変わったときに発火する。</summary>
    public event EventHandler? Changed;

    public LatchState this[ModifierKind kind] =>
        _states.TryGetValue(kind, out var state) ? state : LatchState.Off;

    public bool IsActive(ModifierKind kind) => this[kind] != LatchState.Off || _held.Contains(kind);

    /// <summary>
    /// 表示用の状態。
    ///
    /// 押さえている間は押す前の状態のまま見せる。指で押さえるのは同時押しの操作で、
    /// 指を離せば元に戻る。灯りは「離した後も効き続ける」ことを示す印なので、
    /// 同時押しの間に光らせると、掛かっていないものが掛かって見える。
    /// 押していること自体はキーの沈みで分かる。
    ///
    /// ロックから押さえた場合は光ったまま。あちらは離しても効き続ける。
    /// </summary>
    public LatchState DisplayState(ModifierKind kind) =>
        _held.Contains(kind) ? StateBeforeHold(kind) : this[kind];

    /// <summary>修飾キーを押さえ始めたときに呼ぶ。</summary>
    public void Hold(ModifierKind kind)
    {
        if (kind == ModifierKind.None) return;

        var now = Environment.TickCount64;

        // 続けて押されたか。間が空いた押下は、二度押しではなく別の操作として扱う。
        if (_lastPressAt.TryGetValue(kind, out var last)
            && now - last <= InputDevices.DoubleTapMs)
        {
            _consecutive.Add(kind);
        }
        else
        {
            _consecutive.Remove(kind);
        }

        _lastPressAt[kind] = now;

        _held.Add(kind);
        _usedWhileHeld.Remove(kind);
        _stateBeforeHold[kind] = this[kind];
        _heldSince[kind] = now;
    }

    /// <summary>タップと呼べない長さだけ押さえられていたか。</summary>
    private bool WasLongPress(ModifierKind kind) =>
        _heldSince.TryGetValue(kind, out var since)
        && Environment.TickCount64 - since >= LongPressMs;

    private LatchState StateBeforeHold(ModifierKind kind) =>
        _stateBeforeHold.TryGetValue(kind, out var state) ? state : LatchState.Off;

    /// <summary>
    /// 修飾キーから指を離したときに呼ぶ。
    ///
    /// 押さえながら打った場合と、何も打たずにタップと呼べない長さ押さえていた場合は、
    /// 押す前の状態によらず解除する。指で押さえる操作は同時押しであり、
    /// 指を離した後まで効かせたい操作ではない。
    ///
    /// 短く叩いただけならタップとみなし、<see cref="Toggle"/> が決めた状態をそのまま残す。
    /// これで「タップしてラッチ」と「押さえたまま連打」が同じキーで両立する。
    /// </summary>
    public void Release(ModifierKind kind)
    {
        if (!_held.Remove(kind)) return;

        // 押さえながら打った、あるいはタップと呼べない長さ押さえていた。
        // どちらも同時押しの操作なので、押した時点の巡回を取り消して解除する。
        if (_usedWhileHeld.Remove(kind) || WasLongPress(kind))
        {
            _states[kind] = LatchState.Off;
        }

        _stateBeforeHold.Remove(kind);
        _heldSince.Remove(kind);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsShiftActive => IsActive(ModifierKind.Shift);
    public bool IsFnActive => IsActive(ModifierKind.Fn);

    /// <summary>
    /// 修飾キーがタップされたときに呼ぶ。Off → Latched → Locked → Off と巡回する。
    ///
    /// ラッチからロックへ進むのは、続けて押されたときだけにする。
    /// ラッチは次の 1 文字で消えるものなので、間を置いて押し直したのなら
    /// ロックしたいのではなく、掛けたラッチを外したいはずである。
    /// </summary>
    public void Toggle(ModifierKind kind)
    {
        if (kind == ModifierKind.None) return;

        _states[kind] = this[kind] switch
        {
            LatchState.Off => LatchState.Latched,
            LatchState.Latched when _consecutive.Contains(kind) => LatchState.Locked,
            _ => LatchState.Off,
        };

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 通常キーの入力後に呼ぶ。ラッチは解除し、ロックは維持する。
    /// </summary>
    public void ConsumeLatched()
    {
        var changed = false;

        foreach (var kind in _states.Keys)
        {
            // 押さえている間は解除しない。指を離した時点でまとめて片付ける。
            if (_held.Contains(kind))
            {
                if (IsActive(kind)) _usedWhileHeld.Add(kind);
                continue;
            }

            if (_states[kind] == LatchState.Latched)
            {
                _states[kind] = LatchState.Off;
                changed = true;
            }
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>すべて解除する。</summary>
    public void Clear()
    {
        var changed = _held.Count > 0;

        _held.Clear();
        _usedWhileHeld.Clear();
        _stateBeforeHold.Clear();
        _heldSince.Clear();
        _lastPressAt.Clear();
        _consecutive.Clear();

        foreach (var kind in _states.Keys)
        {
            if (_states[kind] != LatchState.Off)
            {
                _states[kind] = LatchState.Off;
                changed = true;
            }
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 送出時に併せて押す修飾キーを、押す順で返す。
    /// Fn は送出対象ではないため含めない。
    /// </summary>
    public IReadOnlyList<ModifierKind> ActiveSendable()
    {
        var result = new List<ModifierKind>(4);

        // Ctrl → Alt → Shift → Win の順。物理キーボードでの一般的な押下順に合わせる。
        if (IsActive(ModifierKind.Ctrl)) result.Add(ModifierKind.Ctrl);
        if (IsActive(ModifierKind.Alt)) result.Add(ModifierKind.Alt);
        if (IsActive(ModifierKind.Shift)) result.Add(ModifierKind.Shift);
        if (IsActive(ModifierKind.Win)) result.Add(ModifierKind.Win);

        return result;
    }
}
