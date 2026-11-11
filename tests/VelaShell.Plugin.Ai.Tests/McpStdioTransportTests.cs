using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.StdioFixture;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class McpStdioTransportTests
{
    [TestMethod]
    [DataRow(McpEnvironmentInheritMode.Minimal, false, null)]
    [DataRow(McpEnvironmentInheritMode.None, false, null)]
    [DataRow(McpEnvironmentInheritMode.All, false, "parent-value")]
    [DataRow(McpEnvironmentInheritMode.Minimal, true, "explicit-value")]
    [DataRow(McpEnvironmentInheritMode.None, true, "explicit-value")]
    [DataRow(McpEnvironmentInheritMode.All, true, "explicit-value")]
    public async Task RealProcess_ReceivesConfiguredDirectoryAndEnvironment(
        McpEnvironmentInheritMode mode, bool explicitValue, string? expected)
    {
        using var fixture = new Fixture();
        Environment.SetEnvironmentVariable(fixture.Variable, "parent-value");
        try
        {
            using var context = new TestPluginContext();
            await using var manager = new McpManager(context);
            fixture.Config.EnvironmentInheritMode = mode;
            fixture.Config.EnvironmentVariables = explicitValue ? fixture.Variable + "=explicit-value" : "";
            await using var lease = await manager.AcquireToolsAsync([fixture.Config],
                new McpApproval(null, ApprovalMode.Bypass), fixture.Token);
            using JsonDocument data = await InspectAsync(lease, fixture.Token);
            Assert.AreEqual(expected, data.RootElement.GetProperty("value").GetString());
            Assert.AreEqual(CanonicalPath(fixture.Directory),
                CanonicalPath(data.RootElement.GetProperty("directory").GetString()!));
            Assert.AreEqual("Ready", manager.GetSnapshot(fixture.Config.Id).State);
            using Process process = Process.GetProcessById(data.RootElement.GetProperty("pid").GetInt32());
            await manager.StopAsync(fixture.Config.Id, fixture.Token);
            await process.WaitForExitAsync(fixture.Token);
            Assert.AreEqual("Stopped", manager.GetSnapshot(fixture.Config.Id).State);
        }
        finally { Environment.SetEnvironmentVariable(fixture.Variable, null); }
    }

    [TestMethod]
    public async Task RealProcess_RestartInvalidatesOldTools_AndDisableExitsReplacement()
    {
        using var fixture = new Fixture();
        using var context = new TestPluginContext();
        await using var manager = new McpManager(context);
        var approval = new McpApproval(null, ApprovalMode.Bypass);
        await using var first = await manager.AcquireToolsAsync([fixture.Config], approval, fixture.Token);
        using JsonDocument firstData = await InspectAsync(first, fixture.Token);
        using Process oldProcess = Process.GetProcessById(firstData.RootElement.GetProperty("pid").GetInt32());
        await manager.RestartAsync(fixture.Config, fixture.Token);
        await oldProcess.WaitForExitAsync(fixture.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await first.Tools.OfType<AIFunction>().Single().InvokeAsync(new AIFunctionArguments(), fixture.Token));
        await using var second = await manager.AcquireToolsAsync([fixture.Config], approval, fixture.Token);
        using JsonDocument secondData = await InspectAsync(second, fixture.Token);
        using Process replacement = Process.GetProcessById(secondData.RootElement.GetProperty("pid").GetInt32());
        await first.DisposeAsync();
        Assert.IsFalse(replacement.HasExited, "An old lease must not close the restarted connection.");
        fixture.Config.Enabled = false;
        await manager.ReconcileAsync([fixture.Config]).WaitAsync(fixture.Token);
        await replacement.WaitForExitAsync(fixture.Token);
        Assert.AreEqual("Stopped", manager.GetSnapshot(fixture.Config.Id).State);
    }

    [TestMethod]
    public async Task RealProcess_LastLeaseReleaseExitsNonKeepAliveServer()
    {
        using var fixture = new Fixture();
        using var context = new TestPluginContext();
        await using var manager = new McpManager(context);
        fixture.Config.KeepAlive = false;
        var approval = new McpApproval(null, ApprovalMode.Bypass);
        await using var first = await manager.AcquireToolsAsync([fixture.Config], approval, fixture.Token);
        await using var second = await manager.AcquireToolsAsync([fixture.Config], approval, fixture.Token);
        using JsonDocument data = await InspectAsync(first, fixture.Token);
        using Process process = Process.GetProcessById(data.RootElement.GetProperty("pid").GetInt32());
        await first.DisposeAsync();
        using JsonDocument stillAlive = await InspectAsync(second, fixture.Token);
        Assert.IsFalse(process.HasExited);
        await second.DisposeAsync();
        await process.WaitForExitAsync(fixture.Token);
        Assert.AreEqual("Stopped", manager.GetSnapshot(fixture.Config.Id).State);
    }

    [TestMethod]
    public async Task RealProcess_StartupTimeoutReapsUnresponsiveChild()
    {
        using var fixture = new Fixture();
        using var context = new TestPluginContext();
        await using var manager = new McpManager(context);
        fixture.Config.Arguments += " --hang";
        fixture.Config.StartupTimeoutSeconds = 2;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(fixture.Config, fixture.Token));
        Assert.AreEqual("MCP startup timed out.", error.Message);
        Assert.IsFalse(fixture.Token.IsCancellationRequested);
        Assert.IsTrue(File.Exists(fixture.PidFile), "The fixture must have started before the handshake timed out.");
        int pid = int.Parse(await File.ReadAllTextAsync(fixture.PidFile, fixture.Token));
        try
        {
            using Process process = Process.GetProcessById(pid);
            await process.WaitForExitAsync(fixture.Token);
        }
        catch (ArgumentException) { /* The SDK already reaped this test process. */ }
        Assert.AreEqual("Failed", manager.GetSnapshot(fixture.Config.Id).State);
    }

    private static async Task<JsonDocument> InspectAsync(McpManager.ToolLease lease, CancellationToken token)
    {
        Assert.HasCount(1, lease.Tools, string.Join(Environment.NewLine, lease.Errors));
        object? raw = await lease.Tools.OfType<AIFunction>().Single().InvokeAsync(new AIFunctionArguments(), token);
        TextContent result = Assert.IsInstanceOfType<TextContent>(raw);
        return JsonDocument.Parse(result.Text);
    }

    private static string CanonicalPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)!;
        string current = root;

        foreach (string segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            DirectoryInfo directory = new(current);
            string? target = directory.LinkTarget;
            if (target is null)
            {
                continue;
            }

            current = Path.GetFullPath(Path.IsPathFullyQualified(target)
                ? target
                : Path.Combine(directory.Parent?.FullName ?? root, target));
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "vela mcp stdio " + Guid.NewGuid().ToString("N"));
        public string Variable { get; } = "VELA_MCP_TEST_" + Guid.NewGuid().ToString("N");
        public string PidFile => Path.Combine(Directory, "process.id");
        public CancellationToken Token => _timeout.Token;
        public McpServerConfig Config { get; }

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
                "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            Config = new McpServerConfig
            {
                Name = "stdio-fixture",
                Transport = McpTransportType.Stdio,
                Command = dotnet,
                Arguments = $"\"{typeof(FixtureHost).Assembly.Location}\" \"{PidFile}\" {Variable}",
                WorkingDirectory = Directory
            };
        }

        public void Dispose()
        {
            _timeout.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
