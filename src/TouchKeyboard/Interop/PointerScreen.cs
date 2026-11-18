namespace TouchKeyboard.Interop;

/// <summary>
/// ポインタの画面上の位置を読む。物理ピクセル。
///
/// XAML のポインタ通知が渡す位置はウィンドウ内の座標で、ウィンドウを動かしている
/// 最中は基準がずれる。動かした直後の通知が、動かす前と後のどちらのウィンドウを
/// 基準にしているかが揃わず、ウィンドウを指に付いていかせる処理では動かした量が
/// 次の読み取りに跳ね返って揺れる。画面上の座標はウィンドウの位置に左右されない。
/// </summary>
public static class PointerScreen
{
    /// <summary>
    /// 画面上の位置を読む。
    /// </summary>
    /// <param name="pointerId">XAML の Pointer.PointerId。指とペンでは Win32 のポインタ ID と同じ。</param>
    /// <param name="isMouse">
    /// マウスか。マウスは Win32 のポインタとして届かないことがあるので、カーソル位置を読む。
    /// </param>
    /// <returns>
    /// 読めたら true。指とペンで読めなかったときは false。カーソルで代用はしない
    /// （タッチではカーソルが動かないので、別の場所を指す）。
    /// </returns>
    public static bool TryGet(uint pointerId, bool isMouse, out int x, out int y)
    {
        if (!isMouse)
        {
            if (NativeMethods.GetPointerInfo(pointerId, out var info))
            {
                x = info.ptPixelLocation.X;
                y = info.ptPixelLocation.Y;
                return true;
            }

            x = 0;
            y = 0;
            return false;
        }

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            x = cursor.X;
            y = cursor.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }
}
