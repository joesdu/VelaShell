using ReactiveUI;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>导入连接文件对话框里的一行(#571):文件里的一条连接、它与本机的比对结果、勾没勾上。</summary>
public sealed class SessionFileImportItemViewModel : ReactiveObject
{
    private SessionImportConflict _conflict = SessionImportConflict.Skip;

    /// <summary>用一条比对结果构造。</summary>
    /// <param name="item">比对结果。</param>
    public SessionFileImportItemViewModel(SessionImportPlanItem item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        IsSelected = CanSelect;
    }

    /// <summary>底层的比对结果。</summary>
    public SessionImportPlanItem Item { get; }

    private SessionImportCandidate Candidate => Item.Candidate;

    /// <summary>是否勾选导入。</summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value && CanSelect);
    }

    /// <summary>
    /// 能不能勾:有错误的行不能;与本机重复、而重复的处理方式是「跳过」时也不能 —— 勾了也不会写,
    /// 让它看起来能勾只会让人以为点了「导入」它就进去了。
    /// </summary>
    public bool CanSelect => Candidate.IsValid && !(Item.IsDuplicate && _conflict == SessionImportConflict.Skip);

    /// <summary>显示名称。</summary>
    public string Name => Candidate.Profile.Name;

    /// <summary>连接目标(<c>user@host:port</c>)。</summary>
    public string Endpoint
    {
        get
        {
            SessionProfile profile = Candidate.Profile;
            string target = profile.Host.Contains(':', StringComparison.Ordinal) ? $"[{profile.Host}]" : profile.Host;
            return profile.Username.Length > 0 ? $"{profile.Username}@{target}:{profile.Port}" : $"{target}:{profile.Port}";
        }
    }

    /// <summary>协议标签(与 CSV 协议列同一套写法,大写)。</summary>
    public string Protocol => Candidate.Profile.ConnectionType == ConnectionType.Plugin
        ? Candidate.Profile.PluginProtocolId ?? "plugin"
        : SessionCsv.FormatProtocol(Candidate.Profile).ToUpperInvariant();

    /// <summary>落到哪个分组(由对话框按「放到哪里」的选择回填)。</summary>
    public string GroupLabel
    {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>这一行是否有致命问题。</summary>
    public bool HasError => !Candidate.IsValid;

    /// <summary>这一行是否与本机已有连接重复。</summary>
    public bool IsDuplicate => Item.IsDuplicate;

    /// <summary>
    /// 状态说明:错误原因(CSV 带行号)/「已存在,跳过」/「将覆盖」/「已存在,另存一条」/「新建」。
    /// </summary>
    public string StatusText
    {
        get
        {
            if (!Candidate.IsValid)
            {
                string errors = string.Join("; ", Candidate.Errors);
                return Candidate.LineNumber is { } line ? Strings.Format("SessImport_LineFmt", line, errors) : errors;
            }
            if (Item.Existing is not { } existing)
            {
                return Strings.Get("SessImport_StatusNew");
            }
            return _conflict switch
            {
                SessionImportConflict.Overwrite => Strings.Format("SessImport_StatusOverwrite", existing.Name),
                SessionImportConflict.KeepBoth => Strings.Format("SessImport_StatusKeepBoth", existing.Name),
                _ => Strings.Format("SessImport_StatusSkip", existing.Name)
            };
        }
    }

    /// <summary>提示(无效 Id、找不到的跳板 / 共享凭据…);没有时为空串。</summary>
    public string WarningText => string.Join("; ", Candidate.Warnings.Concat(Item.Warnings));

    /// <summary>是否有提示。</summary>
    public bool HasWarning => Candidate.Warnings.Count > 0 || Item.Warnings.Count > 0;

    /// <summary>这一行是否带着密码(CSV 密码列,或解锁后的 JSON 机密)。</summary>
    public bool HasSecret => Candidate.HasSecret;

    /// <summary>重复的处理方式变了:重算能不能勾与状态文案,并按新规则重设勾选。</summary>
    /// <param name="conflict">新的处理方式。</param>
    internal void ApplyConflict(SessionImportConflict conflict)
    {
        _conflict = conflict;
        this.RaisePropertyChanged(nameof(CanSelect));
        this.RaisePropertyChanged(nameof(StatusText));
        IsSelected = CanSelect;
    }

    /// <summary>机密解开之后,「带着密码」那个小标记要跟着变。</summary>
    internal void RefreshSecret() => this.RaisePropertyChanged(nameof(HasSecret));
}
