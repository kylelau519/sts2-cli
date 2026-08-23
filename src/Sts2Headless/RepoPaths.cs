namespace Sts2Headless;

/// <summary>
/// Locates repo-relative data directories (e.g. localization_eng/) from the executing
/// assembly's own location rather than AppContext.BaseDirectory -- under `dotnet exec`
/// BaseDirectory is the DLL's own folder, but under a custom CoreCLR host (pythonnet's
/// clr_loader, used by gym_example.py) it doesn't reliably point there, which turned the old
/// fixed-depth "../../../../.." walk into a path that resolved to the filesystem root.
/// </summary>
internal static class RepoPaths
{
    public static string FindContaining(string markerDir)
    {
        var dir = Path.GetDirectoryName(typeof(RepoPaths).Assembly.Location);
        for (var depth = 0; depth < 16 && !string.IsNullOrEmpty(dir); depth++)
        {
            var candidate = Path.Combine(dir, markerDir);
            if (Directory.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }

        // Assembly.Location can be empty for a single-file publish; fall back to the old guess.
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", markerDir));
    }
}
