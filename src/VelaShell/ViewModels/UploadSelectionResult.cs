namespace VelaShell.ViewModels;

/// <summary>一次已提交的上传选择;目标在提交时固定,不随后台面板导航变化。</summary>
/// <param name="LocalPaths">用户勾选的本地文件和目录。</param>
/// <param name="RemotePath">选中映射的完整远端目标。</param>
/// <param name="MappingId">本机映射标识;临时连接为空。</param>
public sealed record UploadSelectionResult(IReadOnlyList<string> LocalPaths, string RemotePath, Guid? MappingId);
