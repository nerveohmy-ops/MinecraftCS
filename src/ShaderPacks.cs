using System.IO.Compression;
using System.Text.RegularExpressions;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    const int MaxShaderSource = 1024 * 1024;
    static readonly Regex shaderInclude = new("""^\s*#\s*include\s+"([^"]+)"\s*(?://.*)?$""");

    static string ResolveShaderpacksDirectory(string executableDirectory)
    {
        for (var dir = new DirectoryInfo(executableDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MinecraftCS.csproj")))
                return Path.Combine(dir.FullName, "shaderpacks");
        return Path.Combine(executableDirectory, "shaderpacks");
    }

    static string ReadShaderText(Stream stream)
    {
        using var reader = new StreamReader(stream);
        char[] buffer = new char[MaxShaderSource + 1];
        int length = reader.ReadBlock(buffer);
        if (length > MaxShaderSource) throw new InvalidDataException("Shader source is too large");
        return new string(buffer, 0, length);
    }

    static string NormalizeShaderPath(string root, string from, string include)
    {
        string path = include.StartsWith('/') ? root + "/" + include.TrimStart('/') :
            from[..(from.LastIndexOf('/') + 1)] + include;
        List<string> segments = [];
        foreach (string part in path.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (segments.Count == 0) throw new InvalidDataException("Include escapes the shader pack");
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(part);
        }
        string normalized = string.Join('/', segments);
        if (root.Length > 0 && !normalized.StartsWith(root + "/", StringComparison.Ordinal))
            throw new InvalidDataException("Include escapes the shader pack");
        return normalized;
    }

    static string ExpandShaderIncludes(string file, string root, Func<string, string> read)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        var output = new System.Text.StringBuilder();
        void Expand(string path)
        {
            if (active.Count >= 24 || !active.Add(path)) throw new InvalidDataException($"Recursive shader include: {path}");
            using var reader = new StringReader(read(path));
            while (reader.ReadLine() is string line)
            {
                var include = shaderInclude.Match(line);
                if (include.Success) Expand(NormalizeShaderPath(root, path, include.Groups[1].Value));
                else output.AppendLine(line);
                if (output.Length > MaxShaderSource) throw new InvalidDataException("Expanded shader source is too large");
            }
            active.Remove(path);
        }
        Expand(file);
        return output.ToString();
    }

    sealed record ShaderPackSource(string Vertex, string Fragment);

    static ShaderPackSource ReadShaderPack(string path)
    {
        if (Directory.Exists(path))
        {
            string[] files = Directory.GetFiles(path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 24
            });
            var entries = files.ToDictionary(file => Path.GetRelativePath(path, file).Replace('\\', '/'), StringComparer.Ordinal);
            return ReadShaderEntries(entries.Keys, file =>
            {
                if (!entries.TryGetValue(file, out var target)) throw new InvalidDataException($"Missing shader file: {file}");
                using var stream = File.OpenRead(target);
                return ReadShaderText(stream);
            });
        }

        using var archive = ZipFile.OpenRead(path);
        return ReadShaderEntries(archive.Entries.Select(entry => entry.FullName), file =>
        {
            var entry = archive.GetEntry(file) ?? throw new InvalidDataException($"Missing shader file: {file}");
            using var stream = entry.Open();
            return ReadShaderText(stream);
        });
    }

    static ShaderPackSource ReadShaderEntries(IEnumerable<string> paths, Func<string, string> read)
    {
        var files = paths.ToHashSet(StringComparer.Ordinal);
        string fragment = files.Where(IsPackFragment)
            .OrderBy(file => file.Length).ThenBy(file => file, StringComparer.Ordinal).FirstOrDefault();
        if (fragment != null)
        {
            string prefix = fragment[..^"fragment.glsl".Length];
            string root = prefix.TrimEnd('/');
            string vertex = prefix + "vertex.glsl";
            return new ShaderPackSource(files.Contains(vertex) ? ExpandShaderIncludes(vertex, root, read) : VertexShaderSrc,
                ExpandShaderIncludes(fragment, root, read));
        }

        throw new InvalidDataException("Shader pack must contain fragment.glsl");
    }

    static bool IsPackFragment(string path) =>
        path == "fragment.glsl" || path.EndsWith("/fragment.glsl", StringComparison.Ordinal);

    static bool IsMinecraftCsShaderArchive(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Any(entry => IsPackFragment(entry.FullName));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[ShaderManager] Could not read {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }
}
