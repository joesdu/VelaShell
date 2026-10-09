using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Presentation.ViewModels;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>
/// 资源管理器发起的批量操作与导入导出(#571):弹哪个对话框、做完刷新什么、怎么告诉用户。
/// </summary>
/// <remarks>
/// 从 <see cref="MainWindow" /> 拆出来单放,是因为这几条流程彼此独立又都要窗口(弹框、选文件),
/// 堆进主窗口那个两千行的代码后台里只会更难找。会话树只发事件,不认识任何窗口。
/// </remarks>
/// <param name="owner">对话框的属主窗口。</param>
/// <param name="viewModel">主窗口视图模型(打开连接、刷新树、浮层提示)。</param>
/// <param name="services">取导入导出服务与仓储。</param>
internal sealed class SessionTreeActions(Window owner, MainWindowViewModel viewModel, IServiceProvider services)
{
    /// <summary>一次打开超过这么多条连接时先确认:整组几十台一下子全开,多半不是手滑之外的本意。</summary>
    public const int OpenManyConfirmThreshold = 10;

    /// <summary>
    /// 导入文件的大小上限。两百台设备的 CSV 也就几十 KB;超过这个数说明选错了文件(日志、数据库导出),
    /// 整份读进内存再解析纯属白费。
    /// </summary>
    private const long MaxImportBytes = 16L * 1024 * 1024;

    /// <summary>导入完成后的说明里最多列几条提示,再多就只说还有几条。</summary>
    private const int MaxListedWarnings = 15;

    /// <summary>把会话树的各个请求接到对应的流程上。</summary>
    /// <param name="tree">资源管理器的视图模型。</param>
    public void Attach(SessionTreeViewModel tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        tree.OpenManyRequested += profiles => FireAndForget.Run(() => OpenManyAsync(profiles));
        tree.BatchEditRequested += profiles => FireAndForget.Run(() => BatchEditAsync(profiles));
        tree.ExportRequested += request => FireAndForget.Run(() => ExportAsync(request));
        tree.ImportFileRequested += target => FireAndForget.Run(() => ImportFileAsync(target));
        tree.CsvTemplateRequested += () => FireAndForget.Run(SaveCsvTemplateAsync);
        // 批量删除不可撤销,必须先确认(红色危险按钮,默认动作是取消)。
        tree.ConfirmDeleteSessions = message => MessageDialog.ConfirmAsync(owner,
            Strings.Get("Tree_DeleteSelectedTitle"),
            message,
            Strings.Delete,
            Strings.Cancel,
            MessageDialogKind.Warning,
            danger: true);
    }

    /// <summary>整组打开 / 多选打开:条数多时先确认,再按限定的并发度依次连。</summary>
    internal async Task OpenManyAsync(IReadOnlyList<SessionProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            return;
        }
        if (profiles.Count > OpenManyConfirmThreshold
            && !await MessageDialog.ConfirmAsync(owner,
                Strings.Get("Tree_OpenManyTitle"),
                Strings.Format("Tree_OpenManyConfirm", profiles.Count),
                Strings.Get("Tree_OpenManyAction"),
                Strings.Cancel))
        {
            return;
        }
        await viewModel.OpenProfilesAsync(profiles);
    }

    private async Task BatchEditAsync(IReadOnlyList<SessionProfile> profiles)
    {
        if (services.GetService<ISessionRepository>() is not { } repository)
        {
            return;
        }
        // 从仓储重新取一份:树里缓存的那些对象正被别处用着(活动连接、命令面板),对话框只碰副本。
        List<SessionProfile> all = await repository.GetAllSessionsAsync();
        List<SharedCredential> credentials = services.GetService<ISharedCredentialRepository>() is { } shared
            ? await shared.GetAllAsync()
            : [];
        var ids = profiles.Select(static p => p.Id).ToHashSet();
        List<SessionProfile> targets = [.. all.Where(p => ids.Contains(p.Id))];
        if (targets.Count == 0)
        {
            return;
        }
        var editor = new SessionBatchEditViewModel(targets, all, credentials, repository);
        var dialog = new SessionBatchEditView { DataContext = editor };
        SessionBatchEditOutcome? outcome = await dialog.ShowDialog<SessionBatchEditOutcome?>(owner);
        // 保存到一半失败、用户再点「取消」时 outcome 是 null,但库里已经改了几条:照样重读,
        // 否则树里缓存的旧对象会在下一次置顶 / 移动分组时把旧值整条写回去。
        if (outcome is not null || editor.HasWritten)
        {
            await viewModel.RefreshSessionTreeAsync();
        }
        if (outcome is null)
        {
            return;
        }
        List<string> parts = [Strings.Format("BatchEdit_DoneFmt", outcome.Changed)];
        if (outcome.AuthSkipped > 0)
        {
            parts.Add(Strings.Format("BatchEdit_PreviewAuthSkippedFmt", outcome.AuthSkipped));
        }
        if (outcome.JumpHostSkipped > 0)
        {
            parts.Add(Strings.Format("BatchEdit_PreviewJumpSkippedFmt", outcome.JumpHostSkipped));
        }
        string message = string.Join(Strings.Get("BatchEdit_PreviewSeparator"), parts);
        if (outcome.AuthSkipped > 0 || outcome.JumpHostSkipped > 0)
        {
            viewModel.Toasts.Warning(message);
        }
        else
        {
            viewModel.Toasts.Info(message);
        }
    }

    private async Task ExportAsync(SessionExportRequest request)
    {
        if (request.ProfileIds.Count == 0 || services.GetService<ISessionArchiveService>() is not { } archive)
        {
            return;
        }
        var dialog = new SessionExportView { DataContext = new SessionExportViewModel(archive, request) };
        if (await dialog.ShowDialog<SessionExportResult?>(owner) is not { } result)
        {
            return;
        }
        string file = Path.GetFileName(result.Path);
        viewModel.Toasts.Info(result.Format == SessionFileFormat.Csv
            ? Strings.Format("SessExport_DoneCsv", result.SessionCount, file)
            : result.SecretsIncluded
                ? Strings.Format("SessExport_DoneWithSecrets", result.SessionCount, file)
                : Strings.Format("SessExport_Done", result.SessionCount, file));
    }

    private async Task ImportFileAsync(Guid? targetGroupId)
    {
        if (services.GetService<ISessionArchiveService>() is not { } archive)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("SessImport_PickTitle"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.DownloadsAsync(owner),
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.Get("SessFile_AnyType")) { Patterns = ["*.json", "*.csv", "*.tsv", "*.txt"] },
                new FilePickerFileType(Strings.Get("SessFile_JsonType")) { Patterns = ["*.json"] },
                new FilePickerFileType(Strings.Get("SessFile_CsvType")) { Patterns = ["*.csv", "*.tsv", "*.txt"] }
            ]
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { Length: > 0 } path)
        {
            return; // 用户取消了。
        }

        SessionImportDocument document;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxImportBytes)
            {
                await ShowImportProblemAsync(Strings.Format("SessImport_TooLarge",
                    (info.Length / (1024.0 * 1024)).ToString("F1"), MaxImportBytes / (1024 * 1024)));
                return;
            }
            document = archive.Parse(path, await File.ReadAllBytesAsync(path));
        }
        catch (SessionFileFormatException ex)
        {
            await ShowImportProblemAsync(ex.Message);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 选完之后文件被删 / 被占用(网络盘、同步目录上并不罕见),或是一份解析器没料到的怪文件:
            // 一律告诉用户读不了。漏到 FireAndForget 里只会记一行日志,用户看到的是「点了导入什么都没发生」。
            await ShowImportProblemAsync(Strings.Format("SessImport_ReadFailed", ex.Message));
            return;
        }
        if (document.Candidates.Count == 0)
        {
            await ShowImportProblemAsync(Strings.Get("SessImport_Empty"));
            return;
        }

        var importer = new SessionFileImportViewModel(archive, document, targetGroupId);
        var dialog = new SessionFileImportView { DataContext = importer };
        SessionFileImportOutcome? outcome = await dialog.ShowDialog<SessionFileImportOutcome?>(owner);
        // 同批量修改:写到一半失败后关掉对话框,库里已经有了一部分,资源管理器也得重读。
        if (outcome is not null || importer.HasWritten)
        {
            await viewModel.RefreshSessionTreeAsync();
        }
        if (outcome is null)
        {
            return;
        }
        string summary = Strings.Format("SessImport_DoneFmt", outcome.Created, outcome.Updated, outcome.Skipped);
        if (outcome.Warnings.Count == 0)
        {
            viewModel.Toasts.Info(summary);
            return;
        }
        // 有妥协(跳板找不到改成直连之类)就弹框列清楚:浮层几秒就走,这些是要一条条去核对的。
        IEnumerable<string> listed = outcome.Warnings.Take(MaxListedWarnings);
        string more = outcome.Warnings.Count > MaxListedWarnings
            ? Environment.NewLine + Strings.Format("SessImport_MoreWarnings", outcome.Warnings.Count - MaxListedWarnings)
            : string.Empty;
        await MessageDialog.ShowMessageAsync(owner,
            Strings.Get("SessImport_DoneTitle"),
            summary + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, listed) + more,
            MessageDialogKind.Warning);
    }

    private async Task SaveCsvTemplateAsync()
    {
        if (services.GetService<ISessionArchiveService>() is not { } archive)
        {
            return;
        }
        IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = Strings.Get("Tree_SaveCsvTemplate"),
            SuggestedFileName = "velashell-connections-template.csv",
            SuggestedStartLocation = await StorageDefaults.DownloadsAsync(owner),
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType(Strings.Get("SessFile_CsvType")) { Patterns = ["*.csv"] }]
        });
        if (file?.TryGetLocalPath() is not { Length: > 0 } path)
        {
            return;
        }
        try
        {
            await File.WriteAllBytesAsync(path, archive.CreateCsvTemplate());
            viewModel.Toasts.Info(Strings.Format("SessExport_TemplateSaved", Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            viewModel.Toasts.Error(Strings.Format("SessExport_Failed", ex.Message));
        }
    }

    private Task ShowImportProblemAsync(string message) =>
        MessageDialog.ShowMessageAsync(owner, Strings.Get("SessImport_Title"), message, MessageDialogKind.Error);
}
