using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LabPet.Client.Interop;

namespace LabPet.Client.Views;

internal static class QuickMessageInputBehavior
{
    public static readonly TimeSpan DismissDelay = TimeSpan.FromMilliseconds(360);

    public static bool IsActive(UIElement surface)
        => surface.IsKeyboardFocusWithin || surface.IsMouseOver || surface.IsMouseCaptureWithin;

    public static void Focus(TextBox input)
    {
        Win32WindowHelper.ActivatePopupForKeyboardInput(input);
        input.Focus();
        Keyboard.Focus(input);
    }
}
