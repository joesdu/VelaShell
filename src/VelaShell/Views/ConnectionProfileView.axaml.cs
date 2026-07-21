using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
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

    private void ApplyProtoTabFocusAdorner()
    {
        var buttons = this.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("proto-tab"))
            .ToList();
        foreach (Button button in buttons)
        {
            var layer = AdornerLayer.GetAdornerLayer(button);
            layer?.DefaultFocusAdorner = null;

            var controls = new[] { button }
                .Concat(button.GetVisualDescendants().OfType<Control>())
                .ToList();
            foreach (Control control in controls)
            {
                control.FocusAdorner = null;
                control.SetValue(AdornerLayer.DefaultFocusAdornerProperty, null);
            }
            button.GotFocus += ProtoTab_GotFocus;
            button.LostFocus += ProtoTab_LostFocus;
        }
    }

    private void ProtoTab_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is Button button)
        {
            AdornerLayer.SetAdorner(button, CreateVelaFocusAdorner());
        }
    }

    private static void ProtoTab_LostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is Button button)
        {
            AdornerLayer.SetAdorner(button, null);
        }
    }

    private Border CreateVelaFocusAdorner()
    {
        Border border = new()
        {
            Background = this.FindResource("VelaAccentDim") as IBrush,
            BorderBrush = this.FindResource("VelaAccent") as IBrush,
            BorderThickness = new Avalonia.Thickness(1),
            IsHitTestVisible = false
        };
        return border;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not ConnectionProfileViewModel viewModel)
        {
            return;
        }
        ApplyProtoTabFocusAdorner();
        viewModel.SaveCommand.Subscribe(Close);
        viewModel.ConnectCommand.Subscribe(Close);
        viewModel.CancelCommand.Subscribe(Close);
        await viewModel.LoadGroupsAsync();
    }

    /// <summary>Esc 等价于点击取消:经 CancelCommand 走与取消按钮完全相同的关闭路径(不保存改动)。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is ConnectionProfileViewModel viewModel)
            {
                viewModel.CancelCommand.Execute().Subscribe();
            }
            else
            {
                Close();
            }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
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
        if (files.AsParallel().FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            viewModel.PrivateKeyPath = path;
        }
    }
}
