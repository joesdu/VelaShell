using Avalonia.Media;
using VelaShell.Controls.Controls;

namespace VelaShell.Controls.Tests;

[TestClass]
public class LucideIconTests
{
    [TestMethod]
    public void NewIcon_HasNoGeometryOrBrushByDefault()
    {
        LucideIcon icon = new();
        Assert.IsNull(icon.Data);
        Assert.IsNull(icon.Foreground);
    }

    [TestMethod]
    public void ForegroundProperty_RoundTrips()
    {
        LucideIcon icon = new() { Foreground = Brushes.Red };
        Assert.AreSame(Brushes.Red, icon.Foreground);
    }
}
