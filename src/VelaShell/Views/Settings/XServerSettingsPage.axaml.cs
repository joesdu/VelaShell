using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views.Settings;

/// <summary>X Server 设置页:引擎(内置 / VcXsrv),以及 VcXsrv 的位置与启动参数。</summary>
public partial class XServerSettingsPage : UserControl
{
    /// <summary>初始化 X Server 设置页并加载 XAML 组件。</summary>
    public XServerSettingsPage() => InitializeComponent();

    private void BrowseExecutable_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        FilePickerOpenOptions options = new()
        {
            Title = Strings.Get("SetXServer_SelectExecutableTitle"),
            AllowMultiple = false,
            // 已填过就从它所在目录出发,否则从 Program Files 出发(官方安装包默认装在那下面)。
            SuggestedStartLocation = await StorageDefaults.FolderAsync(top, Path.GetDirectoryName(ExecutableBox.Text))
                                     ?? await StorageDefaults.FolderAsync(
                                         top,
                                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                                     ),
            FileTypeFilter =
            [
                new(Strings.Get("SetTransfer_ExecutableFilter")) { Patterns = ["*.exe"] },
                FilePickerFileTypes.All
            ]
        };
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(options);
        if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            // 直接写控件,由 TwoWay 绑定回写 POCO;VM 监听到路径变化会重新查找并刷新状态行。
            ExecutableBox.Text = path;
        }
    });

    private void Help_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await new XServerHelpDialog().ShowDialog(owner);
        }
    });
}
