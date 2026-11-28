using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("SyncInput")]
public class ChannelManagerWindowUiTests
{
    [TestMethod]
    [DataRow(740)]
    [DataRow(470)]
    [DataRow(472)]
    public Task GroupedMatrix_AlignsWithHeadersAndMovesOnlyClickedTerminal(int width) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(GetType().Assembly).RunOnUiAsync(() =>
        {
            var host = new MainWindowViewModel();
            SessionTreeViewModel tree = host.Sidebar.SessionTree = new(Substitute.For<ISessionRepository>());
            var group = new SessionTreeNodeViewModel(Guid.NewGuid(), "Production", true) { IsExpanded = true };
            tree.Nodes.Add(group);
            var child = new SessionTreeNodeViewModel(Guid.NewGuid(), "East", true) { IsExpanded = true };
            group.Children.Add(child);
            foreach (string title in new[] { "api-01", "api-02", "database-primary-" + new string('x', 90), "metrics", "worker-01", "worker-02" })
            {
                var profile = new SessionProfile { Name = title, Host = "example.test" };
                child.Children.Add(new(profile.Id, title, false) { TabIconId = title.StartsWith("database-", StringComparison.Ordinal) ? "database" : null });
                var tab = new TerminalTabViewModel(FakeTerminal.Emulator()) { Title = title, Profile = profile, ConnectionStatus = SessionStatus.Connected };
                host.Layout.AddDocument(new TerminalDocument(tab));
                tab.JoinSyncChannel(SyncInputChannel.A);
            }
            using var manager = new ChannelManagerViewModel(host);
            var window = new ChannelManagerWindow { Width = width, DataContext = manager };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var visuals = window.GetVisualDescendants().ToList();
                Rect Box(Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
                void Click(Control control)
                {
                    Point point = Box(control).Center;
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                    Dispatcher.UIThread.RunJobs();
                }
                Border folder = visuals.OfType<Border>().First(b => b.Classes.Contains("group"));
                CheckBox check = folder.GetVisualDescendants().OfType<CheckBox>().Single();
                Button arrow = folder.GetVisualDescendants().OfType<Button>().Single(b => b.IsVisible && b is not CheckBox and not RadioButton);
                Assert.IsLessThanOrEqualTo(Box(arrow).Left, Box(check).Right);
                Border row = visuals.OfType<Border>().Single(b => b.Classes.Contains("matrix-row") && b.DataContext is ChannelManagerRow { Label: var label } && label.StartsWith("database-primary-", StringComparison.Ordinal));
                var radios = row.GetVisualDescendants().OfType<RadioButton>().ToList();
                Assert.HasCount(6, radios);
                TextBlock name = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("matrix-label"));
                var labels = visuals.OfType<TextBlock>().Where(t => t.Classes.Contains("matrix-label")).ToList();
                TextBlock parentLabel = labels.Single(t => t.DataContext is ChannelManagerRow { IsGroup: true, Name: "East" });
                foreach (TextBlock? terminalLabel in labels.Where(t => t.DataContext is ChannelManagerRow { IsGroup: false }))
                {
                    Assert.AreEqual(Box(parentLabel).Left, Box(terminalLabel).Left, 0.5);
                }
                Border checkboxGlyph = row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "NormalRectangle");
                Assert.AreEqual(Box(name).Center.Y, Box(checkboxGlyph).Center.Y, 1);
                var icons = visuals.OfType<VelaShell.Controls.Controls.LucideIcon>().Where(i => i.Classes.Contains("session-icon") && i.IsVisible).ToList();
                Assert.HasCount(1, icons);
                Assert.IsNotNull(icons[0].Data);
                Assert.IsLessThanOrEqualTo(Box(name).Left, Box(icons[0]).Right);
                Assert.IsLessThanOrEqualTo(Box(radios[0]).Left, Box(name).Right);
                foreach (RadioButton? radio in radios)
                {
                    TextBlock header = visuals.OfType<TextBlock>().Single(t => t.Classes.Contains("column-header") && t.Text == (string?)radio.Tag);
                    Assert.AreEqual(Box(header).Center.X, Box(radio).Center.X, 1);
                }
                Click(radios[4]);
                Assert.AreEqual("E", ((ChannelManagerRow)row.DataContext!).Channel);
                window.UpdateLayout();
                Ellipse dot = radios[4].GetVisualDescendants().OfType<Ellipse>().Single(e => e.Name == "Dot");
                Ellipse ring = radios[4].GetVisualDescendants().OfType<Ellipse>().Single(e => e.Name == "Ring");
                Assert.IsTrue(dot.IsVisible);
                Assert.AreEqual(Box(ring).Center, Box(dot).Center);
                Assert.IsFalse(radios[0].GetVisualDescendants().OfType<Ellipse>().Single(e => e.Name == "Dot").IsVisible);
                Assert.AreEqual("E 1", visuals.OfType<Button>().Single(b => b.Classes.Contains("filter-chip") && (string?)b.Tag == "6").Content);
                Click(check);
                ComboBox target = visuals.OfType<ComboBox>().Single();
                Assert.IsTrue(target.IsEnabled);
                Assert.AreSequenceEqual("ABCDEF".Select(c => VelaShell.Localization.LocalizedStrings.Instance[$"ChannelManager_Channel{c}"]).ToArray(), target.Items.OfType<ComboBoxItem>().Skip(1).Select(item => item.Content).ToArray());
                Border titleBar = visuals.OfType<Border>().Single(b => b.Classes.Contains("window-titlebar"));
                Assert.AreEqual(window.Title, titleBar.GetVisualDescendants().OfType<TextBlock>().Single().Text);
                using WriteableBitmap? frame = window.CaptureRenderedFrame();
                Assert.IsNotNull(frame);
                Click(visuals.OfType<Button>().Single(b => b.Classes.Contains("filter-chip") && (string?)b.Tag == "6"));
                Assert.ContainsSingle(r => !r.IsGroup, manager.Rows);
                target.SelectedIndex = 6;
                Assert.IsTrue(manager.IsEmpty);
                Assert.ContainsSingle(t => t.SyncChannel == SyncInputChannel.F, host.TerminalTabs);
                Assert.AreEqual(5, host.TerminalTabs.Count(t => t.SyncChannel == SyncInputChannel.A));
            }
            finally { window.Close(); }
        });
}
