using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

/// <summary>
/// 快捷命令的视图模型:包装 <see cref="QuickCommand" /> 模型并暴露可绑定属性。
/// 内置命令的实例只读(写入静默忽略)—— 改内置命令是换上一条同标识的自定义命令(#555),
/// 由 <see cref="QuickCommandsViewModel" /> 负责替换。
/// </summary>
public class QuickCommandViewModel(QuickCommand model) : ReactiveObject
{
    private readonly QuickCommand _model = model ?? throw new ArgumentNullException(nameof(model));

    /// <summary>命令的唯一标识。</summary>
    public Guid Id => _model.Id;

    /// <summary>是否为目录里原样的内置命令;这一实例只读。</summary>
    public bool IsBuiltIn => _model.IsBuiltIn;

    /// <summary>
    /// 是否为改过(或挪到别的分组)的内置命令:自定义命令占着内置命令的标识。
    /// 这种命令可以「恢复默认」—— 去掉这条,目录里的原样那条回来。
    /// </summary>
    public bool IsBuiltInOverride => !IsBuiltIn && QuickCommandCatalog.IsBuiltInId(Id);

    /// <summary>正文的第一行:侧栏一行放不下整条多行命令。</summary>
    public string CommandPreview => QuickCommandText.FirstLine(CommandText);

    /// <summary>正文是否有多行。</summary>
    public bool IsMultiline => QuickCommandText.IsMultiline(CommandText);

    /// <summary>多行命令在第一行之后还有几行,如「+2 行」;单行命令为空串。</summary>
    public string MoreLinesText =>
        IsMultiline ? Strings.Format("QuickCmd_MoreLines", QuickCommandText.LineCount(CommandText) - 1) : string.Empty;

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
            this.RaisePropertyChanged(nameof(CommandPreview));
            this.RaisePropertyChanged(nameof(IsMultiline));
            this.RaisePropertyChanged(nameof(MoreLinesText));
        }
    } = model.CommandText;

    /// <summary>发送后是否按回车;内置命令忽略写入。</summary>
    public bool ExecuteAfterSend
    {
        get;
        set
        {
            if (IsBuiltIn)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            _model.ExecuteAfterSend = value;
        }
    } = model.ExecuteAfterSend;

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
