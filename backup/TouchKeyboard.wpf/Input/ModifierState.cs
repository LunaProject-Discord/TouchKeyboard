using System;
using System.Collections.Generic;
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
/// タップでラッチ、ラッチ中の再タップでロック、ロック中の再タップで解除。
/// architecture.md の「ダブルタップでロック」は、ラッチ中の再タップとして実装する。
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

    /// <summary>いずれかの修飾キーの状態が変わったときに発火する。</summary>
    public event EventHandler? Changed;

    public LatchState this[ModifierKind kind] =>
        _states.TryGetValue(kind, out var state) ? state : LatchState.Off;

    public bool IsActive(ModifierKind kind) => this[kind] != LatchState.Off;

    public bool IsShiftActive => IsActive(ModifierKind.Shift);
    public bool IsFnActive => IsActive(ModifierKind.Fn);

    /// <summary>修飾キーがタップされたときに呼ぶ。Off → Latched → Locked → Off と巡回する。</summary>
    public void Toggle(ModifierKind kind)
    {
        if (kind == ModifierKind.None) return;

        _states[kind] = this[kind] switch
        {
            LatchState.Off => LatchState.Latched,
            LatchState.Latched => LatchState.Locked,
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
        var changed = false;

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
