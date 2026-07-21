using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Docking.Controls;
using VelaShell.Localization;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.Security;
using VelaShell.Terminal;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("Todo2VisualQA")]
public sealed class Todo2VisualCaptureUiTests
{
    private static readonly object ManifestGate = new();
    private static readonly List<CaptureManifestEntry> Manifest = [];
    private static HeadlessUnitTestSession _session = null!;
    private static LocalizationService _localization = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Todo2VisualCaptureUiTests).Assembly);
        _localization = new();
        LocalizedStrings.Instance.Attach(_localization);
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        string? directory = CaptureDirectory;
        if (directory is null)
        {
            return;
        }

        lock (ManifestGate)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "todo-2-visual-state-manifest.json"),
                JsonSerializer.Serialize(Manifest, new JsonSerializerOptions { WriteIndented = true })
            );
        }
    }

    [TestMethod]
    public void ConnectionProfile_RendersRequiredProtocolStates()
    {
        OnUi(() =>
        {
            RenderProfile(ThemeVariant.Dark, "en-US", ConnectionType.SSH, "connection-profile-ssh-dark.png");
            RenderProfile(ThemeVariant.Dark, "en-US", ConnectionType.SFTP, "connection-profile-sftp-dark.png");
            RenderProfile(ThemeVariant.Light, "en-US", ConnectionType.SFTP, "connection-profile-sftp-light.png");
            RenderProfile(ThemeVariant.Dark, "zh-CN", ConnectionType.SFTP, "connection-profile-sftp-zh-Hans-dark.png");
        });
    }

    [TestMethod]
    public void ConnectionProfile_SftpSelectorKeyboardFocus_IsCaptured()
    {
        OnUi(() =>
        {
            RenderProfile(
                ThemeVariant.Dark,
                "en-US",
                ConnectionType.SFTP,
                "connection-profile-sftp-keyboard-focused-dark.png",
                focusSftp: true
            );
        });
    }

    [TestMethod]
    public void SessionTree_RendersSshAndSftpCapabilityStates()
    {
        OnUi(() =>
        {
            RenderSessionTree(ConnectionType.SSH, "session-tree-ssh-context-state.png");
            RenderSessionTree(ConnectionType.SFTP, "session-tree-sftp-context-state.png");
        });
    }

    [TestMethod]
    public async Task OpenSftp_RendersStandaloneDualPane()
    {
        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnUiAsync(async () =>
        {
            ThemeVariant previousTheme = Application.Current!.RequestedThemeVariant;
            Exception? error = null;
            try
            {
                await RenderStandaloneDualPaneAsync(ThemeVariant.Dark, "sftp-dual-pane-ssh-dark.png");
                await RenderStandaloneDualPaneAsync(ThemeVariant.Light, "sftp-dual-pane-ssh-light.png");
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                Application.Current.RequestedThemeVariant = previousTheme;
                completed.TrySetResult(error);
            }
        });
        Exception? error = await completed.Task;
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task DownloadSelectedAsync_RefreshNotificationsStayOnUiSynchronizationContext()
    {
        await OnUiAsync(async () =>
        {
            (MainWindowViewModel vm, SessionProfile profile, _, ISftpService sftpService) = CreateConnectedSshViewModel();
            await vm.OpenSftpForProfileAsync(profile);
            SftpDocument document = vm.Layout.AllDocuments().OfType<SftpDocument>().Single();
            await document.ViewModel.InitialLoadTask;
            RemoteFileInfoViewModel remote = document.ViewModel.RemoteFiles.Files.Single(file => !file.IsParentEntry);
            document.ViewModel.RemoteFiles.SelectedFiles.Add(remote);

            int uiThread = Environment.CurrentManagedThreadId;
            List<int> notificationThreads = [];
            PropertyChangedEventHandler propertyChanged = (_, _) => notificationThreads.Add(Environment.CurrentManagedThreadId);
            NotifyCollectionChangedEventHandler collectionChanged = (_, _) => notificationThreads.Add(Environment.CurrentManagedThreadId);
            document.ViewModel.LocalFiles.PropertyChanged += propertyChanged;
            document.ViewModel.LocalFiles.Entries.CollectionChanged += collectionChanged;

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sftpService.DownloadFileAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    started.SetResult();
                    return completed.Task;
                });

            try
            {
                Task download = document.ViewModel.DownloadSelectedAsync();
                await started.Task;
                await Task.Run(completed.SetResult);
                await download;
                Assert.IsNotEmpty(notificationThreads);
                Assert.IsTrue(notificationThreads.All(thread => thread == uiThread));
            }
            finally
            {
                document.ViewModel.LocalFiles.PropertyChanged -= propertyChanged;
                document.ViewModel.LocalFiles.Entries.CollectionChanged -= collectionChanged;
                await document.ViewModel.CloseAsync();
            }
        });
    }

    private static async Task RenderStandaloneDualPaneAsync(ThemeVariant theme, string fileName)
    {
        Application.Current!.RequestedThemeVariant = theme;
        (MainWindowViewModel vm, SessionProfile profile, ISshClientWrapper sshClient, _) = CreateConnectedSshViewModel();
        TerminalTabViewModel? compatibilityResult = await vm.OpenSftpForProfileAsync(profile);
        Assert.IsNull(compatibilityResult);
        Assert.IsEmpty(vm.TabBar.Tabs);
        Assert.IsNotNull(vm.Layout.AllDocuments().OfType<SftpDocument>().SingleOrDefault());
        SftpDocument document = vm.Layout.AllDocuments().OfType<SftpDocument>().Single();
        Assert.IsEmpty(vm.TabBar.Tabs);
        sshClient.DidNotReceive().CreateShellStreamAsync(
            Arg.Any<string>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<int>(),
            Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>());

        int uiThread = Environment.CurrentManagedThreadId;
        List<int> notificationThreads = [];
        PropertyChangedEventHandler propertyChanged = (_, _) => notificationThreads.Add(Environment.CurrentManagedThreadId);
        NotifyCollectionChangedEventHandler collectionChanged = (_, _) => notificationThreads.Add(Environment.CurrentManagedThreadId);
        document.ViewModel.LocalFiles.PropertyChanged += propertyChanged;
        document.ViewModel.LocalFiles.Entries.CollectionChanged += collectionChanged;
        document.ViewModel.RemoteFiles.PropertyChanged += propertyChanged;
        document.ViewModel.RemoteFiles.Files.CollectionChanged += collectionChanged;
        await document.ViewModel.InitialLoadTask;
        Assert.IsNotEmpty(notificationThreads);
        Assert.IsTrue(notificationThreads.All(thread => thread == uiThread));
        Assert.IsNull(document.ViewModel.RemoteFiles.ErrorMessage);
        Assert.Contains(file => file.Name == "readme.txt", document.ViewModel.RemoteFiles.Files);

        TerminalTabViewModel terminalViewModel = new(Substitute.For<ITerminalEmulator>());
        vm.Layout.AddDocument(new TerminalDocument(terminalViewModel));
        vm.Layout.ActivateDocument(document);
        var dock = new DockWorkspaceControl { Workspace = vm.Layout };
        var window = new Window { Width = 1280, Height = 620, Content = dock };
        BindingDiagnosticSink diagnostics = new();
        ILogSink previousSink = Logger.Sink;
        Logger.Sink = diagnostics;
        try
        {
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsNotNull(dock.GetVisualDescendants().OfType<DockTabItem>().SingleOrDefault());
            Assert.IsNotNull(dock.GetVisualDescendants().OfType<SftpDockTabItem>().SingleOrDefault());
            SftpDocumentView view = dock.GetVisualDescendants().OfType<SftpDocumentView>().Single();
            LocalFilePaneView localPane = view.GetVisualDescendants().OfType<LocalFilePaneView>().Single();
            FileBrowserView remotePane = view.GetVisualDescendants().OfType<FileBrowserView>().Single();
            Button uploadPill = remotePane.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Classes.Contains("upload-pill"));
            Button localRefresh = view.FindControl<Button>("LocalRefreshButton")
                ?? throw new AssertFailedException("Local refresh button is missing.");
            Button remoteRefresh = view.FindControl<Button>("RemoteRefreshButton")
                ?? throw new AssertFailedException("Remote refresh button is missing.");
            Assert.AreNotEqual(ToolTip.GetTip(localRefresh), ToolTip.GetTip(remoteRefresh));
            Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(localRefresh)));
            Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(remoteRefresh)));
            PropertyInfo focusAdorner = uploadPill.GetType().GetProperty("FocusAdorner")
                ?? throw new AssertFailedException("Upload pill does not expose FocusAdorner.");
            Assert.IsNull(focusAdorner.GetValue(uploadPill));
            Assert.IsTrue(uploadPill.Focus());
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Control templateRoot = uploadPill.GetVisualDescendants().OfType<Control>()
                .SingleOrDefault(control => control.Name == "PART_UploadPillRoot")
                ?? throw new AssertFailedException("Upload pill PART_UploadPillRoot is missing.");
            Assert.IsNotNull(templateRoot);
            Assert.IsFalse(uploadPill.GetVisualDescendants().OfType<Control>()
                .Any(control => control.Name == "PART_FocusAdorner"));
            Control contentPresenter = uploadPill.GetVisualDescendants().OfType<Control>()
                .SingleOrDefault(control => control.Name == "PART_ContentPresenter")
                ?? throw new AssertFailedException("Upload pill PART_ContentPresenter is missing.");
            string buttonBackground = ReadProperty(uploadPill, "Background") ?? string.Empty;
            string buttonBorder = ReadProperty(uploadPill, "BorderBrush") ?? string.Empty;
            string presenterBackground = ReadProperty(contentPresenter, "Background") ?? string.Empty;
            string presenterBorder = ReadProperty(contentPresenter, "BorderBrush") ?? string.Empty;
            string rootBackground = ReadProperty(templateRoot, "Background") ?? string.Empty;
            string rootBorder = ReadProperty(templateRoot, "BorderBrush") ?? string.Empty;
            string runtimeProperties =
                $"Button Background={buttonBackground}, Border={buttonBorder}, "
                + $"PART_ContentPresenter Background={presenterBackground}, Border={presenterBorder}, "
                + $"PART_UploadPillRoot Background={rootBackground}, Border={rootBorder}";
            Assert.IsFalse(runtimeProperties.Contains("lime", StringComparison.OrdinalIgnoreCase), runtimeProperties);
            Assert.IsFalse(runtimeProperties.Contains("00FF00", StringComparison.OrdinalIgnoreCase), runtimeProperties);
            using (WriteableBitmap focusedFrame = window.CaptureRenderedFrame()
                   ?? throw new AssertFailedException("Headless renderer did not produce a focused docked frame."))
            {
                Rect uploadBounds = new(uploadPill.TranslatePoint(new Point(), window)!.Value, uploadPill.Bounds.Size);
                AssertUploadPillPixels(focusedFrame, window, uploadBounds);
            }
            Grid paneGrid = view.GetVisualDescendants().OfType<Grid>()
                .Single(grid => grid.ColumnDefinitions.Count == 3 && grid.ColumnDefinitions[1].Width.Value == 4);
            Assert.AreEqual(280, paneGrid.ColumnDefinitions[0].MinWidth);
            Assert.AreEqual(280, paneGrid.ColumnDefinitions[2].MinWidth);
            Assert.IsTrue(localPane.Bounds.X < remotePane.Bounds.X);
            Assert.IsTrue(localPane.Bounds.Left >= 0);
            Assert.IsTrue(remotePane.Bounds.Right <= view.Bounds.Width + 1);
            Assert.IsFalse(localPane.Bounds.Bottom > view.Bounds.Height + 1);
            Assert.IsFalse(remotePane.Bounds.Bottom > view.Bounds.Height + 1);

            foreach (Border row in localPane.GetVisualDescendants().OfType<Border>()
                         .Where(border => border.Classes.Contains("file-row")))
            {
                Grid nameCell = row.GetVisualDescendants().OfType<Grid>()
                    .Single(grid => grid.Classes.Contains("local-name-cell"));
                TextBlock sizeCell = row.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Classes.Contains("local-size-cell"));
                Point nameOrigin = nameCell.TranslatePoint(new Point(), view)!.Value;
                Point sizeOrigin = sizeCell.TranslatePoint(new Point(), view)!.Value;
                Assert.IsTrue(
                    nameOrigin.X + nameCell.Bounds.Width <= sizeOrigin.X + 1,
                    $"Local name cell overlaps size column: name={nameCell.Bounds}, size={sizeCell.Bounds}."
                );
            }
            Assert.IsEmpty(diagnostics.Errors, string.Join(Environment.NewLine, diagnostics.Errors));

            string? frame = SaveFrame(window, fileName);
            AddManifest(new(
                Path.GetFileNameWithoutExtension(fileName),
                "Standalone SFTP dual-pane document with local pane left and remote pane right",
                theme.ToString(),
                "en-US",
                "ssh",
                false,
                frame,
                "Fresh SftpDocumentView capture; evidence is written only when VELASHELL_VISUAL_QA_DIR is configured."
            ));
        }
        finally
        {
            document.ViewModel.LocalFiles.PropertyChanged -= propertyChanged;
            document.ViewModel.LocalFiles.Entries.CollectionChanged -= collectionChanged;
            document.ViewModel.RemoteFiles.PropertyChanged -= propertyChanged;
            document.ViewModel.RemoteFiles.Files.CollectionChanged -= collectionChanged;
            Logger.Sink = previousSink;
            window.Close();
            await document.ViewModel.CloseAsync();
        }
    }

    private static void RenderProfile(
        ThemeVariant theme,
        string cultureName,
        ConnectionType selected,
        string fileName,
        bool focusSftp = false)
    {
        ThemeVariant previousTheme = Application.Current!.RequestedThemeVariant;
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            Application.Current.RequestedThemeVariant = theme;
            _localization.SetLanguage(cultureName);

            var vm = new ConnectionProfileViewModel
            {
                Host = "files.example.com",
                Port = 22,
                Username = "root",
                Password = SecureStringConvert.FromPlaintext("secret"),
            };
            vm.SelectConnectionTypeCommand.Execute(selected).Subscribe();
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var protocolButtons = window.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("proto-tab"))
                .ToList();
            Assert.HasCount(2, protocolButtons);
            Assert.IsTrue(protocolButtons.All(button => button.IsTabStop));
            var disabledProtocols = window.GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("proto-tab"))
                .ToList();
            Assert.HasCount(2, disabledProtocols);
            Assert.IsTrue(disabledProtocols.All(border => !border.IsEnabled));

            Button sftp = protocolButtons.Single(button =>
                button.Content is TextBlock { Text: "SFTP" });
            if (focusSftp)
            {
                sftp.Focus();
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(sftp.IsFocused);
                Assert.IsInstanceOfType<SolidColorBrush>(sftp.Background);
                Assert.AreEqual(Color.Parse("#30BD93F9"), ((SolidColorBrush)sftp.Background).Color);
                Border[] limeFocusLayers = sftp.GetVisualDescendants()
                    .OfType<Border>()
                    .Where(border => border.Background is SolidColorBrush brush
                        && brush.Color.G > 180
                        && brush.Color.R > 100
                        && brush.Color.B < 100)
                    .ToArray();
                Assert.IsEmpty(limeFocusLayers, "Fluent system focus overlay replaced the Vela focus token.");
                WriteFocusRuntimeDiagnostics(sftp, fileName);
            }

            string? frame = SaveFrame(window, fileName);
            AddManifest(new(
                Path.GetFileNameWithoutExtension(fileName),
                "ConnectionProfileView protocol selector",
                theme.ToString(),
                cultureName,
                selected.ToString(),
                sftp.IsFocused,
                frame,
                "Telnet and Serial are disabled protocol borders."
            ));
            window.Close();
        }
        finally
        {
            Application.Current.RequestedThemeVariant = previousTheme;
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
            _localization.SetLanguage(previousUiCulture.Name);
        }
    }

    private static void AssertUploadPillPixels(
        WriteableBitmap bitmap,
        TopLevel topLevel,
        Rect bounds)
    {
        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        const int stride = 4;
        int bufferSize = checked(width * height * stride);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, bufferSize, width * stride);
            int greenDominantPixels = 0;
            int purpleFamilyPixels = 0;
            int exact77C331 = 0;
            int exactB8E899 = 0;
            double scaleX = bitmap.PixelSize.Width / topLevel.Bounds.Width;
            double scaleY = bitmap.PixelSize.Height / topLevel.Bounds.Height;
            int left = Math.Max(0, (int)Math.Floor(bounds.Left * scaleX) + 2);
            int top = Math.Max(0, (int)Math.Floor(bounds.Top * scaleY) + 2);
            int right = Math.Min(width, (int)Math.Ceiling(bounds.Right * scaleX) - 2);
            int bottom = Math.Min(height, (int)Math.Ceiling(bounds.Bottom * scaleY) - 2);
            int samplePixels = Math.Max(0, right - left) * Math.Max(0, bottom - top);
            for (int y = top; y < bottom; y++)
            {
                for (int x = left; x < right; x++)
                {
                    int offset = (y * width + x) * stride;
                    byte red = Marshal.ReadByte(buffer, offset);
                    byte green = Marshal.ReadByte(buffer, offset + 1);
                    byte blue = Marshal.ReadByte(buffer, offset + 2);
                    if (green >= 120 && green >= red + 32 && green >= blue + 32)
                    {
                        greenDominantPixels++;
                    }
                    if (red == 0x77 && green == 0xC3 && blue == 0x31)
                    {
                        exact77C331++;
                    }
                    if (red == 0xB8 && green == 0xE8 && blue == 0x99)
                    {
                        exactB8E899++;
                    }
                    if (blue >= red && blue > green)
                    {
                        purpleFamilyPixels++;
                    }
                }
            }
            double greenShare = samplePixels == 0 ? 1 : (double)greenDominantPixels / samplePixels;
            Assert.IsLessThan(0.02, greenShare,
                $"Focused upload pill contains green-dominant pixels: {greenDominantPixels}/{samplePixels}; "
                + $"bounds={bounds}, topLevel={topLevel.Bounds}, bitmap={bitmap.PixelSize}.");
            Assert.AreEqual(0, exact77C331, "Focused upload pill contains #77C331.");
            Assert.AreEqual(0, exactB8E899, "Focused upload pill contains #B8E899.");
            Assert.IsGreaterThan(0, purpleFamilyPixels, "Focused upload pill lacks purple-family pixels.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void RenderSessionTree(ConnectionType connectionType, string fileName)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        _localization.SetLanguage("en-US");
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        var profile = new SessionProfile
        {
            ConnectionType = connectionType,
            Name = connectionType == ConnectionType.SFTP ? "SftpFiles" : "SshServer",
            Host = "files.example.com",
            Username = "root",
        };
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { profile }));
        var vm = new SessionTreeViewModel(repository);
        vm.LoadCommand.Execute().FirstAsync().GetAwaiter().GetResult();
        vm.SelectedNode = vm.Nodes.Single();

        var view = new SessionTreeView { DataContext = vm, Width = 280, Height = 180 };
        var host = new Border
        {
            Width = 300,
            Height = 200,
            Background = (IBrush)Application.Current!.Resources["VelaBgSidebar"]!,
            Child = view,
        };
        var window = new Window { Width = 300, Height = 200, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.IsTrue(vm.SelectSession(profile.Id));
        Dispatcher.UIThread.RunJobs();

        Border sessionRow = view.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => border.Classes.Contains("session"));
        Assert.IsNotNull(sessionRow.ContextMenu);
        Dispatcher.UIThread.RunJobs();
        bool openSftpVisible = vm.Nodes.Single().CanOpenSftp;
        bool openSftpRaised = false;
        vm.OpenSftpRequested += _ => openSftpRaised = true;
        vm.OpenSftpCommand.Execute().Subscribe();
        Assert.IsTrue(openSftpVisible);
        Assert.IsTrue(openSftpRaised);
        Assert.IsTrue(view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == profile.Name));

        ContextMenu menu = sessionRow.ContextMenu!;
        MenuItem openSftp = menu.Items.OfType<MenuItem>().Single(item =>
            item.Header?.ToString() == VelaShell.Core.Resources.Strings.Get("Tree_OpenSftp"));
        MenuItem portForward = menu.Items.OfType<MenuItem>().Single(item =>
            item.Header?.ToString() == VelaShell.Core.Resources.Strings.Get("Tree_PortForwarding"));

        string? ownerFrame = SaveFrame(window, fileName.Replace(".png", "-owner.png", StringComparison.Ordinal));
        menu.Open(sessionRow);
        Dispatcher.UIThread.RunJobs();
        TopLevel popupRoot = TopLevel.GetTopLevel(openSftp)!;
        popupRoot.UpdateLayout();
        Assert.IsTrue(menu.IsOpen);
        Assert.IsTrue(openSftp.IsVisible);
        Assert.AreEqual(connectionType == ConnectionType.SSH, portForward.IsVisible);
        MenuItem[] visiblePopupItems = menu.GetVisualDescendants()
            .OfType<MenuItem>()
            .Where(item => item.IsVisible)
            .ToArray();
        Assert.IsTrue(visiblePopupItems.Any(item =>
            item.Header?.ToString() == VelaShell.Core.Resources.Strings.Get("Connect")));
        if (connectionType == ConnectionType.SSH)
        {
            Assert.Contains(openSftp, visiblePopupItems);
            Assert.Contains(portForward, visiblePopupItems);
        }
        else
        {
            Assert.Contains(openSftp, visiblePopupItems);
            Assert.DoesNotContain(portForward, visiblePopupItems);
        }

        string? frame = SaveFrame(popupRoot, fileName);
        AddManifest(new(
            Path.GetFileNameWithoutExtension(fileName),
            "SessionTreeView selected session with standalone Open SFTP and SSH-only port forwarding",
            "dark",
            CultureInfo.CurrentUICulture.Name,
            connectionType.ToString(),
            false,
            frame,
            $"Real production ContextMenu opened from the selected row; CanOpenSftp={openSftpVisible}, OpenSftpRequested raised={openSftpRaised}. Popup capture is the actual PopupRoot; owner frame is recorded separately.",
            ownerFrame
        ));
        menu.Close();
        window.Close();
    }

    private static (MainWindowViewModel ViewModel, SessionProfile Profile, ISshClientWrapper SshClient, ISftpService SftpService) CreateConnectedSshViewModel()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper shellStream = Substitute.For<IShellStreamWrapper>();
        ISftpService sftpService = Substitute.For<ISftpService>();
        ITerminalEmulator terminal = Substitute.For<ITerminalEmulator>();
        shellStream.CanRead.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<int>().Task);
        var profile = new SessionProfile
        {
            Name = "Files",
            Host = "files.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
        };
        var session = new SshSession
        {
            SessionId = Guid.NewGuid(),
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
            },
            Status = SessionStatus.Connected,
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096, Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()).Returns(shellStream);
        sftpService.GetWorkingDirectoryAsync(session.SessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("/home/testuser"));
        sftpService.ListDirectoryAsync(session.SessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>
            {
                new()
                {
                    Name = "readme.txt",
                    FullPath = "/home/testuser/readme.txt",
                    Size = 42,
                    Permissions = "-rw-r--r--",
                    IsDirectory = false,
                    LastModified = new DateTime(2026, 7, 19),
                    Owner = "testuser",
                    Group = "testuser",
                },
            }));
        return (
            new MainWindowViewModel(workflow, sshConnectionService, () => terminal, sftpService: sftpService),
            profile,
            sshClient,
            sftpService
        );
    }

    private static string? SaveFrame(TopLevel topLevel, string fileName)
    {
        string? directory = CaptureDirectory;
        if (directory is null)
        {
            return null;
        }

        Directory.CreateDirectory(directory);
        using WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame, "Skia headless renderer should produce a visual-QA frame.");
        string path = Path.Combine(directory, fileName);
        using FileStream output = File.Create(path);
        frame.Save(output, PngBitmapEncoderOptions.Default);
        return path;
    }

    private static void AddManifest(CaptureManifestEntry entry)
    {
        lock (ManifestGate)
        {
            Manifest.Add(entry);
        }
    }

    private static void WriteFocusRuntimeDiagnostics(Button button, string captureFileName)
    {
        string? directory = CaptureDirectory;
        if (directory is null)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var children = button.GetVisualDescendants()
            .Select(visual => new
            {
                Type = visual.GetType().FullName,
                Name = (visual as Control)?.Name,
                Bounds = visual.Bounds.ToString(),
                Background = ReadProperty(visual, "Background"),
                Fill = ReadProperty(visual, "Fill"),
                Stroke = ReadProperty(visual, "Stroke"),
                Foreground = ReadProperty(visual, "Foreground"),
                BorderBrush = ReadProperty(visual, "BorderBrush"),
                BorderThickness = ReadProperty(visual, "BorderThickness"),
            })
            .ToList();
        var topLevel = TopLevel.GetTopLevel(button);
        var adornerLayer = AdornerLayer.GetAdornerLayer(button);
        Control? adorner = AdornerLayer.GetAdorner(button);
        var adornerVisuals = topLevel?.GetVisualDescendants()
            .Where(visual => visual.GetType().FullName?.Contains("Adorner", StringComparison.OrdinalIgnoreCase) == true
                || (visual as Control)?.Name?.Contains("Focus", StringComparison.OrdinalIgnoreCase) == true)
            .Select(visual => new
            {
                Type = visual.GetType().FullName,
                Name = (visual as Control)?.Name,
                Bounds = visual.Bounds.ToString(),
                Background = ReadProperty(visual, "Background"),
                Fill = ReadProperty(visual, "Fill"),
                Stroke = ReadProperty(visual, "Stroke"),
                Foreground = ReadProperty(visual, "Foreground"),
            })
            .ToList();
        PropertyInfo? focusAdornerProperty = button.GetType().GetProperty("FocusAdorner");
        object? focusAdorner = focusAdornerProperty?.GetValue(button);
        var diagnostic = new
        {
            Capture = captureFileName,
            ButtonType = button.GetType().FullName,
            ButtonBounds = button.Bounds.ToString(),
            ButtonInTopLevelBounds = topLevel is null
                ? null
                : button.TranslatePoint(new Point(0, 0), topLevel)?.ToString(),
            ButtonBackground = ReadProperty(button, "Background"),
            ButtonBorderBrush = ReadProperty(button, "BorderBrush"),
            FocusAdornerProperty = focusAdornerProperty?.PropertyType.FullName,
            FocusAdorner = focusAdorner?.GetType().FullName,
            Children = children,
            AdornerVisuals = adornerVisuals,
            AdornerLayer = adornerLayer?.GetType().FullName,
            AdornerLayerDefaultFocusAdorner = adornerLayer?.DefaultFocusAdorner?.GetType().FullName,
            Adorner = adorner?.GetType().FullName,
        };
        File.WriteAllText(
            Path.Combine(directory, "focused-sftp-runtime-tree.json"),
            JsonSerializer.Serialize(diagnostic, new JsonSerializerOptions { WriteIndented = true })
        );
    }

    private static string? ReadProperty(object source, string propertyName)
    {
        object? value = source.GetType().GetProperty(propertyName)?.GetValue(source);
        return value?.ToString();
    }

    private static string? CaptureDirectory
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }
            if (Path.IsPathRooted(configured))
            {
                return configured;
            }

            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VelaShell.slnx")))
            {
                directory = directory.Parent;
            }
            return directory is null
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(directory.FullName, configured));
        }
    }

    private static void OnUi(Func<Task> action) =>
        _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    private static void OnUi(Action action) =>
        _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    private static Task OnUiAsync(Func<Task> action) =>
        _session.Dispatch(action, CancellationToken.None);

    private sealed class BindingDiagnosticSink : ILogSink
    {
        public List<string> Errors { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) => true;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Capture(level, area, messageTemplate);

        public void Log(
            LogEventLevel level,
            string area,
            object? source,
            string messageTemplate,
            params object?[] propertyValues
        ) => Capture(level, area, messageTemplate);

        private void Capture(LogEventLevel level, string area, string messageTemplate)
        {
            if (level >= LogEventLevel.Error
                || area.Contains("Binding", StringComparison.OrdinalIgnoreCase)
                || messageTemplate.Contains("binding", StringComparison.OrdinalIgnoreCase))
            {
                Errors.Add($"{level} {area}: {messageTemplate}");
            }
        }
    }

    private sealed record CaptureManifestEntry(
        string State,
        string Surface,
        string Theme,
        string Culture,
        string SelectedProtocol,
        bool KeyboardFocused,
        string? Png,
        string Limitation,
        string? OwnerPng = null);
}
