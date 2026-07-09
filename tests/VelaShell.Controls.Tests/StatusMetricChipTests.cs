using VelaShell.Controls.Controls;

namespace VelaShell.Controls.Tests;

[TestClass]
public sealed class StatusMetricChipTests
{
    [TestMethod]
    public void Label_And_Value_CanBeAssigned()
    {
        var control = new StatusMetricChip
        {
            Label = "Latency",
            Value = "24ms"
        };

        Assert.AreEqual("Latency", control.Label);
        Assert.AreEqual("24ms", control.Value);
    }
}
