using System;
using Microsoft.UI.Xaml;
using TouchKeyboard.Interop;
using TouchKeyboard.Settings;
using Windows.Graphics;

namespace TouchKeyboard.Views;

/// <summary>
/// 浮かせている間に、縁を掴んで大きさを変える。
///
/// システムのリサイズ枠は使わない。<c>IsResizable</c> を立てると WS_THICKFRAME が付き、
/// 上端に線が出る。中身をタイトルバーまで伸ばしているため、その線は消せない。
/// 当たり判定を自前で置けば、枠のスタイルを変えずに済む。
/// </summary>
public sealed partial class KeyboardWindow
{
    private enum ResizeEdge
    {
        None,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    private ResizeEdge _edge;

    /// <summary>掴んだ時点のウィンドウの位置と大きさ。物理ピクセル。</summary>
    private RectInt32 _resizeStartBounds;

    /// <summary>掴んだ時点の指の位置。画面座標の物理ピクセル。</summary>
    private PointInt32 _resizeStartPoint;

    private bool _edgesWired;

    /// <summary>縁の当たり判定を出し入れする。</summary>
    private void SetResizeEdgesVisible(bool visible)
    {
        WireResizeEdges();

        var state = visible ? Visibility.Visible : Visibility.Collapsed;

        foreach (var strip in ResizeStrips()) strip.Visibility = state;
    }

    private FrameworkElement[] ResizeStrips() =>
    [
        ResizeLeft, ResizeRight, ResizeTop, ResizeBottom,
        ResizeTopLeft, ResizeTopRight, ResizeBottomLeft, ResizeBottomRight,
    ];

    private void WireResizeEdges()
    {
        if (_edgesWired) return;
        _edgesWired = true;

        Wire(ResizeLeft, ResizeEdge.Left);
        Wire(ResizeRight, ResizeEdge.Right);
        Wire(ResizeTop, ResizeEdge.Top);
        Wire(ResizeBottom, ResizeEdge.Bottom);
        Wire(ResizeTopLeft, ResizeEdge.TopLeft);
        Wire(ResizeTopRight, ResizeEdge.TopRight);
        Wire(ResizeBottomLeft, ResizeEdge.BottomLeft);
        Wire(ResizeBottomRight, ResizeEdge.BottomRight);

        void Wire(FrameworkElement strip, ResizeEdge edge)
        {
            strip.PointerPressed += (sender, e) =>
            {
                if (!IsFloating) return;

                _edge = edge;

                _resizeStartBounds = new RectInt32(
                    AppWindow.Position.X, AppWindow.Position.Y,
                    AppWindow.Size.Width, AppWindow.Size.Height);

                _resizeStartPoint = ScreenPoint(e.GetCurrentPoint(null).Position);

                ((UIElement)sender).CapturePointer(e.Pointer);
                e.Handled = true;
            };

            strip.PointerMoved += (_, e) =>
            {
                if (_edge == ResizeEdge.None) return;

                UpdateResizeEdge(ScreenPoint(e.GetCurrentPoint(null).Position));
                e.Handled = true;
            };

            strip.PointerReleased += (sender, e) =>
            {
                if (_edge == ResizeEdge.None) return;

                _edge = ResizeEdge.None;
                ((UIElement)sender).ReleasePointerCapture(e.Pointer);
                RememberFloatingBounds();
                e.Handled = true;
            };

            strip.PointerCaptureLost += (_, _) =>
            {
                if (_edge == ResizeEdge.None) return;

                _edge = ResizeEdge.None;
                RememberFloatingBounds();
            };
        }
    }

    /// <summary>ウィンドウ内の位置（DIP）を画面座標の物理ピクセルに直す。</summary>
    private PointInt32 ScreenPoint(Windows.Foundation.Point inWindow)
    {
        var scale = RasterizationScale();

        return new PointInt32(
            AppWindow.Position.X + (int)Math.Round(inWindow.X * scale),
            AppWindow.Position.Y + (int)Math.Round(inWindow.Y * scale));
    }

    private void UpdateResizeEdge(PointInt32 screen)
    {
        var scale = RasterizationScale();

        // 下限は印字が読めるところ。指で押す大きさとは別に決める。
        var minWidth = (int)Math.Round(AppSettings.MinFloatingWidthDip * scale);
        var minHeight = (int)Math.Round(
            (AppSettings.MinFloatingBodyDip + TitleBarHeightDip) * scale);

        var dx = screen.X - _resizeStartPoint.X;
        var dy = screen.Y - _resizeStartPoint.Y;

        var x = _resizeStartBounds.X;
        var y = _resizeStartBounds.Y;
        var width = _resizeStartBounds.Width;
        var height = _resizeStartBounds.Height;

        if (_edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft)
        {
            // 左端を動かすと位置も変わる。下限に達したら右端を固定して止める。
            var right = _resizeStartBounds.X + _resizeStartBounds.Width;
            x = Math.Min(_resizeStartBounds.X + dx, right - minWidth);
            width = right - x;
        }

        if (_edge is ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight)
        {
            width = Math.Max(minWidth, _resizeStartBounds.Width + dx);
        }

        if (_edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight)
        {
            // 上端も同じ。下端を固定したまま伸び縮みさせる。
            var bottom = _resizeStartBounds.Y + _resizeStartBounds.Height;
            y = Math.Min(_resizeStartBounds.Y + dy, bottom - minHeight);
            height = bottom - y;
        }

        if (_edge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight)
        {
            height = Math.Max(minHeight, _resizeStartBounds.Height + dy);
        }

        // 画面からはみ出させない。掴めない場所へ広がると戻す手段が無くなる。
        if (MonitorInfo.FromWindow(_hwnd) is { } monitor)
        {
            width = Math.Min(width, monitor.Bounds.Width);
            height = Math.Min(height, monitor.Bounds.Height);
            x = Math.Clamp(x, monitor.Bounds.Left, monitor.Bounds.Right - width);
            y = Math.Clamp(y, monitor.Bounds.Top, monitor.Bounds.Bottom - height);
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }
}
