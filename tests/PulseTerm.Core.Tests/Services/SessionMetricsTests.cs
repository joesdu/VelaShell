using PulseTerm.Core.Services;

namespace PulseTerm.Core.Tests.Services;

[TestClass]
[TestCategory("Metrics")]
public class SessionMetricsTests
{
    private const string SampleOutput =
        "__P__\n8\n" +
        "__L__\n0.96 0.80 0.70 1/234 5678\n" +
        "__M__\n17179869184 4509715660\n" +
        "__D__\n549755813888 128849018880\n" +
        "__O__\nUbuntu 22.04.4 LTS\n" +
        "__K__\n6.8.0-40-generic\n";

    [TestMethod]
    public void Parse_FullOutput_MapsAllFields()
    {
        var m = SessionMetrics.Parse(SampleOutput);

        Assert.IsNotNull(m);
        Assert.AreEqual(8, m.CpuCores);
        Assert.AreEqual(12.0, m.CpuPercent, 0.1); // 0.96 / 8 cores = 12%
        Assert.AreEqual(17179869184L, m.MemTotalBytes);
        Assert.AreEqual(4509715660L, m.MemUsedBytes);
        Assert.AreEqual(549755813888L, m.DiskTotalBytes);
        Assert.AreEqual("Ubuntu 22.04.4 LTS", m.OsVersion);
        Assert.AreEqual("6.8.0-40-generic", m.Kernel);
        Assert.AreEqual(26.25, m.MemPercent, 0.1);
        Assert.AreEqual(23.4, m.DiskPercent, 0.1);
    }

    [TestMethod]
    public void Parse_PartialOutput_MissingSectionsDegradeGracefully()
    {
        var m = SessionMetrics.Parse("__P__\n4\n__L__\n2.0 1.0 0.5\n__M__\n__D__\n__O__\n__K__\n");

        Assert.IsNotNull(m);
        Assert.AreEqual(4, m.CpuCores);
        Assert.AreEqual(50.0, m.CpuPercent, 0.1);
        Assert.AreEqual(0, m.MemPercent);
    }

    [TestMethod]
    public void Parse_CpuPercent_ClampsAt100()
    {
        var m = SessionMetrics.Parse("__P__\n1\n__L__\n9.5 1.0 0.5\n__O__\nx\n");
        Assert.IsNotNull(m);
        Assert.AreEqual(100.0, m.CpuPercent);
    }

    [TestMethod]
    public void Parse_EmptyOrGarbage_ReturnsNull()
    {
        Assert.IsNull(SessionMetrics.Parse(""));
        Assert.IsNull(SessionMetrics.Parse("command not found"));
    }
}
