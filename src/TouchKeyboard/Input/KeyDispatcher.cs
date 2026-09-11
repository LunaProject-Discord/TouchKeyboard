using System;
using System.Collections.Generic;
using TouchKeyboard.Interop;
using TouchKeyboard.Layout;

namespace TouchKeyboard.Input;

/// <summary>
/// 「どのキーが押されたか」を「どのスキャンコード列を送るか」に変換する。
/// UI を知らない。
/// </summary>
public sealed class KeyDispatcher
{
    /// <summary>
    /// 修飾キーそのもののスキャンコード。ラッチした修飾キーを通常キーに添えて送るときに使う。
    /// 左側のキーを使う。右 Alt / 右 Ctrl は拡張キーだが、修飾としては左で十分。
    /// </summary>
    private static readonly Dictionary<ModifierKind, KeyEvent> ModifierKeys = new()
    {
        [ModifierKind.Shift] = KeyEvent.Down(0x2A),
        [ModifierKind.Ctrl] = KeyEvent.Down(0x1D),
        [ModifierKind.Alt] = KeyEvent.Down(0x38),
        [ModifierKind.Win] = KeyEvent.Down(0x5B, extended: true),
    };

    public ModifierState Modifiers { get; } = new();

    /// <summary>送出に失敗したときに発火する。UI 側でログに出す。</summary>
    public event EventHandler<Exception>? SendFailed;

    /// <summary>送出に成功したときに発火する。UI 側でログに出す。</summary>
    public event EventHandler<string>? Sent;

    /// <summary>
    /// キーがタップされたときに呼ぶ。
    /// 修飾キーならラッチ状態を更新するだけで、スキャンコードは送出しない。
    /// </summary>
    public void Press(KeyDefinition key)
    {
        if (key.Spacer) return;

        if (key.IsModifier)
        {
            // 押さえ始めを記録してからラッチを進める。
            // 指を離すまでは有効なままなので、別の指で連続して文字を打てる。
            Modifiers.Hold(key.Modifier);
            Modifiers.Toggle(key.Modifier);
            return;
        }

        SendNormalKey(key);

        // ラッチは 1 回の入力で解除し、ロックは維持する。
        Modifiers.ConsumeLatched();
    }

    /// <summary>
    /// キーから指が離れたときに呼ぶ。修飾キー以外では何もしない。
    /// </summary>
    public void Release(KeyDefinition key)
    {
        if (key.IsModifier) Modifiers.Release(key.Modifier);
    }

    /// <summary>長押しリピートで呼ぶ。ラッチの消費は行わない。</summary>
    public void Repeat(KeyDefinition key)
    {
        if (key.Spacer || key.IsModifier) return;
        SendNormalKey(key);
    }

    /// <summary>
    /// Fn ラッチを考慮した、実際に送出するスキャンコードと拡張フラグ。
    /// 仮想キーでの指定があるキーでは、そのコードも返す。
    /// </summary>
    public (ushort ScanCode, bool Extended, ushort VirtualKey) Resolve(KeyDefinition key)
    {
        if (Modifiers.IsFnActive && key.FnScanCode.HasValue)
        {
            return (key.FnScanCode.Value, key.FnExtended, 0);
        }

        // 仮想キーでの指定があれば、対応するスキャンコードをそのつど配列から引く。
        // 配列は切り替えられるので、起動時に一度だけ引いて持たない。
        // 引けなければ定義に書かれた代わりのコードを、仮想キー無しで送る。
        if (key.VirtualKey is { } virtualKey)
        {
            return KeyboardLayout.ScanCodeFor(virtualKey) is { } mapped
                ? (mapped.ScanCode, mapped.Extended, virtualKey)
                : (key.ScanCode, key.Extended, (ushort)0);
        }

        return (key.ScanCode, key.Extended, 0);
    }

    /// <summary>Fn / Shift のラッチを考慮した表示ラベル。</summary>
    public string ResolveLabel(KeyDefinition key)
    {
        if (Modifiers.IsFnActive && key.FnLabel is { Length: > 0 } fn) return fn;
        if (Modifiers.IsShiftActive && key.ShiftLabel is { Length: > 0 } shift) return shift;
        return key.Label;
    }

    /// <summary>
    /// Fn のラッチを考慮したアイコン文字。アイコンを持たないキーでは null。
    /// null のときは <see cref="ResolveLabel"/> の文字列を描く。
    /// </summary>
    public string? ResolveIcon(KeyDefinition key)
    {
        if (Modifiers.IsFnActive)
        {
            // Fn 段に別のキーが割り当てられている場合、通常時のアイコンは使わない。
            if (key.FnIcon is not null) return KeyDefinition.ResolveIcon(key.FnIcon);
            if (key.HasFnLayer) return null;
        }

        return KeyDefinition.ResolveIcon(key.Icon);
    }

    private void SendNormalKey(KeyDefinition key)
    {
        var (scanCode, extended, virtualKey) = Resolve(key);
        if (scanCode == 0 && virtualKey == 0) return;

        var active = Modifiers.ActiveSendable();
        var modifiers = new KeyEvent[active.Count];
        for (var i = 0; i < active.Count; i++)
        {
            modifiers[i] = ModifierKeys[active[i]];
        }

        try
        {
            // 「修飾ダウン → 通常ダウン → 通常アップ → 修飾アップ」を 1 回の SendInput で送る。
            // 修飾キーを先に単独で送って保持する方式は、異常終了時に押しっぱなしが残る。
            KeySender.TapWithModifiers(scanCode, extended, modifiers, virtualKey);

            var prefix = active.Count == 0 ? string.Empty : string.Join("+", active) + "+";
            var vk = virtualKey == 0 ? string.Empty : $" vk=0x{virtualKey:X2}";

            Sent?.Invoke(
                this,
                $"{prefix}{key.Label}  sc=0x{scanCode:X2}{(extended ? " E0" : string.Empty)}{vk}");
        }
        catch (Exception ex)
        {
            SendFailed?.Invoke(this, ex);
        }
    }
}
