namespace VelaShell.ViewModels;

/// <summary>Validates names derived from a remote listing before local path construction.</summary>
public static class LocalPathSafety
{
    /// <summary>Resolves a remote-derived single filename beneath the current directory.</summary>
    public static bool TryResolveDestination(
        string currentDirectory,
        string remoteName,
        out string destination
    )
    {
        destination = string.Empty;
        if (string.IsNullOrWhiteSpace(currentDirectory) || !IsSafeLeafName(remoteName))
        {
            return false;
        }

        try
        {
            string current = Path.GetFullPath(currentDirectory);
            string candidate = Path.GetFullPath(Path.Combine(current, remoteName));
            string prefix = current.EndsWith(Path.DirectorySeparatorChar)
                ? current
                : current + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!candidate.StartsWith(prefix, comparison))
            {
                return false;
            }

            if (!HasNoReparsePointBelowRoot(current, candidate))
            {
                return false;
            }

            destination = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasNoReparsePointBelowRoot(string root, string candidate)
    {
        try
        {
            string relative = Path.GetRelativePath(root, candidate);
            string current = root;
            foreach (
                string component in relative.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries
                )
            )
            {
                current = Path.Combine(current, component);
                try
                {
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }
                }
                catch (FileNotFoundException)
                {
                    return true;
                }
                catch (DirectoryNotFoundException)
                {
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (ArgumentException)
                {
                    return false;
                }
                catch (NotSupportedException)
                {
                    return false;
                }
            }

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Returns a safe canonical destination, or null when the name is unsafe.</summary>
    public static string? ResolveDestination(string currentDirectory, string remoteName) =>
        TryResolveDestination(currentDirectory, remoteName, out string destination)
            ? destination
            : null;

    /// <summary>Returns whether a remote-derived value is a single safe leaf name.</summary>
    public static bool IsSafeLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return false;
        }

        if (Path.IsPathRooted(name) || name.Contains('/') || name.Contains('\\'))
        {
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        string deviceName = name.TrimEnd(' ', '.').Split('.')[0].ToUpperInvariant();
        return deviceName is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(deviceName.StartsWith("COM", StringComparison.Ordinal) && IsDeviceNumber(deviceName[3..]))
            && !(deviceName.StartsWith("LPT", StringComparison.Ordinal) && IsDeviceNumber(deviceName[3..]));
    }

    private static bool IsDeviceNumber(string suffix) =>
        suffix.Length == 1 && suffix[0] is >= '1' and <= '9';
}
