using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VelaShell.Docking.Model;

/// <summary>
/// VelaDock 模型基类:纯 INotifyPropertyChanged,不依赖任何 UI 框架,
/// 保证整个布局树可以脱离 Avalonia 单元测试(docs/dock-replacement-plan.md §2.1)。
/// </summary>
public abstract class DockElement : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
