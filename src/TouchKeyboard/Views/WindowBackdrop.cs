using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using TouchKeyboard.Interop;
using TouchKeyboard.Settings;
using WinRT;

namespace TouchKeyboard.Views;

/// <summary>
/// ウィンドウに素材を適用する。
///
/// 簡易 API（<c>Window.SystemBackdrop = new DesktopAcrylicBackdrop()</c>）は使えない。
/// あちらはウィンドウのアクティブ状態に応じて
/// <see cref="SystemBackdropConfiguration.IsInputActive"/> を切り替えるため、
/// WS_EX_NOACTIVATE のウィンドウでは常に false になりぼかしが消える。
///
/// コントローラを直接使い、IsInputActive を true に固定して回避する。
/// すべて公開 API で完結する。
/// </summary>
public sealed class WindowBackdrop : IDisposable
{
    private ISystemBackdropControllerWithTargets? _controller;
    private SystemBackdropConfiguration? _configuration;
    private bool _disposed;

    /// <summary>適用できたか。false のときは呼び出し側で単色背景にフォールバックする。</summary>
    public bool IsApplied { get; private set; }

    /// <summary>適用できなかった場合の理由。診断用。</summary>
    public string Status { get; private set; } = "未適用";

    public WindowBackdrop(Window window, bool isDark, BackdropMaterial material)
    {
        try
        {
            // Mica・Acrylic のどちらも DWM/コンポジションが背後を継続的に描き直す。
            // 低電力モード中はどちらも使わず、呼び出し側（KeyboardWindow.ApplyBackdrop）が
            // 単色背景にフォールバックする（利用者の指示：「低電力モード中はどちらも
            // オフにしてください」）。
            if (PowerMode.IsLowPower)
            {
                Status = "低電力モード中のため素材を適用しません";
                return;
            }

            _controller = Create(material);

            if (_controller is null)
            {
                Status = $"この環境では {material} に対応していません";
                return;
            }

            _configuration = new SystemBackdropConfiguration
            {
                // ここが肝。アクティブ状態に関係なく「入力中」として扱わせる。
                IsInputActive = true,
                Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
            };

            _controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
            _controller.SetSystemBackdropConfiguration(_configuration);

            IsApplied = true;
            Status = _controller.GetType().Name;
        }
        catch (Exception ex)
        {
            Status = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// 素材ごとのコントローラ。対応していなければ null。
    ///
    /// Mica はデスクトップの壁紙を素材にする。手前のアプリはぼかさないため、
    /// キーボードの下にあるウィンドウの内容は見えない。標準タッチキーボードの実測では
    /// DWM のバックドロップが未設定で、コンポジションによる Acrylic だった。
    /// 既定を Acrylic にしているのはそのため。
    /// </summary>
    private static ISystemBackdropControllerWithTargets? Create(BackdropMaterial material) => material switch
    {
        BackdropMaterial.Mica when MicaController.IsSupported() =>
            new MicaController { Kind = MicaKind.Base },

        BackdropMaterial.MicaAlt when MicaController.IsSupported() =>
            new MicaController { Kind = MicaKind.BaseAlt },

        BackdropMaterial.Acrylic when DesktopAcrylicController.IsSupported() =>
            new DesktopAcrylicController(),

        _ => null,
    };

    /// <summary>OS のライト／ダーク切り替えに追従させる。</summary>
    public void SetTheme(bool isDark)
    {
        if (_configuration is null) return;

        _configuration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        // IsInputActive は true のまま維持する。ここを戻すとぼかしが消える。
        _configuration.IsInputActive = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _controller?.Dispose();
        _controller = null;
        _configuration = null;
        IsApplied = false;
    }
}
