using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>
/// 本地 Agent 的目录授权边界。它提供插件内的路径约束和符号链接检查，
/// 不等同于操作系统级沙箱。
/// </summary>
public sealed class LocalPathScope(IReadOnlyList<LocalWorkspace> workspaces)
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>当前配置中的本地工作区。</summary>
    public IReadOnlyList<LocalWorkspace> Workspaces { get; } = workspaces ?? [];

    /// <summary>至少有一个根目录存在且格式有效。</summary>
    public bool HasUsableWorkspace => Workspaces.Any(static workspace => IsConfigured(workspace));

    /// <summary>解析并校验一个工作区路径。</summary>
    public bool TryResolve(string? workspaceId, string? path, bool write, out LocalPath pathInfo, out string error)
    {
        _ = write;
        pathInfo = default;
        error = "";
        LocalWorkspace? workspace = FindWorkspace(workspaceId);
        if (workspace is null)
        {
            error = workspaceId is null
                ? "No local workspace is configured."
                : "The requested local workspace is not available.";
            return false;
        }
        if (!IsConfigured(workspace))
        {
            error = "The local workspace has an invalid or missing root directory.";
            return false;
        }
        string rawPath = path?.Trim() ?? "";
        if (rawPath.Contains('\0'))
        {
            error = "The local path contains an invalid NUL character.";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(
                Path.IsPathFullyQualified(rawPath)
                    ? rawPath
                    : Path.Combine(workspace.RootPath, rawPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"The local path is invalid: {ex.Message}";
            return false;
        }

        string root = Path.GetFullPath(workspace.RootPath);
        if (!IsWithin(root, fullPath))
        {
            error = "The local path is outside the selected workspace.";
            return false;
        }

        string checkPath = File.Exists(fullPath) || Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath) ?? fullPath;
        if (!IsSafePath(root, checkPath))
        {
            error = "The local path contains a symbolic link or reparse point. "
                    + "Use a path inside the configured workspace without links.";
            return false;
        }

        pathInfo = new LocalPath(workspace, fullPath);
        return true;
    }

    /// <summary>按 id 查找工作区；未指定时返回第一个有效工作区。</summary>
    public LocalWorkspace? FindWorkspace(string? workspaceId)
    {
        if (workspaceId is null)
        {
            return Workspaces.FirstOrDefault(IsConfigured);
        }
        return Workspaces.FirstOrDefault(workspace =>
            string.Equals(workspace.Id, workspaceId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>判断工作区根目录是否为绝对且已存在的目录。</summary>
    public static bool IsConfigured(LocalWorkspace workspace)
        => !string.IsNullOrWhiteSpace(workspace.RootPath)
            && Path.IsPathFullyQualified(workspace.RootPath)
            && Directory.Exists(workspace.RootPath);

    private static bool IsWithin(string root, string path)
    {
        if (string.Equals(root, path, PathComparison))
        {
            return true;
        }

        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static bool IsSafePath(string root, string path)
    {
        string current = Path.GetFullPath(path);
        while (IsWithin(root, current))
        {
            if (HasReparsePoint(current))
            {
                return false;
            }
            if (string.Equals(root, current, PathComparison))
            {
                return true;
            }
            string? parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, PathComparison))
            {
                return false;
            }
            current = parent;
        }
        return false;
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}

/// <summary>经 <see cref="LocalPathScope" /> 校验后的工作区路径。</summary>
public readonly record struct LocalPath(LocalWorkspace Workspace, string FullPath)
{
    /// <summary>相对于工作区根目录的路径。</summary>
    public string RelativePath
        => Path.GetRelativePath(Workspace.RootPath, FullPath);
}
