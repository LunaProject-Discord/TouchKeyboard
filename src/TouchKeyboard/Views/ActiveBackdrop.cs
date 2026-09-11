using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TouchKeyboard.Settings;

namespace TouchKeyboard.Views;

/// <summary>
/// 非アクティブでも消えないバックドロップ。<see cref="SystemBackdropElement"/> に渡す。
///
/// 既製の <c>MicaBackdrop</c> や <c>DesktopAcrylicBackdrop</c> は、ウィンドウの
/// アクティブ状態に応じて <see cref="SystemBackdropConfiguration.IsInputActive"/> を
/// 切り替える。このアプリは WS_EX_NOACTIVATE で常に非アクティブなので、
/// そのままでは素材が出ず、パネルが透けてしまう。
///
/// ウィンドウ本体で <see cref="WindowBackdrop"/> がしている固定を、要素側でも行う。
/// </summary>
public sealed partial class ActiveBackdrop : SystemBackdrop
{
    private readonly BackdropMaterial _material;
    private readonly SystemBackdropConfiguration _configuration;

    private ISystemBackdropControllerWithTargets? _controller;

    public ActiveBackdrop(BackdropMaterial material, bool isDark)
    {
        _material = material;

        _configuration = new SystemBackdropConfiguration
        {
            // ここが肝。アクティブ状態に関係なく「入力中」として扱わせる。
            IsInputActive = true,
            Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        };
    }

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        try
        {
            _controller = Create(_material);
            if (_controller is null) return;

            _controller.AddSystemBackdropTarget(connectedTarget);
            _controller.SetSystemBackdropConfiguration(_configuration);
        }
        catch (Exception)
        {
            // 素材が敷けなくても、呼び出し側が単色で塗ってあるので読めなくはならない。
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        try
        {
            _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        }
        catch (Exception)
        {
            // 破棄の途中で失敗しても続行する。
        }
        finally
        {
            _controller?.Dispose();
            _controller = null;
        }
    }

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
}
