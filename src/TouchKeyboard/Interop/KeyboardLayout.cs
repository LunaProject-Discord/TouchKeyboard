namespace TouchKeyboard.Interop;

/// <summary>OS のキーボード配列を引く。</summary>
public static class KeyboardLayout
{
    /// <summary>
    /// 仮想キーに対応するスキャンコードを、いまの配列から引く。引けなければ null。
    ///
    /// スキャンコードを配列ファイルに直に書けないキーがある。IME の入／切は
    /// 日本語 106/109 の刻印には無く、値は配列ドライバが決めている。
    /// 記憶や推測で埋めず、OS に聞く。
    ///
    /// 見るのは自プロセスの配列。入力先が別の配列を使っていても、
    /// 送るのはスキャンコードなので、受け手側の配列で解釈される。
    /// </summary>
    public static (ushort ScanCode, bool Extended)? ScanCodeFor(ushort virtualKey)
    {
        var value = NativeMethods.MapVirtualKeyExW(
            virtualKey,
            NativeMethods.MAPVK_VK_TO_VSC_EX,
            NativeMethods.GetKeyboardLayout(0));

        if (value == 0) return null;

        // 上位バイトは拡張の前置き。0xE0 なら拡張キーとして送る。
        // 0xE1（Pause）は 2 バイトの特殊な並びで、この経路では扱わない。
        var prefix = (value >> 8) & 0xFF;

        return ((ushort)(value & 0xFF), prefix == 0xE0);
    }
}
