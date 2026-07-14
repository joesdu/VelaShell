using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>连接配置编辑窗口,用于新建或编辑连接档案并支持保存后立即连接。</summary>
public partial class ConnectionProfileView : Window
{
    /// <summary>初始化连接配置窗口,并在打开时绑定命令与加载分组数据。</summary>
    public ConnectionProfileView()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
        {
            return;
        }
        viewModel.SaveCommand.Subscribe(Close);
        viewModel.ConnectCommand.Subscribe(Close);
        viewModel.CancelCommand.Subscribe(Close);
        await viewModel.LoadGroupsAsync();
    }

    /// <summary>无系统标题栏 —— 按住头部可拖动窗口。</summary>
    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private async void BrowseKeyFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("Profile_SelectKeyFile"),
            AllowMultiple = false
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            viewModel.PrivateKeyPath = path;
        }
    }
}
