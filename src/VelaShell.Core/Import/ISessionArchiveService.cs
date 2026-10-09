namespace VelaShell.Core.Import;

/// <summary>导出前给用户看的统计。</summary>
/// <param name="SessionCount">实际要写的连接数(含自动带上的跳板)。</param>
/// <param name="RequestedCount">用户直接选的连接数。</param>
/// <param name="DependencyCount">因被当作跳板引用而自动带上的连接数。</param>
/// <param name="GroupCount">涉及的分组数。</param>
/// <param name="SecretCount">带着机密(密码、私钥口令、插件机密)的连接与共享凭据数。</param>
public sealed record SessionExportPreview(int SessionCount, int RequestedCount, int DependencyCount, int GroupCount, int SecretCount);

/// <summary>生成好的导出文件。</summary>
/// <param name="Content">文件字节。</param>
/// <param name="SessionCount">写进去的连接数。</param>
/// <param name="SecretsIncluded">是否带了(加密的)机密。</param>
public sealed record SessionExportFile(byte[] Content, int SessionCount, bool SecretsIncluded);

/// <summary>
/// 连接的导入与导出(#571):VelaShell JSON(完整、可带加密的机密)与 CSV(给 Excel 批量编辑)。
/// </summary>
/// <remarks>
/// 规则都在纯函数里(<see cref="SessionArchiveBuilder" /> / <see cref="SessionArchiveJson" /> / <see cref="SessionCsv" /> /
/// <see cref="SessionImportPlanner" />),这个服务只负责从仓储取数据、把算好的结果写回去。
/// </remarks>
public interface ISessionArchiveService
{
    /// <summary>定下导出范围(用户选的 + 它们的跳板)并统计,不生成文件。</summary>
    /// <param name="profileIds">用户选中的连接。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统计。</returns>
    Task<SessionExportPreview> PreviewExportAsync(IReadOnlyCollection<Guid> profileIds, CancellationToken cancellationToken = default);

    /// <summary>生成导出文件。</summary>
    /// <param name="profileIds">用户选中的连接(跳板会自动带上)。</param>
    /// <param name="format">格式;CSV 一律不带机密。</param>
    /// <param name="passphrase">导出口令;非空时机密用它加密进 JSON,null / 空 = 不导出机密。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件字节与统计。</returns>
    Task<SessionExportFile> ExportAsync(
        IReadOnlyCollection<Guid> profileIds,
        SessionFileFormat format,
        string? passphrase,
        CancellationToken cancellationToken = default);

    /// <summary>CSV 导入模板(带 BOM 的 UTF-8)。</summary>
    /// <returns>文件字节。</returns>
    byte[] CreateCsvTemplate();

    /// <summary>
    /// 解析一份连接文件:扩展名是 <c>.json</c> 按 JSON,<c>.csv</c> / <c>.tsv</c> / <c>.txt</c> 按 CSV,其余看内容是不是以 <c>{</c> 开头。
    /// 读不了时抛 <see cref="SessionFileFormatException" />(消息已本地化)。
    /// </summary>
    /// <param name="fileName">文件名。</param>
    /// <param name="content">文件字节。</param>
    /// <returns>解析结果。</returns>
    SessionImportDocument Parse(string fileName, byte[] content);

    /// <summary>与本机当前数据比对,得出导入预览。</summary>
    /// <param name="document">解析好的文件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导入计划。</returns>
    Task<SessionImportPlan> PlanAsync(SessionImportDocument document, CancellationToken cancellationToken = default);

    /// <summary>按用户的选择写入。</summary>
    /// <param name="plan">导入计划。</param>
    /// <param name="options">用户的选择。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结果统计。</returns>
    Task<SessionFileImportOutcome> ImportAsync(SessionImportPlan plan, SessionImportOptions options, CancellationToken cancellationToken = default);
}
