namespace CloudDrive.Core;

public sealed record ProviderCommand(string Verb, string? Source, string? Root, string? RelativePath)
{
    public static ProviderCommand Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            return new("help", null, null, null);

        string verb = args[0].ToLowerInvariant();
        if (verb == "doctor" && args.Length == 1)
            return new(verb, null, null, null);
        if (verb is not ("run" or "status" or "release" or "unregister"))
            throw new ArgumentException($"未知命令：{args[0]}");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index += 2)
        {
            string option = args[index];
            if (option is not ("--source" or "--root" or "--path") || index + 1 >= args.Length)
                throw new ArgumentException($"无效参数或缺少参数值：{option}");
            if (!values.TryAdd(option, args[index + 1]))
                throw new ArgumentException($"参数不能重复：{option}");
        }

        string root = RequireAbsolute(values, "--root");
        string? source = verb == "run" ? RequireAbsolute(values, "--source") : null;
        if (verb != "run" && values.ContainsKey("--source"))
            throw new ArgumentException("--source 仅用于 run。");
        string? relative = null;
        if (verb is "status" or "release")
        {
            if (!values.TryGetValue("--path", out relative) || string.IsNullOrWhiteSpace(relative))
                throw new ArgumentException("必须提供 --path（相对同步根的文件路径）。");
            relative = relative.Replace('/', '\\');
            if (Path.IsPathRooted(relative) || relative.Contains(':') ||
                relative.Any(char.IsControl) ||
                relative.Split('\\').Any(part => part is ".." or "." or "" || part.EndsWith('.') || part.EndsWith(' ')))
                throw new ArgumentException("--path 必须为不含 .、..、空段或数据流的相对路径。");
        }
        else if (values.ContainsKey("--path"))
            throw new ArgumentException("--path 仅用于 status 或 release。");

        if (source is not null && (IsWithin(root, source) || IsWithin(source, root)))
            throw new ArgumentException("模拟远端和同步根不能相同或互相包含。");
        return new(verb, source, root, relative);
    }

    public IEnumerable<string> NativeArguments()
    {
        yield return Verb;
        if (Source is not null) { yield return "--source"; yield return Source; }
        if (Root is not null) { yield return "--root"; yield return Root; }
        if (RelativePath is not null) { yield return "--path"; yield return RelativePath; }
    }

    private static string RequireAbsolute(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value) ||
            !Path.IsPathFullyQualified(value))
            throw new ArgumentException($"必须提供 {key}，且使用完整绝对路径。");
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (normalized.Equals(Path.GetPathRoot(normalized), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{key} 不能是磁盘根目录。");
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal) || normalized.IndexOf(':', 2) >= 0)
            throw new ArgumentException($"{key} 在本地模拟阶段必须为本地磁盘普通路径。");
        return normalized;
    }

    private static bool IsWithin(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
