using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TouchKeyboard.Diagnostics;
using TouchKeyboard.Interop;
using TouchKeyboard.Settings;

namespace TouchKeyboard.Views;

/// <summary>
/// タスクトレイ常駐（要件 F-6）。
///
/// WinUI 3 には標準の NotifyIcon が無いため、Shell_NotifyIcon と Win32 の
/// ポップアップメニューで組む。通知はメッセージ専用ウィンドウで受ける。
/// </summary>
/// <summary>メニューを出す位置と中身。</summary>
/// <param name="X">画面座標。物理ピクセル。</param>
/// <param name="Y">同上。</param>
/// <param name="Items">上から並べる項目。</param>
public sealed record TrayMenuRequest(int X, int Y, IReadOnlyList<TrayMenuItem> Items);

public sealed class TrayIcon : IDisposable
{
    /// <summary>
    /// 利用者に見せる名前。ホバーしたときの吹き出しと通知に使う。
    /// クラス名やプロセス名とは別に持つ。あちらは実装側の識別子。
    /// </summary>
    private const string DisplayName = "タッチキーボード";

    private const string WindowClassName = "TouchKeyboard.TrayWindow";
    private const uint CallbackMessage = 0x0400 + 1; // WM_APP + 1
    private const uint IconId = 1;

    private readonly AppSettings _settings;

    /// <summary>GC に回収されると WndProc が消えるため、デリゲートを保持する。</summary>
    private readonly ShellNotify.WndProc _wndProc;

    private nint _hwnd;
    private nint _hIcon;
    private bool _disposed;

    /// <summary>メニューに並べたモニタ。選択された ID から引くために保持する。</summary>
    private readonly List<MonitorInfo> _monitors = [];

    /// <summary>
    /// メニューを出してほしい。座標と並べる項目を渡す。
    ///
    /// 描くのはこのクラスの仕事にしない。WinUI の部品で組むには
    /// UI スレッドとウィンドウが要り、通知領域の受け口とは別の話になる。
    /// </summary>
    public event EventHandler<TrayMenuRequest>? MenuRequested;

    public event EventHandler? ToggleRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<string>? MonitorSelected;
    public event EventHandler<bool>? AutoShowChanged;
    public event EventHandler<bool>? CoverTaskbarChanged;

    public TrayIcon(AppSettings settings)
    {
        _settings = settings;
        _wndProc = HandleMessage;

        CreateMessageWindow();
        _hIcon = CreateIcon();
        AddNotifyIcon();
    }

    // ------------------------------------------------------------------
    // メッセージ専用ウィンドウ
    // ------------------------------------------------------------------

    private void CreateMessageWindow()
    {
        var instance = ShellNotify.GetModuleHandleW(null);

        var cls = new ShellNotify.WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = WindowClassName,
        };

        // 既に登録済みでも失敗するだけなので戻り値は見ない。
        ShellNotify.RegisterClassW(ref cls);

        _hwnd = ShellNotify.CreateWindowExW(
            0, WindowClassName, null, 0, 0, 0, 0, 0,
            ShellNotify.HWND_MESSAGE, 0, instance, 0);
    }

    private nint HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4 では lParam の下位ワードにイベントが入り、
            // wParam にメニューを出すべき画面座標が入る。
            //
            // カーソル位置を使ってはいけない。タッチではカーソルがタップ位置に
            // 追従しないため、メニューが見当違いの場所に出る。
            var evt = (uint)(lParam & 0xFFFF);
            var x = (short)(wParam & 0xFFFF);
            var y = (short)((wParam >> 16) & 0xFFFF);

            switch (evt)
            {
                case ShellNotify.WM_LBUTTONUP:
                    ToggleRequested?.Invoke(this, EventArgs.Empty);
                    return 0;

                case ShellNotify.WM_RBUTTONUP:
                    ShowMenu(x, y);
                    return 0;
            }

            return 0;
        }

        return ShellNotify.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ------------------------------------------------------------------
    // 通知領域アイコン
    // ------------------------------------------------------------------

    private void AddNotifyIcon()
    {
        var data = CreateData();
        data.uFlags = ShellNotify.NIF_MESSAGE | ShellNotify.NIF_ICON
                      | ShellNotify.NIF_TIP | ShellNotify.NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _hIcon;
        data.szTip = DisplayName;

        var added = ShellNotify.Shell_NotifyIconW(ShellNotify.NIM_ADD, ref data);

        // バージョン 4 にすると座標やイベントの受け取り方が現行仕様になる。
        var version = CreateData();
        version.uVersion = ShellNotify.NOTIFYICON_VERSION_4;
        var versioned = ShellNotify.Shell_NotifyIconW(ShellNotify.NIM_SETVERSION, ref version);

        // 版を上げると標準の吹き出しが抑制される。自前で描く前提の版のため。
        // 標準のものを使うので、版を上げたあとに明示して入れ直す。
        var tip = CreateData();
        tip.uFlags = ShellNotify.NIF_TIP | ShellNotify.NIF_SHOWTIP;
        tip.szTip = DisplayName;

        var tipped = ShellNotify.Shell_NotifyIconW(ShellNotify.NIM_MODIFY, ref tip);

        TraceLog.Write(
            $"通知領域  追加={added} 版={versioned} 吹き出し={tipped} 文字=\"{DisplayName}\"");
    }

    /// <summary>
    /// アイコンを作り直して差し替える。
    ///
    /// 大きさは拡大率で変わる。作った時点の寸法のままだと、
    /// 拡大率を変えたあとシェルが引き伸ばすことになり、輪郭がにじむ。
    ///
    /// 自動表示が働かない状態（物理キーボードが繋がっている、設定で切ってある）
    /// かどうかも、呼ばれるたびに <see cref="IsAutoShowSuppressed"/> で見直す。
    /// キーボードの着脱は WM_SETTINGCHANGE 経由でこのメソッドが呼ばれるため、
    /// 別に着脱専用の通知を持つ必要はない。
    /// </summary>
    public void RefreshIcon()
    {
        var replacement = CreateIcon();
        if (replacement == 0) return;

        var previous = _hIcon;
        _hIcon = replacement;

        var data = CreateData();
        data.uFlags = ShellNotify.NIF_ICON;
        data.hIcon = _hIcon;

        ShellNotify.Shell_NotifyIconW(ShellNotify.NIM_MODIFY, ref data);

        // 差し替えたあとに捨てる。先に捨てるとシェルが古い方を触る。
        if (previous != 0) ShellNotify.DestroyIcon(previous);
    }

    /// <summary>
    /// 隠したまま起動したことを知らせる。
    /// ウィンドウが出ないと起動に失敗したように見えるため、常駐している旨を伝える。
    /// </summary>
    public void NotifyRunningHidden()
    {
        var data = CreateData();
        data.uFlags = ShellNotify.NIF_INFO;
        data.szInfoTitle = DisplayName;
        data.szInfo = "非表示で起動しました。通知領域のアイコンをクリックすると表示します。";

        ShellNotify.Shell_NotifyIconW(ShellNotify.NIM_MODIFY, ref data);
    }

    private ShellNotify.NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<ShellNotify.NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    // ------------------------------------------------------------------
    // メニュー
    // ------------------------------------------------------------------

    /// <param name="x">シェルが通知してきた画面座標。アイコンの位置が取れなかったときに使う。</param>
    /// <param name="y">同上。</param>
    private void ShowMenu(int x, int y)
    {
        var anchor = IconAnchor() ?? (x, y);
        MenuRequested?.Invoke(this, new TrayMenuRequest(anchor.Item1, anchor.Item2, BuildMenu()));
    }

    /// <summary>
    /// アイコンの右上。メニューはここを起点に開く。
    ///
    /// 通知してきた座標はカーソルの位置になる。マウスならアイコンの上だが、
    /// タッチやキーボードから開いた場合はそこに無い。
    /// 取れなければ null を返し、呼び出し側が通知された座標に戻す。
    /// </summary>
    private (int, int)? IconAnchor()
    {
        var id = new ShellNotify.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<ShellNotify.NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = IconId,
        };

        // 戻り値は HRESULT。隠れているアイコンなどでは失敗する。
        var hr = ShellNotify.Shell_NotifyIconGetRect(ref id, out var rect);

        if (hr == 0 && rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            return (rect.Right, rect.Top);
        }

        // 取れなかったときだけ記録する。毎回書くと、他を追うときに埋もれる。
        TraceLog.Write(
            $"通知領域の位置が取れません  hr=0x{hr:X8} " +
            $"rect={rect.Left},{rect.Top}-{rect.Right},{rect.Bottom}");

        return null;
    }

    /// <summary>
    /// 並べる項目を組む。
    ///
    /// モニタ構成は動的に変わるため、開くたびに列挙する。
    /// 入／切は開いた時点の値を焼き込む。押されるまでの間に変わることはない。
    /// </summary>
    private List<TrayMenuItem> BuildMenu()
    {
        var items = new List<TrayMenuItem>
        {
            new()
            {
                Text = "表示 / 非表示",
                Icon = "E765",
                Invoke = () => ToggleRequested?.Invoke(this, EventArgs.Empty),
            },
            TrayMenuItem.Separator,
            new()
            {
                Text = "入力欄で自動表示",
                IsCheckable = true,
                IsChecked = _settings.AutoShow,
                Invoke = () => AutoShowChanged?.Invoke(this, !_settings.AutoShow),
            },
            new()
            {
                Text = "Windows 起動時に開始",
                IsCheckable = true,
                IsChecked = AppSettings.IsAutoStartEnabled(),
                Invoke = () => AppSettings.SetAutoStart(!AppSettings.IsAutoStartEnabled()),
            },
            new()
            {
                Text = "タスクバーを覆う",
                IsCheckable = true,
                IsChecked = _settings.CoverTaskbar,
                Invoke = () => CoverTaskbarChanged?.Invoke(this, !_settings.CoverTaskbar),
            },
            TrayMenuItem.Separator,
        };

        AppendMonitors(items);

        items.Add(TrayMenuItem.Separator);

        items.Add(new TrayMenuItem
        {
            Text = "設定",
            Icon = "E713",
            Invoke = () => SettingsRequested?.Invoke(this, EventArgs.Empty),
        });

        items.Add(new TrayMenuItem
        {
            Text = "終了",
            Icon = "E7E8",
            Invoke = () => ExitRequested?.Invoke(this, EventArgs.Empty),
        });

        return items;
    }

    /// <summary>
    /// ドッキング先のモニタを並べる。
    ///
    /// 入れ子のメニューにはしない。指で開く相手として、
    /// 触れた瞬間に開く階層は扱いにくい。数も多くない。
    /// </summary>
    private void AppendMonitors(List<TrayMenuItem> items)
    {
        _monitors.Clear();
        _monitors.AddRange(MonitorInfo.All());

        if (_monitors.Count == 0)
        {
            items.Add(new TrayMenuItem
            {
                Text = "（モニタが見つかりません）",
                IsEnabled = false,
            });

            return;
        }

        foreach (var monitor in _monitors)
        {
            var name = monitor.DeviceName;

            items.Add(new TrayMenuItem
            {
                Text = monitor.DisplayName,
                IsCheckable = true,
                IsChecked = string.Equals(
                    _settings.MonitorDeviceName, name, StringComparison.OrdinalIgnoreCase),
                Invoke = () => MonitorSelected?.Invoke(this, name),
            });
        }
    }

    // ------------------------------------------------------------------
    // アイコン
    // ------------------------------------------------------------------

    /// <summary>
    /// Segoe Fluent Icons の Keyboard。実物を描き出して確認した値。
    ///
    /// 輪郭の方を使う。通知領域に並ぶ他のアイコンも輪郭で描かれており、
    /// 塗りつぶしだけが混ざると浮く。
    /// </summary>
    private const int KeyboardGlyph = 0xE765;

    /// <summary>
    /// いま入力欄へ触れても自動表示が働かないか。
    ///
    /// 物理キーボードが繋がっていれば <see cref="App"/> 側で自動表示を見送る
    /// （画面を占有し続ける理由が無いため）。設定で自動表示そのものを切って
    /// ある場合も同様。どちらも「触れても出てこない」状態なので、通知領域の
    /// アイコンを薄くして知らせる（利用者の指示：「キーボードが接続されている
    /// 状態などで自動で表示する条件ではない場合は、タスクトレイのアイコンの
    /// 前景色を薄くしてください」）。
    /// </summary>
    private bool IsAutoShowSuppressed() =>
        !_settings.AutoShow || InputDevices.HasPhysicalKeyboard();

    /// <summary>
    /// 通知領域のアイコンを実行時に作る。
    ///
    /// アイコンファイルは持たない。字面をそのまま焼けば、大きさも太さも
    /// 他の通知領域のアイコンと揃う。
    ///
    /// GDI の文字描画は 32bit の DIB に対しても α を書かない。
    /// 黒地に白で描き、その明るさをそのまま α として埋め直す。
    /// 白い字は覆った量がそのまま明るさになるので、これで縁が滑らかに出る。
    /// </summary>
    private nint CreateIcon()
    {
        // 通知領域が使う大きさで作る。
        //
        // 大きく作って縮めさせると、輪郭の細い字は線が潰れてにじむ。
        // シェルが求める寸法をそのまま使えば縮小が挟まらない。
        var size = Math.Max(
            ShellNotify.GetSystemMetrics(ShellNotify.SM_CXSMICON),
            ShellNotify.GetSystemMetrics(ShellNotify.SM_CYSMICON));

        if (size <= 0) size = 16;

        var header = new ShellNotify.BITMAPINFO
        {
            bmiHeader = new ShellNotify.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<ShellNotify.BITMAPINFOHEADER>(),
                biWidth = size,

                // 負にすると上から下へ並ぶ。読み書きの向きを揃えるため。
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };

        var dc = ShellNotify.CreateCompatibleDC(0);
        if (dc == 0) return 0;

        nint color = 0;
        nint font = 0;

        try
        {
            color = ShellNotify.CreateDIBSection(
                dc, ref header, ShellNotify.DIB_RGB_COLORS, out var bits, 0, 0);

            if (color == 0 || bits == 0) return 0;

            var previousBitmap = ShellNotify.SelectObject(dc, color);

            var glyph = char.ConvertFromUtf32(KeyboardGlyph);

            // 字面は em ボックスいっぱいには広がらない。
            // 高さを指定しただけでは枠に対して小さく見えるので、実際に測って合わせる。
            font = FitFont(dc, glyph, size, out var extent);
            if (font == 0) return 0;

            var previousFont = ShellNotify.SelectObject(dc, font);

            ShellNotify.SetBkMode(dc, ShellNotify.TRANSPARENT);
            ShellNotify.SetTextColor(dc, 0x00FFFFFF);
            ShellNotify.SetTextAlign(dc, ShellNotify.TA_CENTER);

            // 横は TA_CENTER が合わせる。縦は測った高さから自分で寄せる。
            ShellNotify.TextOutW(dc, size / 2, (size - extent.cy) / 2, glyph, 1);

            // 描き終えるまで内容は確定しない。
            ShellNotify.SelectObject(dc, previousBitmap);
            if (previousFont != 0) ShellNotify.SelectObject(dc, previousFont);

            // タスクバーの明暗に合わせる。
            //
            // Windows は「アプリ用」と「システム用」を別々に設定できる。
            // タスクバーはシステム用に従うので、そちらを見る。
            FillAlphaFromLuminance(bits, size, dark: Theme.SystemIsDark, dimmed: IsAutoShowSuppressed());

            // 字面の中心と、実際に描かれた形の中心はずれる。
            // 上下の余白は字ごとに違うので、描いたあとに測って寄せ直す。
            CenterInk(bits, size);

            var mask = ShellNotify.CreateBitmap(size, size, 1, 1, 0);

            try
            {
                var info = new ShellNotify.ICONINFO
                {
                    fIcon = true,
                    hbmMask = mask,
                    hbmColor = color,
                };

                return ShellNotify.CreateIconIndirect(ref info);
            }
            finally
            {
                ShellNotify.DeleteObject(mask);
            }
        }
        finally
        {
            if (font != 0) ShellNotify.DeleteObject(font);
            if (color != 0) ShellNotify.DeleteObject(color);
            ShellNotify.DeleteDC(dc);
        }
    }

    /// <summary>
    /// 枠にできるだけ大きく収まる字の大きさを選ぶ。
    ///
    /// 字面の縦横比は字ごとに違う。高さを枠に合わせても、横に長い字なら
    /// 先にはみ出す。1 度測って、はみ出した割合ぶんだけ縮め直す。
    /// </summary>
    /// <param name="extent">選んだ大きさで実際に占める幅と高さ。</param>
    private static nint FitFont(nint dc, string glyph, int size, out ShellNotify.SIZE extent)
    {
        extent = default;

        var height = size;
        nint font = 0;

        // 2 周で足りる。1 周目で縮める割合が決まり、2 周目でその結果を確かめる。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var candidate = ShellNotify.CreateFontW(
                -height, 0, 0, 0, 400, 0, 0, 0, ShellNotify.DEFAULT_CHARSET,
                0, 0, ShellNotify.ANTIALIASED_QUALITY, 0, "Segoe Fluent Icons");

            if (candidate == 0) return font;

            var previous = ShellNotify.SelectObject(dc, candidate);
            ShellNotify.GetTextExtentPoint32W(dc, glyph, 1, out var measured);
            ShellNotify.SelectObject(dc, previous);

            if (font != 0) ShellNotify.DeleteObject(font);
            font = candidate;
            extent = measured;

            var overflow = Math.Max(
                measured.cx / (double)size,
                measured.cy / (double)size);

            if (overflow <= 1.0) break;

            height = (int)Math.Floor(height / overflow);
            if (height < 8) break;
        }

        return font;
    }

    /// <summary>
    /// 明るさを α として埋め、色を塗り替える。
    ///
    /// 黒地に白で描いてあるので、書かれている値はそのまま覆った量になる。
    /// これを α にして、色は α を掛けた値で置く（乗算済みアルファ）。
    /// 白のままなら値は変わらない。暗い色なら 0 に近づく。
    /// </summary>
    /// <param name="dark">タスクバーが暗いか。暗ければ白、明るければ黒で描く。</param>
    /// <param name="dimmed">
    /// 自動表示が働かない状態か（<see cref="IsAutoShowSuppressed"/> 参照）。
    /// 真なら α を <see cref="DimAlphaFactor"/> 倍に落として前景色を薄くする。
    /// 乗算済みアルファなので、色（<c>value</c>）側も同じ倍率で落とさないと
    /// 縁だけ元の濃さのまま残っておかしくなる。
    /// </param>
    private static void FillAlphaFromLuminance(nint bits, int size, bool dark, bool dimmed)
    {
        var count = size * size * 4;
        var pixels = new byte[count];

        Marshal.Copy(bits, pixels, 0, count);

        // 明るいタスクバーでは白い字が見えない。暗い側へ振る。
        var tone = dark ? 0xFF : 0x00;

        for (var i = 0; i < count; i += 4)
        {
            // BGRA。白で描いたので 3 つとも同じ値になる。1 つ読めば足りる。
            var coverage = pixels[i];
            var value = (byte)(tone * coverage / 255);
            var alpha = coverage;

            if (dimmed)
            {
                value = (byte)(value * DimAlphaFactor);
                alpha = (byte)(alpha * DimAlphaFactor);
            }

            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = alpha;
        }

        Marshal.Copy(pixels, 0, bits, count);
    }

    /// <summary>
    /// 自動表示が働かないときの前景色の薄さ。1 が元の濃さ、0 が透明。
    /// 消えて見えないほどではなく、一目で「今は違う」と分かる程度に絞った。
    /// </summary>
    private const double DimAlphaFactor = 0.4;

    /// <summary>
    /// 描かれた形が枠の中央に来るようにずらす。
    ///
    /// 字面の枠には上下に余白が含まれる。中央に置いたつもりでも、
    /// 形そのものは上か下に寄る。α が入っている範囲を測って寄せ直す。
    /// </summary>
    private static void CenterInk(nint bits, int size)
    {
        var count = size * size * 4;
        var pixels = new byte[count];

        Marshal.Copy(bits, pixels, 0, count);

        int left = size, top = size, right = -1, bottom = -1;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (pixels[(((y * size) + x) * 4) + 3] == 0) continue;

                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }

        // 何も描かれていない。触らない。
        if (right < 0) return;

        var dx = ((size - 1 - right) - left) / 2;
        var dy = ((size - 1 - bottom) - top) / 2;

        if (dx == 0 && dy == 0) return;

        var moved = new byte[count];

        for (var y = 0; y < size; y++)
        {
            var sourceY = y - dy;
            if (sourceY < 0 || sourceY >= size) continue;

            for (var x = 0; x < size; x++)
            {
                var sourceX = x - dx;
                if (sourceX < 0 || sourceX >= size) continue;

                Array.Copy(
                    pixels, ((sourceY * size) + sourceX) * 4,
                    moved, ((y * size) + x) * 4,
                    4);
            }
        }

        Marshal.Copy(moved, 0, bits, count);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var data = CreateData();
        ShellNotify.Shell_NotifyIconW(ShellNotify.NIM_DELETE, ref data);

        if (_hIcon != 0)
        {
            ShellNotify.DestroyIcon(_hIcon);
            _hIcon = 0;
        }

        if (_hwnd != 0)
        {
            ShellNotify.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }
}
