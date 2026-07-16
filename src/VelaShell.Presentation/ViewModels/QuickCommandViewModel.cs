using ReactiveUI;
using VelaShell.Core.Models;

namespace VelaShell.Presentation.ViewModels;

/// <summary>快捷命令的视图模型:包装 <see cref="QuickCommand" /> 模型并暴露可绑定属性;内置命令为只读,编辑时静默忽略写入。</summary>
public class QuickCommandViewModel(QuickCommand model) : ReactiveObject
{
    private readonly QuickCommand _model = model ?? throw new ArgumentNullException(nameof(model));

    /// <summary>命令的唯一标识。</summary>
    public Guid Id => _model.Id;

    /// <summary>是否为内置命令;内置命令只读,不可编辑。</summary>
    public bool IsBuiltIn => _model.IsBuiltIn;

    /// <summary>命令显示名称;内置命令忽略写入。</summary>
    public string Name
    {
        get;
        set
        {
            if (IsBuiltIn)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            _model.Name = value;
        }
    } = model.Name;

    /// <summary>命令所属分组标识;内置命令忽略写入。</summary>
    public Guid GroupId
    {
        get => _model.GroupId;
        set
        {
            if (IsBuiltIn)
            {
                return;
            }
            if (_model.GroupId == value)
            {
                return;
            }
            _model.GroupId = value;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>当前所属分组的显示名称。</summary>
    public string Category
    {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>命令的实际执行文本;内置命令忽略写入。</summary>
    public string CommandText
    {
        get;
        set
        {
            if (IsBuiltIn)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            _model.CommandText = value;
        }
    } = model.CommandText;

    /// <summary>命令描述说明;内置命令忽略写入。</summary>
    public string Description
    {
        get;
        set
        {
            if (IsBuiltIn)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            _model.Description = value;
        }
    } = model.Description;

    /// <summary>在分组内的显示顺序。</summary>
    public int SortOrder
    {
        get => _model.SortOrder;
        set
        {
            if (IsBuiltIn || _model.SortOrder == value)
            {
                return;
            }
            _model.SortOrder = value;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>返回底层的 <see cref="QuickCommand" /> 模型实例。</summary>
    public QuickCommand ToModel() => _model;
}
