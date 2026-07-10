using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VelaShell.ViewModels;

namespace VelaShell.Views;

public partial class CommandPaletteView : UserControl
{
    private CommandPaletteViewModel? _vm;

    public CommandPaletteView()
    {
        InitializeComponent();
        // Tunnel so arrow/enter/escape are intercepted before the search TextBox consumes them.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = DataContext as CommandPaletteViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && _vm?.IsOpen == true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                TextBox? box = this.FindControl<TextBox>("SearchBox");
                box?.Focus();
                box?.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Down:
                _vm.MoveDown();
                e.Handled = true;
                break;
            case Key.Up:
                _vm.MoveUp();
                e.Handled = true;
                break;
            case Key.Enter:
                _vm.ExecuteSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.Close();
                e.Handled = true;
                break;
        }
    }

    private void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is not null && sender is Control { DataContext: CommandPaletteItem item })
        {
            _vm.Activate(item);
        }
    }
}
