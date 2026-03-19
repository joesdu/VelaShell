using PulseTerm.Controls.Controls;

namespace PulseTerm.Controls.Tests;

public sealed class StatusMetricChipTests
{
    [Fact]
    public void Label_And_Value_CanBeAssigned()
    {
        var control = new StatusMetricChip
        {
            Label = "Latency",
            Value = "24ms"
        };

        Assert.Equal("Latency", control.Label);
        Assert.Equal("24ms", control.Value);
    }
}
