using System;

namespace TouchKeyboard.Views;

/// <summary>
/// 通知領域のメニューに並べる 1 項目。
///
/// 表示の都合と実際の動作をここで一組にしておく。
/// メニューを組む側（<see cref="TrayIcon"/>）と描く側（<see cref="TrayMenuWindow"/>）が
/// 別々に ID を突き合わせる形にすると、増やすたびに両方を直すことになる。
/// </summary>
public sealed class TrayMenuItem
{
    /// <summary>区切り線。文字も動作も持たない。</summary>
    public static TrayMenuItem Separator { get; } = new() { IsSeparator = true };

    public bool IsSeparator { get; private init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>Segoe Fluent Icons のコードポイント。null なら字面を描かない。</summary>
    public string? Icon { get; init; }

    /// <summary>入／切のある項目か。true のときは <see cref="IsChecked"/> を見て印を出す。</summary>
    public bool IsCheckable { get; init; }

    public bool IsChecked { get; init; }

    /// <summary>false なら灰色にして押せなくする。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>選ばれたときに行うこと。</summary>
    public Action? Invoke { get; init; }
}
