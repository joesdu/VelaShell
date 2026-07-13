using System.Collections.ObjectModel;
using System.Reactive;
using System.Text;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Presentation.Services;

namespace VelaShell.ViewModels;

/// <summary>诊断步骤行(设计 RGXg1 stepPanel):序号 + 名称 + 状态图形/耗时。</summary>
public sealed class DiagnosticStepItemViewModel(int index, string name) : ReactiveObject
{
    private int Index { get; } = index;

    private string Name { get; set; } = name;

    public string DisplayName => $"{Index + 1}. {Name}";

    public DiagnosticStepStatus Status
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = DiagnosticStepStatus.Pending;

    public string? Detail
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public long? ElapsedMs
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>状态列文本:✅ 4ms / ⚠ 原因 / ✗ 原因 / ⏸ 等待修复后重试(设计 RGXg1)。</summary>
    public string StatusText => Status switch
    {
        DiagnosticStepStatus.Running => "… 检测中",
        DiagnosticStepStatus.Success => ElapsedMs is { } ms ? $"✓  {ms}ms" : "✓",
        DiagnosticStepStatus.Warning => "⚠",
        DiagnosticStepStatus.Failed => "✗",
        DiagnosticStepStatus.Skipped => "⏸",
        _ => "—"
    };

    public bool IsSuccess => Status == DiagnosticStepStatus.Success;

    public bool IsWarning => Status == DiagnosticStepStatus.Warning;

    public bool IsFailed => Status == DiagnosticStepStatus.Failed;

    public bool IsMuted => Status is DiagnosticStepStatus.Pending or DiagnosticStepStatus.Skipped;

    public bool IsRunning => Status == DiagnosticStepStatus.Running;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public void Apply(DiagnosticStepUpdate update)
    {
        Name = update.Name;
        Status = update.Status;
        Detail = update.Detail;
        ElapsedMs = update.ElapsedMs;
        this.RaisePropertyChanged(nameof(DisplayName));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(IsSuccess));
        this.RaisePropertyChanged(nameof(IsWarning));
        this.RaisePropertyChanged(nameof(IsFailed));
        this.RaisePropertyChanged(nameof(IsMuted));
        this.RaisePropertyChanged(nameof(IsRunning));
        this.RaisePropertyChanged(nameof(HasDetail));
    }

    public void Reset() => Apply(new(Index, Name, DiagnosticStepStatus.Pending));
}

/// <summary>连接诊断中心(设计 RGXg1):对一条连接配置逐步检测并给出问题与修复建议。</summary>
public class ConnectionDiagnosticsViewModel : ReactiveObject
{
    private readonly IConnectionDiagnosticsService _diagnosticsService;
    private readonly SessionProfile _profile;
    private DiagnosticReport? _lastReport;

    public ConnectionDiagnosticsViewModel(SessionProfile profile, IConnectionDiagnosticsService diagnosticsService)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _diagnosticsService = diagnosticsService ?? throw new ArgumentNullException(nameof(diagnosticsService));
        Steps =
        [
            new(0, "DNS 解析"),
            new(1, "TCP 建链"),
            new(2, "SSH 握手"),
            new(3, "用户认证")
        ];
        Suggestions = [];
        IObservable<bool> canRun = this.WhenAnyValue(x => x.IsBusy, busy => !busy);
        RunCommand = ReactiveCommand.CreateFromTask(RunAsync, canRun);
    }

    /// <summary>标题栏副标题里的目标描述。</summary>
    public string TargetSummary => $"// 逐步分析 DNS、握手、认证与通道建立 — {(_profile.Name is { Length: > 0 } n ? n : _profile.Host)} ({_profile.Username}@{_profile.Host}:{_profile.Port})";

    public ObservableCollection<DiagnosticStepItemViewModel> Steps { get; }

    public ObservableCollection<string> Suggestions { get; }

    public ReactiveCommand<Unit, Unit> RunCommand { get; }

    public bool IsBusy
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? IssueTitle
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasIssue));
        }
    }

    public string? IssueDescription
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool HasIssue => !string.IsNullOrEmpty(IssueTitle);

    /// <summary>四步全部通过时问题面板显示绿色的"未发现问题"。</summary>
    public bool AllPassed
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool CanExport => _lastReport is not null;

    /// <summary>导出文件名建议。</summary>
    public string SuggestedReportFileName => $"诊断报告-{(_profile.Name is { Length: > 0 } n ? n : _profile.Host)}-{DateTime.Now:yyyyMMdd-HHmmss}.txt";

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        IssueTitle = null;
        IssueDescription = null;
        AllPassed = false;
        Suggestions.Clear();
        foreach (DiagnosticStepItemViewModel step in Steps)
        {
            step.Reset();
        }
        try
        {
            var progress = new Progress<DiagnosticStepUpdate>(update =>
            {
                // Progress<T> 回调已被封送回创建线程(UI);保险起见仍走 Dispatcher。
                if (Dispatcher.UIThread.CheckAccess())
                {
                    Steps[update.Index].Apply(update);
                }
                else
                {
                    Dispatcher.UIThread.Post(() => Steps[update.Index].Apply(update));
                }
            });
            DiagnosticReport report = await _diagnosticsService.DiagnoseAsync(_profile, progress, cancellationToken);
            _lastReport = report;
            IssueTitle = report.IssueTitle;
            IssueDescription = report.IssueDescription;
            foreach (string suggestion in report.Suggestions)
            {
                Suggestions.Add(suggestion);
            }
            AllPassed = report.Success && Suggestions.Count == 0;
            this.RaisePropertyChanged(nameof(CanExport));
        }
        catch (Exception ex)
        {
            IssueTitle = "诊断执行出错";
            IssueDescription = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>生成可保存的纯文本诊断报告(导出诊断报告按钮)。</summary>
    public string BuildReportText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("VelaShell 连接诊断报告");
        builder.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"目标: {_profile.Name} ({_profile.Username}@{_profile.Host}:{_profile.Port})");
        if (_profile.JumpHostProfileId is not null)
        {
            builder.AppendLine("链路: 经由跳板连接");
        }
        builder.AppendLine(new('-', 48));
        foreach (DiagnosticStepItemViewModel step in Steps)
        {
            string status = step.Status switch
            {
                DiagnosticStepStatus.Success => "通过",
                DiagnosticStepStatus.Warning => "警告",
                DiagnosticStepStatus.Failed => "失败",
                DiagnosticStepStatus.Skipped => "跳过",
                _ => "未执行"
            };
            builder.Append($"{step.DisplayName,-24} [{status}]");
            if (step.ElapsedMs is { } ms)
            {
                builder.Append($" {ms}ms");
            }
            builder.AppendLine();
            if (!string.IsNullOrEmpty(step.Detail))
            {
                builder.AppendLine($"    {step.Detail}");
            }
        }
        builder.AppendLine(new('-', 48));
        if (HasIssue)
        {
            builder.AppendLine($"发现问题: {IssueTitle}");
            if (!string.IsNullOrEmpty(IssueDescription))
            {
                builder.AppendLine(IssueDescription);
            }
        }
        else
        {
            builder.AppendLine("未发现问题,各项检测均通过。");
        }
        if (Suggestions.Count > 0)
        {
            builder.AppendLine();
            for (int i = 0; i < Suggestions.Count; i++)
            {
                builder.AppendLine($"建议 {i + 1}: {Suggestions[i]}");
            }
        }
        return builder.ToString();
    }
}
