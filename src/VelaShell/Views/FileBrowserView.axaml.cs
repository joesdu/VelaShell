using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI.Primitives;
using VelaShell.Controls.Controls;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>远程文件浏览器视图:文件列表、可拖拽列、拖放上传,以及操作系统选择器/对话框集成。</summary>
public partial class FileBrowserView : UserControl
{
    private FileBrowserViewModel? _viewModel;

    /// <summary>
    /// 可拖拽列的列键,顺序同表头。末列“修改时间”吃 * 宽度、右侧没有拖拽条,故不在此列。
    /// 每根拖拽条调整的是它左边那一列(Tag 即该列的列键)。
    /// </summary>
    private static readonly string[] ResizableColumns =
    [
        "name",
        "size",
        "permissions",
        "owner",
        "group",
        "type",
    ];

    /// <summary>单根拖拽条的宽度(同 axaml 的拖拽条列定义)。</summary>
    private const double SplitterWidth = 6;

    /// <summary>表头/行的左右内边距合计(axaml 里 Padding="14,0,20,0";右侧的 20 是让开覆盖式滚动条)。</summary>
    private const double HorizontalPadding = 34;

    // 列宽靠度量文字算出来,度量字体必须与实际渲染字体一致 —— 不一致(旧值以 JetBrains Mono
    // 打头、且不含内置 Cascadia)字形步进就对不上,列自适应/截断会错位。渲染用的是
    // VelaUiMonoFont 令牌(跟随设置 → 外观 → 界面字体),故运行时现取(见 ResolveMonoTypeface);
    // 这里只留取不到令牌时的兜底。
    private static readonly FontFamily DefaultMonoFont = new("fonts:VelaShell#Cascadia Mono, JetBrains Mono, Consolas, monospace");
    private static readonly Typeface DefaultMonoTypeface = new(DefaultMonoFont);
    private string? _activeSplitter;
    private double _dragStartX;

    // 跨面板拖拽发起状态(SFTP 双栏模式)。
    private bool _isDragging;
    private Point _dragOrigin;
    private RemoteFileInfoViewModel? _dragRow;
    private PointerPressedEventArgs? _dragPointerArgs;
    private readonly List<RemoteFileInfoViewModel> _selectionAtPress = [];
    private bool _selectionRestoredForPendingDrag;
    private const double DragThreshold = 5;

    /// <summary>按下拖拽条那一刻的列宽快照(列键 → 像素),拖拽期间按位移量增量应用。</summary>
    private readonly Dictionary<string, double> _startWidths = [];

    /// <summary>初始化视图,为视图模型提供操作系统选择器/对话框,并挂接列拖拽条与拖放事件处理。</summary>
    public FileBrowserView()
    {
        InitializeComponent();

        // VM 无法访问 Avalonia 的存储 API;由视图提供操作系统的选择器。
        DataContextChanged += (_, _) =>
        {
            _viewModel?.DirectoryChanged -= OnDirectoryChanged;
            _viewModel?.PathEditActivated -= OnPathEditActivated;
            _viewModel?.RevealRequested -= OnRevealRequested;
            _viewModel = DataContext as FileBrowserViewModel;
            if (_viewModel is not { } vm)
            {
                return;
            }
            vm.DirectoryChanged += OnDirectoryChanged;
            vm.PathEditActivated += OnPathEditActivated;
            vm.RevealRequested += OnRevealRequested;
            vm.ShowRemoteSearch = root => ShowRemoteSearchAsync(vm, root);
            vm.PickUploadSelection = PickLocalPathsAsync;
            vm.PickSavePathForDownload = PickSavePathAsync;
            vm.PickFolderForDownload = PickDownloadFolderAsync;
            vm.PromptForText = PromptForTextAsync;
            vm.CopyToClipboard = CopyToClipboardAsync;
            vm.ShowFileProperties = ShowFilePropertiesAsync;
            vm.ConfirmDelete = ConfirmAsync;
            vm.ConfirmLowSpace = ConfirmLowSpaceAsync;
            vm.OpenLocalFile = OpenLocalFileAsync;
            vm.OpenInBuiltInEditor = OpenInBuiltInEditorAsync;
            vm.PromptConfigureEditor = PromptConfigureEditorAsync;
            vm.ConfirmOverwrite = ConfirmOverwriteAsync;
            vm.ConfirmRemoteOverwrite = ConfirmRemoteOverwriteAsync;
            // 列宽跨重启记忆(全局一份,见 FilePaneLayoutMemory)。
            FilePaneLayoutMemory.RestoreColumns(this, vm);
        };

        // 框选与跨栏拖放使用不相交的按下表面。终端模式没有拖放时,
        // 所有行内容也都留给框选。
        if (this.FindControl<ListBox>("FileList") is { } list
            && this.FindControl<Border>("MarqueeOverlay") is { } overlay)
        {
            MarqueeSelection.Attach(
                list,
                overlay,
                item => item is RemoteFileInfoViewModel { IsParentEntry: false },
                e => DataContext is not FileBrowserViewModel { IsDragEnabled: true }
                    || !MarqueeSelection.IsDndSurface(e.Source, FileList, e.GetPosition(FileList))
            );
        }

        // 拖放接收:操作系统文件拖入(始终启用)+ 跨面板拖入。
        if (this.FindControl<ListBox>("FileList") is { } fileList)
        {
            DragDrop.SetAllowDrop(fileList, true);
            fileList.AddHandler(DragDrop.DragOverEvent, OnFileListDragOver);
            fileList.AddHandler(DragDrop.DropEvent, OnFileListDrop);
            fileList.AddHandler(PointerPressedEvent, OnRemoteDragPointerPressedTunnel, RoutingStrategies.Tunnel, true);
            fileList.AddHandler(PointerPressedEvent, OnRemoteDragPointerPressedBubble, RoutingStrategies.Bubble, true);

            // 跨面板拖拽发起(行 → 本地面板)。
            fileList.AddHandler(PointerMovedEvent, OnRemoteDragPointerMoved);
            fileList.AddHandler(PointerReleasedEvent, OnRemoteDragPointerReleased, RoutingStrategies.Bubble, true);

            // 键入定位:焦点在这一栏里时直接敲文件名开头几个字母就选中它,资源管理器同款。
            // 挂在这个视图自己身上:双栏的另一栏、终端都不受影响;路径输入框里的键入它不碰。
            TypeAheadSelection.Attach(fileList, this, item => item is RemoteFileInfoViewModel { IsParentEntry: false } file ? file.Name : null);

            // 回车进文件夹。ListBoxItem 自己把回车标成已处理,所以连已处理的也收。
            fileList.AddHandler(KeyDownEvent, OnFileListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
            fileList.KeepFocusAcrossReloads();
        }
        AddHandler(PointerMovedEvent, OnColumnSplitterPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnColumnSplitterReleased, RoutingStrategies.Tunnel);

        // Ctrl+L 切到手动输入路径(浏览器/文件管理器的通用手势)。走隧道,免得先被
        // 文件列表的键盘导航吃掉。F5 / F2 / Delete 同一个处理器(见 FilePaneKeys)。
        AddHandler(KeyDownEvent, OnFileBrowserKeyDown, RoutingStrategies.Tunnel);
    }

    // ---- 手动输入路径(#226)-------------------------------------------------

    private void OnFileBrowserKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm)
        {
            return;
        }
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.BeginPathEditCommand.Execute().Subscribe(_ => { }, _ => { });
            e.Handled = true;
            return;
        }

        // F5 / F2 / Delete(资源管理器口径,见 FilePaneKeys)。删除进行中(遮罩挡着)不再接第二次。
        switch (FilePaneKeys.Classify(e, FileList))
        {
            case FilePaneKeyAction.Refresh:
                vm.RefreshCommand.Execute().Subscribe(_ => { }, _ => { });
                e.Handled = true;
                return;
            case FilePaneKeyAction.Rename when !vm.IsLoading && vm.SelectedFiles is [{ IsParentEntry: false } file]:
                vm.RenameCommand.Execute(file).Subscribe(_ => { }, _ => { });
                e.Handled = true;
                return;
            case FilePaneKeyAction.Delete when !vm.IsLoading:
                // 删除命令自己会先弹确认(带服务器名),只选着 ".." 时什么也不做。
                vm.DeleteSelectedCommand.Execute().Subscribe(_ => { }, _ => { });
                e.Handled = true;
                return;
            case FilePaneKeyAction.Back:
                // 与按钮同一个命令、同样先问能不能执行:没有历史、或上一步还没走完时什么也不做。
                FilePaneKeys.Run(vm.GoBackCommand);
                e.Handled = true;
                return;
            case FilePaneKeyAction.Forward:
                FilePaneKeys.Run(vm.GoForwardCommand);
                e.Handled = true;
                return;
        }
    }

    /// <summary>
    /// 进入手动输入态后聚焦输入框并全选:用户十有八九要整条换掉,而不是在现有路径里插字。
    /// 延到 Loaded 优先级是因为输入框此刻刚由 IsVisible 显现,尚未参与布局,立即 Focus 会落空。
    /// </summary>
    private void OnPathEditActivated(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (PathEditBox is not { IsVisible: true } box)
                {
                    return;
                }
                box.Focus();
                box.SelectAll();
            },
            DispatcherPriority.Loaded
        );

    private void OnPathEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                vm.CommitPathEditCommand.Execute().Subscribe(_ => { }, _ => { });
                e.Handled = true;
                return;
            case Key.Escape:
                vm.CancelPathEditCommand.Execute().Subscribe(_ => { }, _ => { });
                e.Handled = true;
                return;
        }
    }

    /// <summary>点到别处即放弃输入,回到面包屑 —— 与地址栏的常见行为一致,不留一个半开的输入框。</summary>
    private void OnPathEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FileBrowserViewModel { IsPathEditing: true } vm)
        {
            vm.CancelPathEditCommand.Execute().Subscribe(_ => { }, _ => { });
        }
    }

    // ---- 当前目录过滤 -----------------------------------------------------------

    /// <summary>用户点开过滤框就把焦点给它(直接能打字);点收起时焦点留在开关上。程序改开关(切换面板)不抢焦点。</summary>
    private void OnFilterToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { IsChecked: true })
        {
            FilePaneFilter.FocusWhenShown(FilterBox);
        }
    }

    /// <summary>过滤框里:Esc 清空并收起,回车把焦点交回列表(过滤留着)。两者都不外溢到宿主。</summary>
    private void OnFilterBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is FileBrowserViewModel vm && FilePaneFilter.HandleKey(e, FileList, () => vm.IsFilterOpen = false))
        {
            e.Handled = true;
        }
    }

    private void OnDirectoryChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                ScrollViewer? scrollViewer = FileList
                    .GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .FirstOrDefault();
                scrollViewer?.Offset = new(0, 0);
            },
            DispatcherPriority.Loaded
        );
    }

    private void OnColumnSplitterPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm || sender is not Control { Tag: string tag })
        {
            return;
        }

        // 这里用 ClickCount,这样即使指针处理/捕获激活时,自适应宽度仍能工作。
        if (e.ClickCount >= 2)
        {
            AutoFitColumnBySplitterTag(vm, tag);
            FilePaneLayoutMemory.SaveColumns(vm);
            e.Handled = true;
            return;
        }
        _activeSplitter = tag;
        _dragStartX = e.GetPosition(this).X;
        _startWidths.Clear();
        foreach (string column in ResizableColumns)
        {
            _startWidths[column] = vm.GetColumnWidth(column).Value;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnColumnSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (
            _activeSplitter is null
            || DataContext is not FileBrowserViewModel vm
            || !_startWidths.TryGetValue(_activeSplitter, out double startWidth)
        )
        {
            return;
        }
        double delta = e.GetPosition(this).X - _dragStartX;
        vm.SetColumnWidth(
            _activeSplitter,
            ClampColumnWidth(
                startWidth + delta,
                FileBrowserViewModel.MinWidthFor(_activeSplitter),
                GetMaxWidth(vm, _activeSplitter)
            )
        );
        e.Handled = true;
    }

    private void OnColumnSplitterReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_activeSplitter is null)
        {
            return;
        }
        _activeSplitter = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (DataContext is FileBrowserViewModel vm)
        {
            FilePaneLayoutMemory.SaveColumns(vm);
        }
    }

    private void AutoFitColumnBySplitterTag(FileBrowserViewModel vm, string tag)
    {
        vm.SetColumnWidth(
            tag,
            ClampColumnWidth(
                EstimateAutoWidth(vm, tag),
                FileBrowserViewModel.MinWidthFor(tag),
                GetMaxWidth(vm, tag)
            )
        );
    }

    /// <summary>
    /// 双击拖拽条的自适应宽度:取表头与所有行里最宽的那条文本。“文件名”列还要
    /// 额外让出前导图标区(14px 图标 + 6px 间距)。上限防止一个超长名字把列撑爆。
    /// </summary>
    private double EstimateAutoWidth(FileBrowserViewModel vm, string columnKey)
    {
        (
            string Header,
            Func<RemoteFileInfoViewModel, string> Cell,
            double Padding,
            double Max
        ) spec = columnKey switch
        {
            "size" => (Strings.Size, f => f.FormattedSize, 8d, 260d),
            "permissions" => (Strings.Permissions, f => f.Permissions, 8d, 300d),
            "owner" => (Strings.PermissionOwner, f => f.Owner, 8d, 300d),
            "group" => (Strings.PermissionGroup, f => f.Group, 8d, 300d),
            "type" => (Strings.FileType, f => f.FileTypeDisplay, 8d, 300d),
            _ => (Strings.FileName, f => f.DisplayName, 34d, 760d),
        };
        // 度量必须用【当前生效的】字体与字号:两者都跟随设置 → 外观 → 界面字体/字号,
        // 写死就会在用户改过设置后把列算窄一截(文字被截断)。
        Typeface typeface = ResolveMonoTypeface();
        double headerWidth = MeasureTextWidth(spec.Header, ResolveFontSize("VelaFontSize10", 10), typeface);
        double rowsWidth = vm.Files.Count > 0
            ? vm.Files.Max(f => MeasureTextWidth(spec.Cell(f), ResolveFontSize("VelaFontSize11", 11), typeface))
            : 0;
        return Math.Clamp(
            Math.Max(headerWidth, rowsWidth) + spec.Padding,
            FileBrowserViewModel.MinWidthFor(columnKey),
            spec.Max
        );
    }

    /// <summary>
    /// 一列能撑到的最大宽度:面板宽度扣掉其余各列、各拖拽条(隐藏列两者都不占位)、
    /// 末列“修改时间”的下限,以及左右内边距。据此拖拽不会把末列挤没。
    /// </summary>
    private double GetMaxWidth(FileBrowserViewModel vm, string columnKey)
    {
        string[] visible = [.. ResizableColumns.Where(vm.IsColumnVisible)];
        double others = visible.Where(c => c != columnKey).Sum(c => vm.GetColumnWidth(c).Value);
        double splitters = visible.Length * SplitterWidth;
        double reserved = vm.IsColumnVisible("modified")
            ? FileBrowserViewModel.MinModifiedWidth
            : 0;
        return Math.Max(
            FileBrowserViewModel.MinWidthFor(columnKey),
            Bounds.Width - others - splitters - reserved - HorizontalPadding
        );
    }

    /// <summary>列表实际渲染用的等宽字体(VelaUiMonoFont 令牌);取不到时退回内置默认。</summary>
    private Typeface ResolveMonoTypeface() =>
        this.TryFindResource("VelaUiMonoFont", out object? value) && value is FontFamily family
            ? new Typeface(family)
            : DefaultMonoTypeface;

    /// <summary>取字号令牌的当前值;取不到时退回默认基准下的磅值。</summary>
    private double ResolveFontSize(string key, double fallback) =>
        this.TryFindResource(key, out object? value) && value is double size ? size : fallback;

    private static double MeasureTextWidth(string text, double fontSize, Typeface typeface)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.White
        );
        return Math.Ceiling(ft.WidthIncludingTrailingWhitespace);
    }

    private static double ClampColumnWidth(double value, double min, double max)
    {
        if (max < min)
        {
            return min;
        }
        return Math.Clamp(value, min, max);
    }

    /// <summary>
    /// 双击一行进入目录,或将文件下载到临时文件夹并用操作系统默认程序打开(§6)。
    /// </summary>
    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm)
        {
            return;
        }
        if ((e.Source as Control)?.DataContext is not RemoteFileInfoViewModel row)
        {
            return;
        }
        vm.ActivateCommand.Execute(row).Subscribe(_ => { }, _ => { });
    }

    /// <summary>
    /// 列表里的回车:只选着一个文件夹(含 "..")时进去,与双击一样;进去之后焦点留在列表里,接着就能敲字定位。
    /// 文件上的回车不做事 —— 双击文件是下载并打开,不该被一个顺手的回车触发。
    /// </summary>
    private void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || e.KeyModifiers != KeyModifiers.None
            || DataContext is not FileBrowserViewModel vm
            || (e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
        {
            return;
        }
        if (vm.SelectedFiles is [{ IsDirectory: true } folder])
        {
            e.Handled = true;
            vm.ActivateCommand.Execute(folder).Subscribe(_ => { }, _ => { });
        }
    }

    /// <summary>用平台默认处理程序打开已下载的本地文件。</summary>
    private async Task OpenLocalFileAsync(string localPath)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }
        await top.Launcher.LaunchFileInfoAsync(new(localPath));
    }

    /// <summary>
    /// 上传的选择步骤:开应用内的选择器,文件与文件夹一次混选。
    /// 系统对话框做不到混选(见 <see cref="LocalPathPickerDialog" />),上传以前正是因此
    /// 被拆成"上传文件""上传文件夹"两个入口。
    /// </summary>
    private async Task<UploadSelectionResult?> PickLocalPathsAsync(string destination)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || _viewModel is not { } vm)
        {
            return null;
        }
        IServiceProvider? services = (Application.Current as App)?.Services;
        Guid? profileId = null;
        if (vm.QueueOrigin is { } origin && services?.GetService<ISessionRepository>() is { } repository)
        {
            try
            {
                // 最近连接重建的临时配置也有 Guid,只有库中存在的配置才是持久映射边界。
                if (await repository.GetSessionAsync(origin.ProfileId) is not null)
                {
                    profileId = origin.ProfileId;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 查不到稳定连接标识时仍可上传,使用全局最近状态。
            }
        }
        return await LocalPathPickerDialog.PickAsync(owner, vm.TransferOptions,
            new Services.UploadPickerContext(profileId, destination, vm.ServerDisplayName),
            vm.UploadDirectoryService, vm.SessionId, vm.NavigateForUploadMappingAsync);
    }

    /// <summary>
    /// 选择文件夹/批量下载的本地目标文件夹,起始位置为设置中的
    /// 本地下载目录 (设置 → 文件传输).
    /// </summary>
    private async Task<string?> PickDownloadFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }
        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(
            new()
            {
                Title = Strings.SelectDownloadFolder,
                AllowMultiple = false,
                SuggestedStartLocation = await ResolveDefaultDownloadFolderAsync(top),
            }
        );
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// 把设置中的下载目录("~" 展开)换成存储提供器的文件夹句柄;失败返回 null
    /// (选择器落在系统默认位置)。
    /// </summary>
    private async Task<IStorageFolder?> ResolveDefaultDownloadFolderAsync(TopLevel top)
    {
        if (DataContext is not FileBrowserViewModel vm)
        {
            return null;
        }
        // "~" 与相对路径一律以用户主目录为基准、留空则跟随系统"下载"文件夹(见 UserPathResolver);
        // 目录不存在时退回主目录,绝不落回进程工作目录 —— 那是外部环境决定的,不是用户想要的落点(#120)。
        return await StorageDefaults.FolderAsync(
                   top,
                   UserPathResolver.ResolveOrDownloads(vm.TransferOptions.LocalDownloadDirectory)
               )
               ?? await StorageDefaults.HomeAsync(top);
    }

    /// <summary>
    /// 正在进行中的一次远程行拖拽:从哪一栏拖出、拖的是哪些条目。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 拖放载荷(<see cref="DragDropFormats.RemotePaths" /> + 路径)里没有会话 —— 它原本只给「远程 → 本地」用,
    /// 本地栏只需要路径。双栏远程文档的另一栏要知道这些路径属于哪台机器,才能从那边读。
    /// 与其改载荷格式(本地栏那边的解析也得跟着改),不如在进程内记下来源:
    /// <see cref="DragDrop.DoDragDropAsync" /> 会一直等到落下或取消才返回,整个拖放期间这份记录都是有效的。
    /// </para>
    /// <para>
    /// 只有来源恰好是本栏的 <see cref="FileBrowserViewModel.DualPeer" /> 才认 ——
    /// 跨会话中转只属于双栏远程文档;同一栏里拖来拖去(#474 否决过的拖动移动)一律不接。
    /// </para>
    /// </remarks>
    private static PeerDrag? _activeRemoteDrag;

    /// <summary>一次远程行拖拽的来源栏与条目。</summary>
    private sealed record PeerDrag(FileBrowserViewModel Source, IReadOnlyList<RemoteFileInfoViewModel> Entries);

    /// <summary>这次拖放是不是双栏远程文档里另一栏拖过来的行;是则给出那次拖拽。</summary>
    private static PeerDrag? PeerDragFor(FileBrowserViewModel target, DragEventArgs e) =>
        _activeRemoteDrag is { } drag
        && target.AcceptsStreamedUploads
        && target.DualPeer is { } peer
        && ReferenceEquals(drag.Source, peer)
        && e.DataTransfer.TryGetText() is { } text
        && text.StartsWith(DragDropFormats.RemotePaths, StringComparison.Ordinal)
            ? drag
            : null;

    private void OnFileListDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        // 接受操作系统文件拖入、跨面板的本地文件拖拽(VFTPL 文本标记),以及双栏远程文档里另一栏的行。
        IReadOnlyList<string> osPaths = ExtractLocalPaths(e);
        bool isCrossPane = !string.IsNullOrEmpty(e.DataTransfer.TryGetText())
            && e.DataTransfer.TryGetText()!.StartsWith(DragDropFormats.LocalPaths);
        bool isPeer = PeerDragFor(vm, e) is not null;
        e.DragEffects = (osPaths.Count > 0 || isCrossPane || isPeer) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileListDrop(object? sender, DragEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not FileBrowserViewModel vm)
        {
            return;
        }
        if (PeerDragFor(vm, e) is { } peerDrag)
        {
            e.Handled = true;
            await vm.ReceiveFromPeerAsync(peerDrag.Entries);
            return;
        }
        IReadOnlyList<string> paths = ExtractLocalPaths(e);
        if (paths.Count == 0)
        {
            // 检查跨面板本地文件拖拽(VFTPL 标记)。
            string? text = e.DataTransfer.TryGetText();
            if (!string.IsNullOrEmpty(text) && text.StartsWith(DragDropFormats.LocalPaths))
            {
                paths = text[DragDropFormats.LocalPaths.Length..].Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
        }
        if (paths.Count == 0)
        {
            return;
        }
        await vm.UploadLocalPathsAsync(paths);
        e.Handled = true;
    });

    private static IReadOnlyList<string> ExtractLocalPaths(DragEventArgs e)
    {
        IStorageItem[]? items = e.DataTransfer.TryGetFiles();
        if (items is null || items.Length == 0)
        {
            return [];
        }
        return
        [
            .. items
                .Select(i => i.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// 「下载为…」的系统另存为对话框。同名时由对话框问「要替换吗」,而且<b>必须问</b>:
    /// 视图模型把用户在这里点的「替换」当作这次的冲突决定,不再走「文件已存在时」策略(见 DownloadItemAsync)。
    /// </summary>
    private async Task<string?> PickSavePathAsync(string suggestedName)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }
        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(
            new()
            {
                Title = Strings.SaveToLocal,
                SuggestedFileName = suggestedName,
                SuggestedStartLocation = await ResolveDefaultDownloadFolderAsync(top),
                ShowOverwritePrompt = true,
            }
        );
        return file?.TryGetLocalPath();
    }

    /// <summary>
    /// 下载遇到本地同名文件且冲突策略为“询问”:覆盖 / 全部覆盖 / 跳过 / 全部跳过。
    /// 无宿主窗口(理论上不至于)时保守按覆盖处理,与旧行为一致。
    /// </summary>
    private Task<FileConflictResolution> ConfirmOverwriteAsync(string localPath) =>
        AskConflictAsync(Strings.Format("Sftp_LocalOverwriteBody", localPath));

    /// <summary>
    /// 上传遇到远端同名文件且冲突策略为“询问”:覆盖 / 全部覆盖 / 跳过 / 全部跳过。
    /// </summary>
    private Task<FileConflictResolution> ConfirmRemoteOverwriteAsync(string remotePath) =>
        AskConflictAsync(Strings.Format("Sftp_RemoteOverwriteBody", remotePath));

    /// <summary>
    /// 弹出四选一冲突弹窗(覆盖 / 全部覆盖 / 跳过 / 全部跳过),批量传输时“全部…”一次决定
    /// 沿用到本批次其余冲突,免去逐文件弹窗。Esc / 关闭 = 跳过该文件(不误覆盖)。
    /// </summary>
    private async Task<FileConflictResolution> AskConflictAsync(string body)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return FileConflictResolution.Overwrite;
        }
        int choice = await MessageDialog.ChooseAsync(
            owner,
            Strings.Get("Sftp_FileExistsTitle"),
            body,
            [
                Strings.Get("Sftp_ConflictOverwrite"),
                Strings.Get("Sftp_ConflictOverwriteAll"),
                Strings.Get("Sftp_ConflictSkip"),
                Strings.Get("Sftp_ConflictSkipAll"),
            ],
            primaryIndex: 0,
            cancelResult: 2, // Esc / 关闭 = 跳过该文件(下标 2)。
            kind: MessageDialogKind.Warning
        );
        return choice switch
        {
            0 => FileConflictResolution.Overwrite,
            1 => FileConflictResolution.OverwriteAll,
            3 => FileConflictResolution.SkipAll,
            _ => FileConflictResolution.Skip,
        };
    }

    /// <summary>
    /// 模态单行文本输入,用于新建文件夹 / 新建文件 / 重命名 / 移动。
    /// 返回用户输入的文本,用户取消则返回 null。
    /// </summary>
    private async Task<string?> PromptForTextAsync(string title, string initialValue)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }
        return await MessageDialog.PromptAsync(owner, title, initialValue);
    }

    /// <summary>上传前预检:目标分区可能放不下时问一句。</summary>
    private async Task<bool> ConfirmLowSpaceAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return true;
        }
        return await MessageDialog.ConfirmAsync(
            owner,
            Strings.Get("FileBrowser_LowSpaceTitle"),
            message,
            Strings.Get("FileBrowser_UploadAnyway"),
            kind: MessageDialogKind.Warning
        );
    }

    /// <summary>用于危险操作(删除)的模态确认(是/否)。返回 true 表示继续。</summary>
    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }
        return await MessageDialog.ConfirmAsync(
            owner,
            Strings.ConfirmDeleteTitle,
            message,
            Strings.Delete,
            kind: MessageDialogKind.Warning,
            danger: true
        );
    }

    private async Task CopyToClipboardAsync(string text)
    {
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    /// <summary>未配置默认编辑器:弹窗说明配置位置,确认则直接打开设置窗口(文件传输页)。</summary>
    private async Task PromptConfigureEditorAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        bool openSettings = await MessageDialog.ConfirmAsync(
            owner,
            Strings.Get("Sftp_NoEditorTitle"),
            Strings.Get("Sftp_NoEditorBody"),
            Strings.Get("Sftp_OpenSettings"),
            kind: MessageDialogKind.Info
        );
        if (!openSettings)
        {
            return;
        }

        // 直达 设置 → 文件传输 页。按枚举跳而不是写死下标:设置页中间插一页,下标就静默错位。
        if (
            Application.Current is App app
            && app.Services?.GetService<SettingsViewModel>() is { } settingsViewModel
        )
        {
            settingsViewModel.SelectSection(SettingsSectionKey.Transfer);
        }
        if (owner.DataContext is MainWindowViewModel mainViewModel)
        {
            mainViewModel.OpenSettingsCommand.Execute().Subscribe();
        }
    }

    /// <summary>
    /// 打开(或提到前面)这个面板的搜索窗口。一个面板一扇:窗口按它定位结果的那个面板认,
    /// 而不是按这个视图 —— 终端侧栏的视图会随标签换 DataContext,各标签的面板各有各的窗口。
    /// </summary>
    /// <param name="vm">面板。</param>
    /// <param name="root">起点目录;null = 面板当前目录(已开着的窗口则不动它填的位置)。</param>
    private Task ShowRemoteSearchAsync(FileBrowserViewModel vm, string? root)
    {
        if (vm.RemoteSearch is not { } service || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return Task.CompletedTask;
        }
        if (owner.OwnedWindows.FirstOrDefault(window =>
                window is RemoteSearchWindow { DataContext: RemoteSearchViewModel search } && ReferenceEquals(search.Pane, vm))
            is { DataContext: RemoteSearchViewModel existing } open)
        {
            if (root is not null)
            {
                existing.Root = root;
            }
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }
            open.Activate();
            return Task.CompletedTask;
        }
        var model = new RemoteSearchViewModel(service, vm);
        if (root is not null)
        {
            model.Root = root;
        }
        var searchWindow = new RemoteSearchWindow { DataContext = model };
        // 面板拆掉了(标签关闭、会话断开):窗口跟着关,不留一扇对着死会话、点了也定位不到的窗口。
        void OnPaneDetached(object? sender, EventArgs e) => Dispatcher.UIThread.Post(searchWindow.Close);
        vm.Detached += OnPaneDetached;
        searchWindow.Closed += (_, _) => vm.Detached -= OnPaneDetached;
        searchWindow.Show(owner);
        return Task.CompletedTask;
    }

    /// <summary>搜索结果定位到的那一行滚进可见区。</summary>
    private void OnRevealRequested(object? sender, RemoteFileInfoViewModel row)
    {
        if (ReferenceEquals(sender, _viewModel))
        {
            FileList.ScrollIntoView(row);
        }
    }

    /// <summary>「打开」:非模态弹出内置 AvaloniaEdit 编辑器,保存时经回调上传回服务器。</summary>
    private Task OpenInBuiltInEditorAsync(
        RemoteFileInfoViewModel file,
        string localPath,
        Func<Task> uploadAsync
    )
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return Task.CompletedTask;
        }
        // 把会话编码带进去:UTF-8 严格解码失败时(GBK / Big5 / Shift_JIS 文件)
        // 靠它回落,而不是用会静默把每个中文字变成 � 的替换解码器。
        Encoding sessionEncoding = owner.DataContext is MainWindowViewModel main
            ? main.ActiveSessionEncoding
            : Encoding.UTF8;
        var editor = new RemoteFileEditorView(file.Name, file.FullPath, localPath, uploadAsync, sessionEncoding);
        editor.Show(owner);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 右键弹菜单前把所指行并入选区(资源管理器惯例):已在多选内则保持
    /// 多选不变,否则改为仅选中该行 —— 否则"下载/删除"(作用于选中集合)会对着
    /// 旧选区操作。
    /// </summary>
    private void FileRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
        {
            // 左键:为可能拖往本地面板做准备(仅 SFTP 模式)。
            if (DataContext is FileBrowserViewModel { IsDragEnabled: true }
                && sender is Border { DataContext: RemoteFileInfoViewModel file }
                && !file.IsParentEntry
                && MarqueeSelection.IsDndSurface(e.Source, (Visual)sender, e.GetPosition((Visual)sender)))
            {
                _dragOrigin = e.GetPosition(this.FindControl<ListBox>("FileList"));
                _dragRow = file;
                _dragPointerArgs = e;
                _isDragging = false;
            }
            return;
        }
        if (
            sender is not Border { DataContext: RemoteFileInfoViewModel file2 }
            || this.FindControl<ListBox>("FileList") is not { } listBox
        )
        {
            return;
        }
        if (file2.IsParentEntry)
        {
            return;
        }
        if (listBox.SelectedItems is { } selection && !selection.Contains(file2))
        {
            selection.Clear();
            selection.Add(file2);
        }
    }

    /// <summary>
    /// 认定协议动作作用的目标行。
    /// <para>
    /// 用 ContextRequested 而不是 PointerPressed 的右键分支:触摸屏/触控笔的**长按**
    /// 由 Avalonia 的 Holding 手势翻译成 ContextRequested,根本不经过右键按下 ——
    /// 那条路上 ContextTarget 会停在上一次鼠标右键命中的行上,
    /// 于是「复制分享链接」会给一个早已不在当前目录的对象生成预签名 URL 并静默写进剪贴板。
    /// </para>
    /// </summary>
    private void FileRow_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is FileBrowserViewModel browser && sender is Border { DataContext: RemoteFileInfoViewModel row })
        {
            // 上级目录那一行按「空白处」处理:只有 Background 作用域的动作适用。
            browser.ContextTarget = row.IsParentEntry ? null : row;
        }
    }

    /// <summary>列表空白处右键:目标为空,只留 Background/Any 作用域的动作。</summary>
    private void FileList_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is FileBrowserViewModel browser)
        {
            browser.ContextTarget = null;
        }
    }

    private void OnRemoteDragPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        _selectionAtPress.Clear();
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed
            || FileList.SelectedItems is not { } selected)
        {
            return;
        }

        foreach (object? item in selected)
        {
            if (item is RemoteFileInfoViewModel file && !file.IsParentEntry)
            {
                _selectionAtPress.Add(file);
            }
        }
    }

    private void OnRemoteDragPointerPressedBubble(object? sender, PointerPressedEventArgs e)
    {
        if (_dragRow is not { } source
            || _selectionAtPress.Count <= 1
            || !_selectionAtPress.Contains(source)
            || e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            || DataContext is not FileBrowserViewModel vm)
        {
            return;
        }

        DragSelectionResolver.SynchronizeSelection(vm.SelectedFiles, _selectionAtPress);
        _selectionRestoredForPendingDrag = true;
    }

    private void OnRemoteDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging || _dragRow is null || _dragPointerArgs is null)
        {
            return;
        }
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed)
        {
            ResetRemoteDragGesture();
            return;
        }
        Point current = e.GetPosition(this.FindControl<ListBox>("FileList"));
        if (Math.Abs(current.X - _dragOrigin.X) < DragThreshold && Math.Abs(current.Y - _dragOrigin.Y) < DragThreshold)
        {
            return;
        }

        _isDragging = true;
        _ = StartRemoteDragAsync(_dragRow, _dragPointerArgs);
    }

    private void OnRemoteDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging
            && _selectionRestoredForPendingDrag
            && _dragRow is { } source
            && DataContext is FileBrowserViewModel vm)
        {
            DragSelectionResolver.SynchronizeSelection(vm.SelectedFiles, [source]);
        }
        ResetRemoteDragGesture();
    }

    private async Task StartRemoteDragAsync(RemoteFileInfoViewModel source, PointerPressedEventArgs pointerArgs)
    {
        if (DataContext is FileBrowserViewModel vm)
        {
            IReadOnlyList<RemoteFileInfoViewModel> entries = DragSelectionResolver.ResolveAtDragStart(
                _selectionAtPress,
                vm.SelectedFiles,
                source,
                item => item.IsParentEntry,
                usePressSnapshot: !pointerArgs.KeyModifiers.HasFlag(KeyModifiers.Control)
                    && !pointerArgs.KeyModifiers.HasFlag(KeyModifiers.Shift));
            DragSelectionResolver.SynchronizeSelection(vm.SelectedFiles, entries);
            string[] paths = [.. entries.Select(item => item.FullPath)];
            // 登记这次拖拽的来源,供双栏远程文档的另一栏认领(见 _activeRemoteDrag)。
            _activeRemoteDrag = new(vm, entries);
            try
            {
                await StartRemoteDragAsync(paths, pointerArgs);
            }
            finally
            {
                _activeRemoteDrag = null;
            }
            return;
        }
        await StartRemoteDragAsync([source.FullPath], pointerArgs);
    }

    private async Task StartRemoteDragAsync(
        IReadOnlyList<string> paths,
        PointerPressedEventArgs pointerArgs)
    {
        var data = new DataTransfer();
        var dragItem = new DataTransferItem();
        dragItem.SetText(DragDropFormats.RemotePaths + string.Join("\n", paths));
        data.Add(dragItem);

        try
        {
            await DragDrop.DoDragDropAsync(pointerArgs, data, DragDropEffects.Copy);
        }
        finally
        {
            ResetRemoteDragGesture();
        }
    }

    private void ResetRemoteDragGesture()
    {
        _isDragging = false;
        _dragRow = null;
        _dragPointerArgs = null;
        _selectionAtPress.Clear();
        _selectionRestoredForPendingDrag = false;
    }

    /// <summary>
    /// 属性弹窗(参考 WinSCP):基本信息 + 属主/属组 + rwx 权限矩阵 + 八进制输入合并在一个界面。
    /// 文本着色一律走 MessageDialog 的 BodyHost 样式类(dim/mono/mono-accent)—— 代码里
    /// FindResource 取不到主题字典的画刷(会拿到 null 把文字画没)。
    /// <paramref name="ownerChoices" /> 不为 null 时属主/属组是可编辑下拉(候选取自远端 passwd/group),
    /// 为 null 时后端改不了属主,两行只读。确定时返回改了的项,取消时返回 null。
    /// </summary>
    private async Task<FilePropertiesChange?> ShowFilePropertiesAsync(RemoteFileInfoViewModel file, RemoteOwnerChoices? ownerChoices)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }
        var content = new StackPanel { Spacing = 14, MinWidth = 360 };

        // ── 头部:类型图标 + 名称 ─────────────────────────────────────────────
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var typeIcon = new LucideIcon
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Data = this.FindResource(
                (file.IsSymbolicLink, file.IsDirectory) switch
                {
                    (true, true) => "Icon.folder-symlink",
                    (true, false) => "Icon.file-symlink",
                    (false, true) => "Icon.folder",
                    _ => "Icon.file",
                }) as Geometry,
        };
        typeIcon.Classes.Add(file.IsDirectory ? "folder" : "file");
        header.Children.Add(typeIcon);
        header.Children.Add(
            new TextBlock
            {
                Text = file.Name,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            }
        );
        content.Children.Add(header);

        // ── 基本信息 ────────────────────────────────────────────────────────
        var rows = new StackPanel { Spacing = 8 };

        void AddRow(string label, string value, bool mono = true)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            var grid = new Grid { ColumnDefinitions = [with("96,*")] };
            var labelText = new TextBlock { Text = label };
            labelText.Classes.Add("dim");
            grid.Children.Add(labelText);
            var valueText = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            if (mono)
            {
                valueText.Classes.Add("mono");
            }
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);
            rows.Children.Add(grid);
        }

        AddRow(
            Strings.FileType,
            file.IsSymbolicLink ? Strings.Get("Sftp_SymlinkType") : file.IsDirectory ? Strings.Folder : Strings.File,
            false);
        AddRow(Strings.FilePath, file.FullPath);
        AddRow(Strings.Get("Sftp_LinkTarget"), file.LinkTarget ?? string.Empty);
        AddRow(Strings.Size, file.FormattedSize);
        AddRow(Strings.Modified, file.FormattedModifiedTime);

        // 可编辑下拉:从候选里挑,或直接填名称 / 数字 id(候选为空时 —— 纯 SFTP 账号查不到表 —— 只能填数字)。
        ComboBox AddOwnerRow(string label, string value, IReadOnlyList<string> choices, string placeholder)
        {
            var grid = new Grid { ColumnDefinitions = [with("96,*")] };
            var labelText = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            labelText.Classes.Add("dim");
            grid.Children.Add(labelText);
            var box = new ComboBox
            {
                IsEditable = true,
                ItemsSource = choices,
                Text = value,
                PlaceholderText = placeholder,
                MaxDropDownHeight = 240,
            };
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            rows.Children.Add(grid);
            return box;
        }

        ComboBox? ownerBox = null;
        ComboBox? groupBox = null;
        if (ownerChoices is null)
        {
            AddRow(Strings.PermissionOwner, file.Owner);
            AddRow(Strings.PermissionGroup, file.Group);
        }
        else
        {
            ownerBox = AddOwnerRow(Strings.PermissionOwner, file.Owner, ownerChoices.Users, Strings.Get("Sftp_OwnerPlaceholder"));
            groupBox = AddOwnerRow(Strings.PermissionGroup, file.Group, ownerChoices.Groups, Strings.Get("Sftp_GroupPlaceholder"));
        }
        content.Children.Add(rows);

        // ── 权限矩阵("drwxr-xr-x" → 9 个 rwx 标志;异常串回退为全不勾) ────────
        bool[] flags = new bool[9];
        string perms = file.Permissions;
        if (perms.Length == 10)
        {
            for (int i = 0; i < 9; i++)
            {
                flags[i] = perms[i + 1] != '-';
            }
        }
        var grid = new Grid
        {
            ColumnDefinitions = [with("96,Auto,Auto,Auto")],
            RowDefinitions = [with("Auto,Auto,Auto,Auto")],
        };

        void Place(Control control, int row, int column)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            if (column > 0)
            {
                control.Margin = new(0, 4, 24, 4);
            }
            else
            {
                control.Margin = new(0, 4);
            }
            grid.Children.Add(control);
        }

        string[] columnHeaders =
        [
            Strings.PermissionRead,
            Strings.PermissionWrite,
            Strings.PermissionExecute,
        ];
        string[] rowHeaders =
        [
            Strings.PermissionOwner,
            Strings.PermissionGroup,
            Strings.PermissionOthers,
        ];
        var permTitle = new TextBlock { Text = Strings.Permissions };
        permTitle.Classes.Add("dim");
        Place(permTitle, 0, 0);
        for (int c = 0; c < 3; c++)
        {
            var head = new TextBlock { Text = columnHeaders[c], FontWeight = FontWeight.Medium };
            Place(head, 0, c + 1);
        }
        var boxes = new CheckBox[9];

        short CurrentMode()
        {
            short mode = 0;
            for (int g = 0; g < 3; g++)
            {
                int digit =
                    (boxes[g * 3].IsChecked == true ? 4 : 0)
                    + (boxes[(g * 3) + 1].IsChecked == true ? 2 : 0)
                    + (boxes[(g * 3) + 2].IsChecked == true ? 1 : 0);
                mode = (short)((mode * 10) + digit);
            }
            return mode;
        }

        // 八进制输入与勾选矩阵双向同步(参考图中的"八进制"输入行)。
        var octalBox = new TextBox { Width = 90, MaxLength = 3 };
        bool syncing = false;

        void SyncOctalFromBoxes()
        {
            if (syncing)
            {
                return;
            }
            syncing = true;
            octalBox.Text = $"{CurrentMode():000}";
            syncing = false;
        }

        void SyncBoxesFromOctal()
        {
            if (syncing || octalBox.Text is not { Length: 3 } text)
            {
                return;
            }
            syncing = true;
            for (int g = 0; g < 3; g++)
            {
                if (text[g] is < '0' or > '7')
                {
                    continue;
                }
                int digit = text[g] - '0';
                boxes[g * 3].IsChecked = (digit & 4) != 0;
                boxes[(g * 3) + 1].IsChecked = (digit & 2) != 0;
                boxes[(g * 3) + 2].IsChecked = (digit & 1) != 0;
            }
            syncing = false;
        }

        for (int r = 0; r < 3; r++)
        {
            var rowLabel = new TextBlock
            {
                Text = rowHeaders[r],
                VerticalAlignment = VerticalAlignment.Center,
            };
            rowLabel.Classes.Add("dim");
            Place(rowLabel, r + 1, 0);
            for (int c = 0; c < 3; c++)
            {
                var box = new CheckBox
                {
                    IsChecked = flags[(r * 3) + c],
                    MinWidth = 0,
                    Padding = new(0),
                };
                box.IsCheckedChanged += (_, _) => SyncOctalFromBoxes();
                boxes[(r * 3) + c] = box;
                Place(box, r + 1, c + 1);
            }
        }
        short initialMode = CurrentMode();
        SyncOctalFromBoxes();
        octalBox.TextChanged += (_, _) => SyncBoxesFromOctal();
        content.Children.Add(grid);
        var octalRow = new Grid { ColumnDefinitions = [with("96,*")] };
        var octalLabel = new TextBlock
        {
            Text = Strings.Get("Sftp_Octal"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        octalLabel.Classes.Add("dim");
        octalRow.Children.Add(octalLabel);
        var octalHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        octalHost.Children.Add(octalBox);
        var chmodEcho = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        chmodEcho.Classes.Add("mono-accent");
        void RefreshEcho() => chmodEcho.Text = $"chmod {CurrentMode():000}";
        RefreshEcho();
        foreach (CheckBox box in boxes)
        {
            box.IsCheckedChanged += (_, _) => RefreshEcho();
        }
        octalBox.TextChanged += (_, _) => RefreshEcho();
        octalHost.Children.Add(chmodEcho);
        Grid.SetColumn(octalHost, 1);
        octalRow.Children.Add(octalHost);
        content.Children.Add(octalRow);
        bool confirmed = await MessageDialog.ShowCustomAsync(owner, Strings.Properties, content);
        if (!confirmed)
        {
            return null;
        }
        short newMode = CurrentMode();
        return new(
            newMode == initialMode ? null : newMode,
            ChangedOwnerText(ownerBox, file.Owner),
            ChangedOwnerText(groupBox, file.Group));
    }

    /// <summary>属主/属组下拉里改过的文本;没改、清空或只读(<paramref name="box" /> 为 null)时为 null。</summary>
    /// <remarks>清空当作没改:属主不存在「设成空」这回事。</remarks>
    private static string? ChangedOwnerText(ComboBox? box, string original)
    {
        string text = box?.Text?.Trim() ?? string.Empty;
        return text.Length == 0 || string.Equals(text, original, StringComparison.Ordinal) ? null : text;
    }
}
