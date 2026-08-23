using System.Text;
using System.Text.RegularExpressions;

namespace Godot;

/// <summary>
/// Minimal reimplementation of the subset of Godot's built-in String extension methods that
/// sts2.dll actually calls (see MegaCrit.Sts2.Core.Saves for the bulk of the usage) -- restored
/// after this file was lost to an untracked-file wipe; not a full port of Godot's String API.
/// </summary>
public static class StringExtensions
{
    public static string PathJoin(this string basePath, string file)
    {
        if (string.IsNullOrEmpty(basePath)) return file;
        if (string.IsNullOrEmpty(file)) return basePath;
        return basePath.EndsWith("/") ? basePath + file : basePath + "/" + file;
    }

    public static string GetBaseDir(this string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[..slash] : "";
    }

    public static string GetFile(this string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    public static string GetExtension(this string path)
    {
        var file = path.GetFile();
        var dot = file.LastIndexOf('.');
        return dot >= 0 ? file[(dot + 1)..] : "";
    }

    public static string Capitalize(this string s)
    {
        var words = s.Replace("_", " ").Replace("-", " ")
            .Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        for (int i = 0; i < words.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            var w = words[i];
            sb.Append(char.ToUpperInvariant(w[0]));
            if (w.Length > 1) sb.Append(w[1..].ToLowerInvariant());
        }
        return sb.ToString();
    }

    public static string ToSnakeCase(this string s)
    {
        var withUnderscores = Regex.Replace(s, "(?<=[a-z0-9])(?=[A-Z])", "_");
        return withUnderscores.ToLowerInvariant();
    }
}
