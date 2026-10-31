namespace CloseMeansExit.Core;

/// <summary>Windows absolute-path matching, also deterministic in non-Windows tests.</summary>
public static class RulePaths
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string value = path.Trim().Replace('/', '\\');

        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            value = @"\\" + value[8..];
        }
        else if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        if (value.IndexOfAny(['\0', '<', '>', '"', '|', '?', '*']) >= 0 ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException("必须使用有效的 Windows 完整路径。", nameof(path));
        }

        string root;
        string rest;

        if (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\')
        {
            root = char.ToUpperInvariant(value[0]) + @":\";
            rest = value[3..];
        }
        else if (value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string[] components = value[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);

            if (components.Length < 2 || components[0] is "." or ".." ||
                components[1] is "." or "..")
            {
                throw new ArgumentException("UNC 路径必须包含服务器和共享名称。", nameof(path));
            }

            root = @"\\" + components[0] + "\\" + components[1];
            rest = string.Join('\\', components.Skip(2));
        }
        else
        {
            throw new ArgumentException("黑名单需要 Windows 完整路径，不能使用相对路径。", nameof(path));
        }

        if (rest.Contains(':') || root.Count(c => c == ':') > 1)
        {
            throw new ArgumentException("不支持设备路径或备用数据流。", nameof(path));
        }

        List<string> segments = [];

        foreach (string segment in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new ArgumentException("路径不能越过其根目录。", nameof(path));
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                throw new ArgumentException("不支持以空格或句点结尾的路径段。", nameof(path));
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? root : root.TrimEnd('\\') + "\\" + string.Join('\\', segments);
    }

    public static bool Equal(string first, string second)
    {
        try
        {
            return string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsProtectedPath(string executablePath, string windowsDirectory, string ownExecutablePath)
    {
        try
        {
            string target = Normalize(executablePath);
            string windows = Normalize(windowsDirectory).TrimEnd('\\');
            string own = Normalize(ownExecutablePath);

            return string.Equals(target, own, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, windows, StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
