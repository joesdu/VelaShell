using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Behaviors;
using VelaShell.Core.Models;
using VelaShell.Security;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("ConnectionProfileUi")]
public sealed class ConnectionProfileViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ConnectionProfileViewUiTests).Assembly);

    [TestMethod]
    public void ProtocolTabs_ExposeFocusableSftpAndKeepLegacyProtocolsDisabled()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel
            {
                Host = "files.example.com",
                Username = "root",
                Password = SecureStringConvert.FromPlaintext("secret"),
            };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var protocolButtons = window.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("proto-tab"))
                .ToList();
            Assert.HasCount(2, protocolButtons);
            Assert.IsTrue(protocolButtons.All(button => button.IsTabStop));
            AssertProtocolTabMotion(protocolButtons);

            var legacyProtocols = window.GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("proto-tab"))
                .ToList();
            Assert.HasCount(2, legacyProtocols);
            Assert.IsTrue(legacyProtocols.All(border => !border.IsEnabled));

            TextBox passwordBox = window.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(SecurePasswordBox.GetEnabled);
            Assert.IsTrue(EnglishInputLocale.GetEnabled(passwordBox));
            Assert.IsFalse(InputMethod.GetIsInputMethodEnabled(passwordBox));

            vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(protocolButtons.Single(button => button.Classes.Contains("selected")).IsEffectivelyVisible);
            Assert.IsTrue(vm.IsSftpSelected);
            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void AssertProtocolTabMotion(IReadOnlyList<Button> protocolButtons)
    {
        foreach (Button button in protocolButtons)
        {
            Assert.IsNotNull(button.Transitions);
            Assert.HasCount(3, button.Transitions);
            Assert.IsTrue(button.Transitions!.Any(transition =>
                transition is BrushTransition { Property: var property, Duration: var duration }
                && property == Button.BackgroundProperty
                && duration == TimeSpan.FromMilliseconds(120)));
            Assert.IsTrue(button.Transitions.Any(transition =>
                transition is BrushTransition { Property: var property, Duration: var duration }
                && property == Border.BorderBrushProperty
                && duration == TimeSpan.FromMilliseconds(120)));
            Assert.IsTrue(button.Transitions.Any(transition =>
                transition is BrushTransition { Property: var property, Duration: var duration }
                && property == Button.ForegroundProperty
                && duration == TimeSpan.FromMilliseconds(120)));
        }
    }
}
