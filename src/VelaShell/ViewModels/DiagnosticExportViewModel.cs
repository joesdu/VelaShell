using ReactiveUI;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Diagnostics;

namespace VelaShell.ViewModels;

/// <summary>导出诊断包对话框里的一个可选项:一类日志,默认不勾。</summary>
public sealed class DiagnosticExportItem : ReactiveObject
{
    /// <summary>按一类日志建勾选项。</summary>
    /// <param name="option">这一类日志。</param>
    public DiagnosticExportItem(DiagnosticLogOption option)
    {
        Option = option;
        (Label, Hint) = option.Kind switch
        {
            DiagnosticLogKind.Transfer => (Strings.Get("DiagBundle_OptionTransfer"), Strings.Get("DiagBundle_OptionTransferHint")),
            DiagnosticLogKind.RemoteEdit => (Strings.Get("DiagBundle_OptionRemoteEdit"), Strings.Get("DiagBundle_OptionRemoteEditHint")),
            DiagnosticLogKind.VcXsrv => (Strings.Get("DiagBundle_OptionVcXsrv"), Strings.Get("DiagBundle_OptionVcXsrvHint")),
            _ => (Strings.Format("DiagBundle_OptionPlugin", option.PluginId ?? string.Empty), Strings.Get("DiagBundle_OptionPluginHint"))
        };
        Size = Strings.Format("DiagBundle_OptionSize", option.Files.Count, TransferItemViewModel.FormatBytes(option.Bytes));
    }

    /// <summary>这一类日志。</summary>
    public DiagnosticLogOption Option { get; }

    /// <summary>名称。</summary>
    public string Label { get; }

    /// <summary>里面有什么(主要是会带上哪些路径与名字),写在名称下面。</summary>
    public string Hint { get; }

    /// <summary>几份、多大。</summary>
    public string Size { get; }

    /// <summary>勾上了没有;默认不勾。</summary>
    public bool IsChecked
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}

/// <summary>
/// 导出诊断包之前的勾选框(关于页):默认只收诊断日志与崩溃记录,传输记录、按天的外部编辑日志、<c>vcxsrv-yyyyMMdd.log</c>
/// 与各插件的日志列在这里、<b>默认一个都不勾</b>。
/// </summary>
/// <remarks>
/// 那几类日志里满是本机与远端的路径、文件名,通用的遮盖规则遮不了文件名;报「传不上去」「插件不工作」时它们又正是最有用的那几份。
/// 所以不替用户决定:写明每一类带着什么,要不要由用户勾(维护者 2026-10-25 定)。勾上的照样过一遍遮盖。
/// 一类都不勾也能导出 —— 默认那两类总在。
/// </remarks>
public sealed class DiagnosticExportViewModel
{
    /// <summary>按眼下找得到的几类日志建对话框的视图模型。</summary>
    /// <param name="options">可选的几类日志(<see cref="DiagnosticBundle.FindOptionalLogs" />)。</param>
    public DiagnosticExportViewModel(IReadOnlyList<DiagnosticLogOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Items = [.. options.Select(option => new DiagnosticExportItem(option))];
    }

    /// <summary>可选项(按界面顺序)。</summary>
    public IReadOnlyList<DiagnosticExportItem> Items { get; }

    /// <summary>有可选项;没有时列表换成一句说明。</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>勾上了的那几类(按界面顺序)。</summary>
    public IReadOnlyList<DiagnosticLogOption> SelectedOptions => [.. Items.Where(item => item.IsChecked).Select(item => item.Option)];
}
