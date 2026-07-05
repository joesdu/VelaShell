using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PulseTerm.App.Behaviors;

/// <summary>
/// Attached behavior that restricts a text input control to printable ASCII characters,
/// blocking IME-composed (e.g. Chinese) input. Apply with
/// <c>behaviors:AsciiOnlyInput.IsEnabled="True"</c> on password / credential fields.
/// </summary>
public static class AsciiOnlyInput
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Interactive, bool>(
            "IsEnabled", typeof(AsciiOnlyInput));

    public static bool GetIsEnabled(Interactive element) => element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Interactive element, bool value) => element.SetValue(IsEnabledProperty, value);

    static AsciiOnlyInput()
    {
        IsEnabledProperty.Changed.AddClassHandler<Interactive>((element, args) =>
        {
            if (args.NewValue is true)
                element.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
            else
                element.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        });
    }

    private static void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text) && !IsPrintableAscii(e.Text))
            e.Handled = true;
    }

    private static bool IsPrintableAscii(string text)
    {
        foreach (char c in text)
        {
            if (c is < ' ' or > '~')
                return false;
        }
        return true;
    }
}
