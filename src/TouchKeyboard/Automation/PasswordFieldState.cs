using System;
using System.Windows.Automation;

namespace TouchKeyboard.Automation;

/// <summary>いまフォーカスがある要素がパスワード欄かを読む。</summary>
public static class PasswordFieldState
{
    /// <summary>
    /// パスワード欄か。読めなければ null。
    ///
    /// フォーカス変更の通知は経由せず、呼ばれるたびに読みに行く。手動で
    /// キーボードを出したときや、同じウィンドウ内でパスワード欄と他の
    /// 入力欄を行き来する場合にも正しく追従させるため（KeyboardWindow の
    /// PollExternalState から定期的に呼ばれる）。
    /// </summary>
    public static bool? IsPassword()
    {
        try
        {
            return AutomationElement.FocusedElement?.Current.IsPassword;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
