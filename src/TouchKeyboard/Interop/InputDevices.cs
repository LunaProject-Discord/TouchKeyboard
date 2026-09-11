namespace TouchKeyboard.Interop;

/// <summary>入力デバイスの状態。</summary>
public static class InputDevices
{
    /// <summary>
    /// 物理キーボードが使える状態か。自動表示を抑えるために見る。
    ///
    /// 判断には <c>SM_CONVERTIBLESLATEMODE</c> を使う。Windows 自身がタッチキーボードの
    /// 自動表示を決めるのに使っている値で、キーボードを畳む・外すと 0 になる。
    ///
    /// Raw Input でキーボードを数える方法は採れない。実機では仮想デバイスを含めて
    /// 8 個が並び、物理的に何が繋がっているかを表さなかった。
    ///
    /// <para>
    /// 既知の限界: この値が表すのは変換型デバイス自身の姿勢だけで、
    /// 後から繋いだ Bluetooth や USB のキーボードは反映されない。
    /// また変換型でない機種では常に 0（スレート扱い）を返すため、自動表示は抑制されない。
    /// いずれもトレイの「入力欄で自動表示」を切れば止められる。
    /// </para>
    /// </summary>
    public static bool HasPhysicalKeyboard() =>
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CONVERTIBLESLATEMODE) != 0;

    /// <summary>
    /// OS が二度押しとみなす間隔（ミリ秒）。
    ///
    /// 自前の値は持たない。利用者がマウスの設定で決めた間隔に合わせる。
    /// </summary>
    public static int DoubleTapMs => (int)NativeMethods.GetDoubleClickTime();
}
