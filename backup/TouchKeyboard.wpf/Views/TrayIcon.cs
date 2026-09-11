using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TouchKeyboard.Interop;
using TouchKeyboard.Settings;

namespace TouchKeyboard.Views;

/// <summary>
/// タスクトレイ常駐。ここから表示切替と終了ができる（要件 F-6）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    private readonly NotifyIcon _notifyIcon;
    private readonly AppSettings _settings;
    private nint _iconHandle;
    private bool _disposed;

    /// <summary>表示／非表示の切り替えを要求された。</summary>
    public event EventHandler? ToggleRequested;

    /// <summary>終了を要求された。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>ドッキング先モニタが選択された。デバイス名を渡す。</summary>
    public event EventHandler<string>? MonitorSelected;

    /// <summary>自動表示の設定が変更された。</summary>
    public event EventHandler<bool>? AutoShowChanged;

    /// <summary>タスクバーを覆う設定が変更された。</summary>
    public event EventHandler<bool>? CoverTaskbarChanged;

    public TrayIcon(AppSettings settings)
    {
        _settings = settings;

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "TouchKeyboard",
            Visible = true,
        };

        // シングルクリックで表示切替。物理キーボードが無い環境でも扱いやすいようにする。
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleRequested?.Invoke(this, EventArgs.Empty);
        };

        _notifyIcon.ContextMenuStrip = BuildMenu();
        _notifyIcon.ContextMenuStrip.Opening += (_, _) => RefreshMenu();
    }

    /// <summary>
    /// 隠したまま起動したことを知らせる。
    /// ウィンドウが出ないと起動に失敗したように見えるため、常駐している旨を伝える。
    /// </summary>
    public void NotifyRunningHidden()
    {
        _notifyIcon.BalloonTipTitle = "TouchKeyboard";
        _notifyIcon.BalloonTipText = "非表示で起動しました。通知領域のアイコンをクリックすると表示します。";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.None;
        _notifyIcon.ShowBalloonTip(3000);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(new ToolStripMenuItem("表示 / 非表示", null,
            (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty))
        {
            Name = "toggle",
        });

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("入力欄で自動表示", null, (sender, _) =>
        {
            if (sender is not ToolStripMenuItem item) return;
            item.Checked = !item.Checked;
            AutoShowChanged?.Invoke(this, item.Checked);
        })
        {
            Name = "autoShow",
            Checked = _settings.AutoShow,
        });

        menu.Items.Add(new ToolStripMenuItem("Windows 起動時に開始", null, (sender, _) =>
        {
            if (sender is not ToolStripMenuItem item) return;
            item.Checked = !item.Checked;
            AppSettings.SetAutoStart(item.Checked);
        })
        {
            Name = "autoStart",
            Checked = AppSettings.IsAutoStartEnabled(),
        });

        menu.Items.Add(new ToolStripMenuItem("タスクバーを覆う", null, (sender, _) =>
        {
            if (sender is not ToolStripMenuItem item) return;
            item.Checked = !item.Checked;
            CoverTaskbarChanged?.Invoke(this, item.Checked);
        })
        {
            Name = "coverTaskbar",
            Checked = _settings.CoverTaskbar,
        });

        menu.Items.Add(new ToolStripMenuItem("ドッキング先モニタ") { Name = "monitors" });

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("終了", null,
            (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        return menu;
    }

    /// <summary>
    /// メニューを開くたびに状態を取り直す。
    /// モニタ構成は動的に変わるため、開いた時点で列挙する。
    /// </summary>
    private void RefreshMenu()
    {
        var menu = _notifyIcon.ContextMenuStrip;
        if (menu is null) return;

        if (menu.Items["autoShow"] is ToolStripMenuItem autoShow)
        {
            autoShow.Checked = _settings.AutoShow;
        }

        if (menu.Items["autoStart"] is ToolStripMenuItem autoStart)
        {
            autoStart.Checked = AppSettings.IsAutoStartEnabled();
        }

        if (menu.Items["coverTaskbar"] is ToolStripMenuItem cover)
        {
            cover.Checked = _settings.CoverTaskbar;
        }

        if (menu.Items["monitors"] is not ToolStripMenuItem monitors) return;

        monitors.DropDownItems.Clear();

        foreach (var monitor in MonitorInfo.All())
        {
            var deviceName = monitor.DeviceName;

            monitors.DropDownItems.Add(new ToolStripMenuItem(
                monitor.DisplayName, null, (_, _) => MonitorSelected?.Invoke(this, deviceName))
            {
                Checked = string.Equals(
                    _settings.MonitorDeviceName, deviceName, StringComparison.OrdinalIgnoreCase),
            });
        }

        if (monitors.DropDownItems.Count == 0)
        {
            monitors.DropDownItems.Add(new ToolStripMenuItem("（モニタが見つかりません）")
            {
                Enabled = false,
            });
        }
    }

    /// <summary>
    /// 仮アイコンを実行時に描く。アイコンファイルを持たずに済ませる。
    /// 正式なアイコンが用意できたら差し替える。
    /// </summary>
    private Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var body = new SolidBrush(Color.FromArgb(240, 245, 245, 245));
            g.FillRectangle(body, 2, 8, 28, 17);

            using var key = new SolidBrush(Color.FromArgb(255, 60, 60, 60));
            for (var row = 0; row < 2; row++)
            {
                for (var col = 0; col < 5; col++)
                {
                    g.FillRectangle(key, 5 + col * 5, 11 + row * 5, 3, 3);
                }
            }

            // 最下段はスペースバーに見立てて 1 本にする。
            g.FillRectangle(key, 10, 21, 12, 3);
        }

        _iconHandle = bitmap.GetHicon();
        return Icon.FromHandle(_iconHandle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        // GetHicon で得たハンドルは明示的に破棄する必要がある。
        if (_iconHandle != 0)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }
    }
}
