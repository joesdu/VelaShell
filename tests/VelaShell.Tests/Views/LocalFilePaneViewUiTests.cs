using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Models;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("LocalFilePane")]
public sealed class LocalFilePaneViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LocalFilePaneViewUiTests).Assembly);
    }

    [TestMethod]
    public async Task InaccessibleRootSelectionRestoresPreviousSelection()
    {
        await _session.Dispatch(async () =>
        {
            using var first = new TempDirectory();
            var inaccessible = new LocalRootEntry("Unavailable", Path.Combine(first.Path, "missing"), false, Path.Combine(first.Path, "missing"));
            var accessible = new LocalRootEntry("~", first.Path, true, first.Path);
            var roots = new TestRootProvider(accessible, inaccessible);
            var viewModel = new LocalFilePaneViewModel(
                new TransferOptions { LocalDownloadDirectory = first.Path },
                rootProvider: roots);
            await viewModel.LoadInitialAsync();

            var view = new LocalFilePaneView { DataContext = viewModel };
            var window = new Window { Width = 600, Height = 400, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                ComboBox combo = view.GetVisualDescendants().OfType<ComboBox>().Single();
                Assert.AreSame(accessible, combo.SelectedItem);

                combo.SelectedItem = inaccessible;
                Dispatcher.UIThread.RunJobs();

                Assert.AreSame(accessible, combo.SelectedItem);
                Assert.AreSame(accessible, viewModel.SelectedRoot);
                Assert.AreEqual(first.Path, viewModel.CurrentPath);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private sealed class TestRootProvider(params LocalRootEntry[] roots) : ILocalRootProvider
    {
        public Task<IReadOnlyList<LocalRootEntry>> EnumerateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalRootEntry>>(roots);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"velashell-root-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
