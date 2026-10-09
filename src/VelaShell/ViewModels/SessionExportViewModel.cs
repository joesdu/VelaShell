using System.Diagnostics.CodeAnalysis;
using System.Security;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Import;
using VelaShell.Core.Resources;
using VelaShell.Presentation.ViewModels;
using VelaShell.Security;

namespace VelaShell.ViewModels;

/// <summary>导出完成后交回宿主的结果(用于浮层提示)。</summary>
/// <param name="Path">写到了哪个文件。</param>
/// <param name="SessionCount">写了几个连接。</param>
/// <param name="SecretsIncluded">是否带了(加密的)敏感信息。</param>
/// <param name="Format">文件格式。</param>
public sealed record SessionExportResult(string Path, int SessionCount, bool SecretsIncluded, SessionFileFormat Format);

/// <summary>
/// 导出连接对话框(#571):选格式(JSON / CSV),JSON 可以勾上「包含敏感信息」并设导出口令。
/// </summary>
/// <remarks>
/// <para>
/// 敏感信息默认不导出,勾上也<b>必须</b>设口令:导出的文件常被随手发给同事、丢进网盘,
/// 明文密码写进去就再也收不回来了。没有「明文导出密码」这个选项,是有意的。
/// </para>
/// <para>
/// CSV 永远不带敏感信息 —— 它是拿去 Excel 里编辑的,本身就没法加密。
/// </para>
/// </remarks>
public sealed class SessionExportViewModel : ReactiveObject
{
    /// <summary>导出口令的最短长度:文件可能落到任何人手里,太短的口令一猜就开。</summary>
    public const int MinPassphraseLength = 8;

    private readonly ISessionArchiveService _service;
    private readonly SessionExportRequest _request;
    private readonly TimeProvider _time;

    /// <summary>构造。</summary>
    /// <param name="service">导入导出服务。</param>
    /// <param name="request">要导出哪些连接。</param>
    /// <param name="timeProvider">时钟(建议文件名里的日期);null 用系统时钟。</param>
    public SessionExportViewModel(ISessionArchiveService service, SessionExportRequest request, TimeProvider? timeProvider = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _time = timeProvider ?? TimeProvider.System;
        SelectFormatCommand = ReactiveCommand.Create<SessionFileFormat>(format => Format = format);
        CancelCommand = ReactiveCommand.Create(() => { });
    }

    /// <summary>对话框标题。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string Title => Strings.Get("SessExport_Title");

    /// <summary>导出统计;加载完之前为 null。</summary>
    public SessionExportPreview? Preview
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseStateChanged();
        }
    }

    /// <summary>选的格式。</summary>
    public SessionFileFormat Format
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseStateChanged();
        }
    } = SessionFileFormat.Json;

    /// <summary>是否选了 JSON。</summary>
    public bool IsJson => Format == SessionFileFormat.Json;

    /// <summary>是否选了 CSV。</summary>
    public bool IsCsv => Format == SessionFileFormat.Csv;

    /// <summary>是否包含敏感信息(只对 JSON 有效)。</summary>
    public bool IncludeSecrets
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseStateChanged();
        }
    }

    /// <summary>导出口令。</summary>
    public SecureString? Passphrase
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseStateChanged();
        }
    }

    /// <summary>再输一遍导出口令。</summary>
    public SecureString? PassphraseConfirm
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseStateChanged();
        }
    }

    /// <summary>是否明文显示口令。</summary>
    public bool ShowPassphrase
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>口令输入区是否显示(JSON 且勾了敏感信息)。</summary>
    public bool ShowPassphraseFields => IsJson && IncludeSecrets;

    /// <summary>「包含敏感信息」能不能勾:这批连接里一个保存的密码都没有时勾了也没东西可导。</summary>
    public bool CanIncludeSecrets => Preview is { SecretCount: > 0 };

    /// <summary>口令有什么问题;没问题(或用不着口令)时为 null。</summary>
    public string? PassphraseError
    {
        get
        {
            if (!ShowPassphraseFields || !CanIncludeSecrets)
            {
                return null;
            }
            int length = Passphrase?.Length ?? 0;
            if (length < MinPassphraseLength)
            {
                return Strings.Format("SessExport_PassphraseTooShort", MinPassphraseLength);
            }
            return SecureStringConvert.ToPlaintext(Passphrase) == SecureStringConvert.ToPlaintext(PassphraseConfirm)
                ? null
                : Strings.Get("SessExport_PassphraseMismatch");
        }
    }

    /// <summary>概要:「将导出 N 个连接(M 个分组)」。</summary>
    public string Summary => Preview is not { } preview
        ? Strings.Get("SessExport_Loading")
        : Strings.Format("SessExport_SummaryFmt", preview.SessionCount, preview.GroupCount);

    /// <summary>自动带上的跳板机说明;没有时为空串。</summary>
    public string DependencyNote => Preview is { DependencyCount: > 0 } preview
        ? Strings.Format("SessExport_DependencyFmt", preview.DependencyCount)
        : string.Empty;

    /// <summary>是否显示跳板机说明。</summary>
    public bool HasDependencyNote => DependencyNote.Length > 0;

    /// <summary>「包含敏感信息」下面那一行说明。</summary>
    public string SecretsHint => Preview is { SecretCount: 0 }
        ? Strings.Get("SessExport_NoSecrets")
        : Strings.Format("SessExport_SecretsHintFmt", Preview?.SecretCount ?? 0);

    /// <summary>当前格式的一句说明。</summary>
    public string FormatHint => Strings.Get(IsJson ? "SessExport_JsonHint" : "SessExport_CsvHint");

    /// <summary>是否正在生成 / 写文件。</summary>
    public bool IsBusy
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(CanExport));
        }
    }

    /// <summary>写文件失败时的说明。</summary>
    public string? ErrorMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>导出按钮能不能点。</summary>
    public bool CanExport => !IsBusy && Preview is { SessionCount: > 0 } && PassphraseError is null;

    /// <summary>建议的文件名:velashell-&lt;范围&gt;-&lt;日期&gt;.json / .csv。</summary>
    public string SuggestedFileName
    {
        get
        {
            string scope = _request.GroupName is { Length: > 0 } group ? Sanitize(group) : "connections";
            return $"velashell-{scope}-{_time.GetLocalNow():yyyyMMdd}.{Extension}";
        }
    }

    /// <summary>当前格式的扩展名(不带点)。</summary>
    public string Extension => IsJson ? "json" : "csv";

    /// <summary>切换格式(分段按钮)。</summary>
    public ReactiveCommand<SessionFileFormat, RxVoid> SelectFormatCommand { get; }

    /// <summary>取消。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    /// <summary>对话框打开时调用:统计导出范围。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            Preview = await _service.PreviewExportAsync(_request.ProfileIds).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            ErrorMessage = Strings.Format("SessExport_Failed", ex.Message);
        }
    }

    /// <summary>生成文件并写到 <paramref name="path" />;失败时返回 null 并把原因放进 <see cref="ErrorMessage" />。</summary>
    /// <param name="path">用户在保存对话框里选的路径。</param>
    /// <returns>结果;失败为 null。</returns>
    public async Task<SessionExportResult?> ExportToAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!CanExport)
        {
            return null;
        }
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            string? passphrase = IsJson && IncludeSecrets && CanIncludeSecrets ? SecureStringConvert.ToPlaintext(Passphrase) : null;
            SessionExportFile file = await _service.ExportAsync(_request.ProfileIds, Format, passphrase).ConfigureAwait(true);
            await File.WriteAllBytesAsync(path, file.Content).ConfigureAwait(true);
            return new SessionExportResult(path, file.SessionCount, file.SecretsIncluded, Format);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 只读目录、盘满、文件被占用 —— 留在对话框里说清楚,用户换个地方再点一次即可。
            ErrorMessage = Strings.Format("SessExport_Failed", ex.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>文件名里不能有的字符换成连字符(分组名可以是任何东西)。</summary>
    private static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new([.. name.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c)]);
        return cleaned.Length == 0 ? "connections" : cleaned;
    }

    private void RaiseStateChanged()
    {
        this.RaisePropertyChanged(nameof(IsJson));
        this.RaisePropertyChanged(nameof(IsCsv));
        this.RaisePropertyChanged(nameof(ShowPassphraseFields));
        this.RaisePropertyChanged(nameof(CanIncludeSecrets));
        this.RaisePropertyChanged(nameof(PassphraseError));
        this.RaisePropertyChanged(nameof(Summary));
        this.RaisePropertyChanged(nameof(DependencyNote));
        this.RaisePropertyChanged(nameof(HasDependencyNote));
        this.RaisePropertyChanged(nameof(SecretsHint));
        this.RaisePropertyChanged(nameof(FormatHint));
        this.RaisePropertyChanged(nameof(CanExport));
        this.RaisePropertyChanged(nameof(SuggestedFileName));
        this.RaisePropertyChanged(nameof(Extension));
    }
}
