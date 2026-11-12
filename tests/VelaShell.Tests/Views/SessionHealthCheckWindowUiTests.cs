using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Localization;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>会话健康巡检的结果窗口画得出来:每台一行,结论、卡在哪一步、说明各在其位;查完之后「停止」灰掉、「重新巡检」可点。</summary>
[TestClass]
[TestCategory("UI")]
public sealed class SessionHealthCheckWindowUiTests
{
    private static HeadlessUnitTestSession _session = null!;
    private static LocalizationService _localization = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SessionHealthCheckWindowUiTests).Assembly);
        _localization = new();
        LocalizedStrings.Instance.Attach(_localization);
    }

    [TestMethod]
    public void Window_ListsEveryHost_WithItsResultAndWhereItStopped()
    {
        OnUi(() =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            _localization.SetLanguage("zh-CN");
            IConnectionDiagnosticsService diagnostics = Substitute.For<IConnectionDiagnosticsService>();
            diagnostics.DiagnoseUnattendedAsync(Arg.Any<SessionProfile>(), Arg.Any<bool>(), Arg.Any<IProgress<DiagnosticStepUpdate>?>(), Arg.Any<CancellationToken>())
                       .Returns(call => call.Arg<SessionProfile>().Name switch
                       {
                           "web-01" => Report(DiagnosticStepStatus.Success, DiagnosticStepStatus.Success, null),
                           "web-02" => Report(DiagnosticStepStatus.Success, DiagnosticStepStatus.Warning, null),
                           "bastion" => Report(DiagnosticStepStatus.Success, DiagnosticStepStatus.Failed, "主机密钥校验失败",
                               new VelaSshHostKeyRejectedException("指纹待确认")),
                           _ => Report(DiagnosticStepStatus.Failed, DiagnosticStepStatus.Skipped, "TCP 建链失败"),
                       });
            var vm = new SessionHealthCheckViewModel(diagnostics);
            SessionProfile ftp = Profile("nas");
            ftp.ConnectionType = ConnectionType.FTP;
            vm.TryStart([Profile("web-01"), Profile("web-02"), Profile("bastion"), Profile("old-switch"), ftp]);
            // 替身同步完成,整批在 TryStart 里就查完了;剩下的续体在 UI 线程上排着。
            Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(vm.IsRunning);

            var window = new SessionHealthCheckWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            string[] texts = [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty)];
            foreach (string expected in new[]
                     {
                         Strings.Get("Health_StatusHealthy"), Strings.Get("Health_StatusNeedsCredentials"),
                         Strings.Get("Health_StatusNeedsAttention"), Strings.Get("Health_StatusFailed"),
                         Strings.Get("Health_StatusUnsupported"), "old-switch", "TCP", vm.Summary,
                     })
            {
                Assert.Contains(expected, texts, $"表上找不到「{expected}」");
            }
            Assert.IsFalse(window.FindControl<Button>("StopButton")!.IsEffectivelyEnabled, "查完了,「停止」灰掉。");
            Assert.IsTrue(window.FindControl<Button>("RerunButton")!.IsEffectivelyEnabled);

            SaveFrame(window, "session-health-check.png");
            window.Close();
        });
    }

    private static SessionProfile Profile(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Host = $"{name}.example.com",
        Username = "root",
        AuthMethod = AuthMethod.Password,
        Password = "secret",
    };

    private static DiagnosticReport Report(DiagnosticStepStatus tcp, DiagnosticStepStatus auth, string? issue, Exception? failure = null) => new()
    {
        Steps =
        [
            new(0, "DNS", DiagnosticStepStatus.Success),
            new(1, "TCP", tcp, tcp == DiagnosticStepStatus.Failed ? "Connection refused" : null),
            new(2, "SSH", tcp == DiagnosticStepStatus.Failed ? DiagnosticStepStatus.Skipped : DiagnosticStepStatus.Success),
            new(3, "认证", auth),
        ],
        IssueTitle = issue,
        Failure = failure,
    };

    private static void SaveFrame(TopLevel topLevel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame);
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }

    private static void OnUi(Action action) => _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}
