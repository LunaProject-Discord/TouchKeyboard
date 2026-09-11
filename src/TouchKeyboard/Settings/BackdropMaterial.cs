namespace TouchKeyboard.Settings;

/// <summary>ウィンドウの背景に敷く素材。</summary>
public enum BackdropMaterial
{
    /// <summary>
    /// 背後のウィンドウをぼかす。
    ///
    /// 標準タッチキーボードを実測したところ、DWM のシステムバックドロップは未設定で、
    /// コンポジションによる Acrylic だった。見え方を標準に寄せるならこちら。
    /// </summary>
    Acrylic = 0,

    /// <summary>
    /// デスクトップの壁紙を素材にする。背後のウィンドウはぼかさない。既定。
    ///
    /// 下が透けないぶん落ち着いて見え、キーも読みやすい。
    /// 標準とは違うが、常に手元にあるものなので見やすさを優先する。
    /// </summary>
    Mica = 1,

    /// <summary>Mica の濃いほう。</summary>
    MicaAlt = 2,
}
