using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Controls;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("DockMotionUi")]
public sealed class DockMotionUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DockMotionUiTests).Assembly);

    [TestMethod]
    public void ContentSwitch_UpdatesTargetImmediately_AndRetainsViewIdentity()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            var first = new TestDocument("first");
            var second = new TestDocument("second");
            workspace.AddDocument(first);
            workspace.AddDocument(second);

            var dock = new DockWorkspaceControl { Workspace = workspace };
            var window = new Window { Width = 640, Height = 360, Content = dock };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            ReparentingHost host = dock.GetVisualDescendants().OfType<ReparentingHost>().Single();
            Control secondView = second.View ?? throw new AssertFailedException("Second document view was not created.");
            Assert.AreSame(secondView, host.Target);
            Assert.AreSame(host.Target, host.Child);
            AssertDockContentMotion(host);
            SaveOptionalFrame(window, "dock-motion-second-active.png");

            workspace.ActivateDocument(first);
            Assert.AreSame(first.View, host.Target);
            Assert.AreSame(host.Target, host.Child);
            Assert.IsTrue(host.Classes.Contains("settling"));
            Assert.IsNull(host.Transitions);
            Assert.AreEqual(0, host.Opacity);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsFalse(host.Classes.Contains("settling"));
            Assert.IsNotNull(host.Transitions);
            SaveOptionalFrame(window, "dock-motion-first-active.png");
            workspace.ActivateDocument(second);
            workspace.ActivateDocument(first);
            Assert.AreSame(first.View, host.Target);
            Assert.AreSame(host.Target, host.Child);
            Assert.IsTrue(host.Classes.Contains("settling"));
            Assert.IsNull(host.Transitions);
            Assert.AreNotSame(secondView, first.View);
            Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(host.Classes.Contains("settling"));
            Assert.IsNotNull(host.Transitions);
            Assert.HasCount(2, host.Transitions);

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void SaveOptionalFrame(TopLevel topLevel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame, "Skia headless renderer should produce a visual-QA frame.");
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }

    private static void AssertDockContentMotion(ReparentingHost host)
    {
        Assert.IsNotNull(host.Transitions);
        Assert.HasCount(2, host.Transitions);
        Assert.Contains(transition =>
            transition is DoubleTransition { Property: var property, Duration: var duration }
            && property == Visual.OpacityProperty
            && duration == TimeSpan.FromMilliseconds(120), host.Transitions);
        Assert.Contains(transition =>
            transition is TransformOperationsTransition { Property: var property, Duration: var duration }
            && property == Visual.RenderTransformProperty
            && duration == TimeSpan.FromMilliseconds(120), host.Transitions);
    }

    private sealed class TestDocument : DockDocument, IDockViewProvider
    {
        public TestDocument(string title) => Title = title;

        public Control? View { get; private set; }

        public Control CreateView() => View ??= new Border { Background = Brushes.Transparent };
    }
}
