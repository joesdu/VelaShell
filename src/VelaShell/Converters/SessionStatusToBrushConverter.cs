using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using VelaShell.Core.Models;

namespace VelaShell.Converters;

/// <summary>
/// Maps a <see cref="SessionStatus" /> to its status-dot brush. The design defines these as
/// theme-constant colors (status-connected/-connecting/-disconnected are identical in dark
/// and light), so fixed brushes are correct here.
/// </summary>
public sealed class SessionStatusToBrushConverter : IValueConverter
{
    public static readonly SessionStatusToBrushConverter Instance = new();

    private static readonly IBrush Connected = new SolidColorBrush(Color.Parse("#00D4AA"));
    private static readonly IBrush Connecting = new SolidColorBrush(Color.Parse("#FDCB6E"));
    private static readonly IBrush Disconnected = new SolidColorBrush(Color.Parse("#FF6B6B"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            SessionStatus.Connected => Connected,
            SessionStatus.Connecting => Connecting,
            _ => Disconnected
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
