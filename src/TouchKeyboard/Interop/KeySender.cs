using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TouchKeyboard.Interop;

/// <summary>
/// 1 つのキーイベント。スキャンコードと拡張キーフラグ、押下か解放かを持つ。
/// </summary>
/// <param name="ScanCode">
/// E0 プレフィックスを含まない基底スキャンコード。拡張キーは <paramref name="Extended"/> で表す。
/// </param>
/// <param name="Extended">
/// E0 プレフィックスが必要なキーなら true。矢印・Insert・Delete・Home・End・PageUp・PageDown・
/// 右 Ctrl・右 Alt・左右 Win・Application・テンキーの Enter と / が該当する。
/// 付け忘れるとテンキー側のキーとして解釈される。
/// </param>
/// <param name="KeyUp">解放イベントなら true。</param>
/// <param name="VirtualKey">
/// 仮想キーで送る場合のコード。0 ならスキャンコードで送る（通常はこちら）。
///
/// スキャンコードで送れないキーにだけ使う。<see cref="KeySender"/> の但し書きを参照。
/// </param>
public readonly record struct KeyEvent(
    ushort ScanCode, bool Extended, bool KeyUp, ushort VirtualKey = 0)
{
    public static KeyEvent Down(ushort scanCode, bool extended = false, ushort virtualKey = 0) =>
        new(scanCode, extended, false, virtualKey);

    public static KeyEvent Up(ushort scanCode, bool extended = false, ushort virtualKey = 0) =>
        new(scanCode, extended, true, virtualKey);
}

/// <summary>
/// SendInput ラッパー。
///
/// 絶対制約: キー送出は SendInput + INPUT_KEYBOARD + KEYEVENTF_SCANCODE のみ。
/// WM_CHAR / SendKeys / keybd_event は使用禁止。通常のキーボードと同じ入力経路を
/// 通さなければ IME が介入せず、ATOK の入力ミス補正が働かない。
///
/// <para>
/// ただし IME の入／切だけは仮想キーで送る。この 2 つはスキャンコードで送れない。
/// 実機のスキャンコードは E0 F1 / E0 F2 だが、スキャンコードのビット 7 は
/// 解放を表す位置にあり、注入時に落とされて 0x71 / 0x72 になる。
/// 実測すると、送った側の意図とは関係なく仮想キー 0xFF（無効）として届いた。
/// 仮想キーで送れば 0x1A / 0x16 として正しく届く（スキャンコードも併せて埋める）。
///
/// 文字を伴わないキーなので、補正の経路には関わらない。
/// </para>
/// </summary>
public static class KeySender
{
    /// <summary>Marshal.SizeOf で求めた INPUT のサイズ。定数を直書きしない。</summary>
    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();

    /// <summary>
    /// 期待される INPUT サイズ。実測値と食い違う場合、構造体レイアウトが壊れており
    /// SendInput は黙って失敗する（Phase 1 の受け入れ基準が通らない主要因）。
    /// x64/ARM64 は 40、x86 は 28。
    /// </summary>
    private static readonly int ExpectedInputSize = IntPtr.Size == 8 ? 40 : 28;

    /// <summary>
    /// 構造体レイアウトの健全性を確認する。起動時に一度呼ぶこと。
    /// </summary>
    /// <exception cref="InvalidOperationException">レイアウトが期待と異なる場合。</exception>
    public static void VerifyLayout()
    {
        if (InputSize != ExpectedInputSize)
        {
            throw new InvalidOperationException(
                $"INPUT 構造体のサイズが不正です。実測 {InputSize} バイト、期待 {ExpectedInputSize} バイト "
                + $"(IntPtr.Size={IntPtr.Size})。ユニオンのレイアウトかパディングが誤っています。");
        }
    }

    /// <summary>
    /// 複数のキーイベントを 1 回の SendInput 呼び出しでまとめて送出する。
    ///
    /// 1 キーずつ呼ぶと、修飾キーダウンと通常キーダウンの間に他プロセスの入力が
    /// 割り込みうる。修飾キー付きの送出では必ずまとめて渡すこと。
    /// </summary>
    /// <exception cref="Win32Exception">送出に失敗した場合。</exception>
    public static void Send(ReadOnlySpan<KeyEvent> events)
    {
        if (events.IsEmpty) return;

        var inputs = new NativeMethods.INPUT[events.Length];
        for (var i = 0; i < events.Length; i++)
        {
            inputs[i] = ToInput(events[i]);
        }

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent != (uint)inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"SendInput が {inputs.Length} 件中 {sent} 件しか送出できませんでした。"
                + $" Win32Error={error}。UIPI により、より高い整合性レベルのウィンドウへは送出できません。");
        }
    }

    /// <summary>単一キーの押下と解放を 1 回の呼び出しで送る。</summary>
    public static void Tap(ushort scanCode, bool extended = false, ushort virtualKey = 0)
    {
        Span<KeyEvent> events =
        [
            KeyEvent.Down(scanCode, extended, virtualKey),
            KeyEvent.Up(scanCode, extended, virtualKey),
        ];
        Send(events);
    }

    /// <summary>押下のみ。長押しリピートや修飾キーの明示的な保持に使う。</summary>
    public static void KeyDown(ushort scanCode, bool extended = false)
        => Send([KeyEvent.Down(scanCode, extended)]);

    /// <summary>解放のみ。</summary>
    public static void KeyUp(ushort scanCode, bool extended = false)
        => Send([KeyEvent.Up(scanCode, extended)]);

    /// <summary>
    /// 修飾キーを伴うキー送出。
    /// 「修飾ダウン → 通常ダウン → 通常アップ → 修飾アップ」を 1 回で送る。
    /// 修飾キーを先に単独で送って保持する方式は、異常終了時に押しっぱなしが残る。
    /// </summary>
    /// <param name="modifiers">押す順に並べた修飾キー。解放は逆順で送る。</param>
    public static void TapWithModifiers(
        ushort scanCode, bool extended, ReadOnlySpan<KeyEvent> modifiers, ushort virtualKey = 0)
    {
        if (modifiers.IsEmpty)
        {
            Tap(scanCode, extended, virtualKey);
            return;
        }

        var events = new KeyEvent[modifiers.Length * 2 + 2];
        var index = 0;

        for (var i = 0; i < modifiers.Length; i++)
        {
            events[index++] = KeyEvent.Down(modifiers[i].ScanCode, modifiers[i].Extended);
        }

        events[index++] = KeyEvent.Down(scanCode, extended, virtualKey);
        events[index++] = KeyEvent.Up(scanCode, extended, virtualKey);

        for (var i = modifiers.Length - 1; i >= 0; i--)
        {
            events[index++] = KeyEvent.Up(modifiers[i].ScanCode, modifiers[i].Extended);
        }

        Send(events);
    }

    private static NativeMethods.INPUT ToInput(KeyEvent e)
    {
        // 仮想キーの指定があるときはスキャンコードでの解釈を頼まない。
        // スキャンコードは実機と同じ値を併せて埋める。受け手が見ることがある。
        var flags = e.VirtualKey == 0 ? NativeMethods.KEYEVENTF_SCANCODE : 0u;

        if (e.Extended) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        if (e.KeyUp) flags |= NativeMethods.KEYEVENTF_KEYUP;

        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    // KEYEVENTF_SCANCODE 使用時は wVk を 0 にする。
                    wVk = e.VirtualKey,
                    wScan = e.ScanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = 0,
                },
            },
        };
    }
}
