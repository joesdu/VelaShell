using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VelaShell.Docking.Model;

/// <summary>
/// VelaDock 模型基类:纯 INotifyPropertyChanged,不依赖任何 UI 框架,
/// 保证整个布局树可以脱离 Avalonia 单元测试(docs/dock-replacement-plan.md §2.1)。
/// </summary>
public abstract class DockElement : INotifyPropertyChanged
{
    /// <summary>属性值变更时触发,用于向绑定层通知刷新。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>在值确有变化时更新后备字段并触发 <see cref="PropertyChanged" /> 通知。</summary>
    /// <typeparam name="T">属性的值类型。</typeparam>
    /// <param name="field">被更新的后备字段(按引用传入)。</param>
    /// <param name="value">要写入的新值。</param>
    /// <param name="propertyName">变更的属性名,默认由编译器填入调用方成员名。</param>
    /// <returns>值发生变化并已触发通知返回 <c>true</c>;否则返回 <c>false</c>。</returns>
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
