using System.IO.Compression;
using MinecraftCS.Graphics;
using System.Text.RegularExpressions;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    const int MaxShaderSource = 1024 * 1024;
    static readonly Regex shaderInclude = new("""^\s*#\s*include\s+"([^"]+)"\s*(?://.*)?$""");
    static readonly Regex javaVersion = new(@"(?m)^\s*#\s*version\s+(\d+)(?:\s+(?:core|compatibility))?[^\S\r\n]*$");
    static readonly Regex fragmentTarget = new(@"\bgl_FragData\s*\[\s*0\s*\]");

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
            throw new InvalidDataException("Include escapes the shaders directory");
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

    sealed record ShaderPackSource(string Vertex, string Fragment, bool Java, RenderShaderSource[] Post = null);

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
        string native = files.Where(file => file == "fragment.glsl" || file.EndsWith("/fragment.glsl", StringComparison.Ordinal))
            .OrderBy(file => file.Length).ThenBy(file => file, StringComparer.Ordinal).FirstOrDefault();
        if (native != null)
        {
            string prefix = native[..^"fragment.glsl".Length];
            string root = prefix.TrimEnd('/');
            string vertex = prefix + "vertex.glsl";
            return new ShaderPackSource(files.Contains(vertex) ? ExpandShaderIncludes(vertex, root, read) : VertexShaderSrc,
                ExpandShaderIncludes(native, root, read), false);
        }

        const string terrainName = "gbuffers_terrain.fsh";
        const string overworld = "shaders/world0/" + terrainName;
        const string common = "shaders/" + terrainName;
        static bool MatchesPath(string file, string path) =>
            file == path || file.EndsWith("/" + path, StringComparison.Ordinal);
        string fragment = files.Where(file => MatchesPath(file, overworld) || MatchesPath(file, common))
            .OrderByDescending(file => MatchesPath(file, overworld))
            .ThenBy(file => file.Length).ThenBy(file => file, StringComparer.Ordinal).FirstOrDefault();
        if (fragment == null) throw new InvalidDataException("No terrain shader in shaders/ or shaders/world0/");

        bool dimension = MatchesPath(fragment, overworld);
        string programDir = fragment[..fragment.LastIndexOf('/')];
        string shaders = dimension ? programDir[..^"/world0".Length] : programDir;
        var programs = new Dictionary<string, string>(StringComparer.Ordinal);
        void AddPrograms(string directory)
        {
            string prefix = directory + "/";
            foreach (string file in files.Where(file => file.StartsWith(prefix, StringComparison.Ordinal)))
            {
                string name = file[prefix.Length..];
                if (!name.Contains('/')) programs[name] = file;
            }
        }
        AddPrograms(shaders);
        if (dimension) AddPrograms(programDir);
        if (!programs.TryGetValue("gbuffers_terrain.vsh", out string vertexPath))
            throw new InvalidDataException("Missing terrain vertex shader");

        var passes = programs.Keys.Where(file =>
            Regex.IsMatch(file, @"^(?:final|shadow\w*|deferred\d*|composite\d*|prepare\d*|begin\d*|setup\d*)\.(?:fsh|vsh|gsh|csh)$"))
            .ToArray();
        static int StageOrder(string file)
        {
            if (file == "final.fsh") return 200;
            var match = Regex.Match(file, @"^(deferred|composite)([1-9]\d?)?\.fsh$");
            if (!match.Success) return -1;
            int group = match.Groups[1].Value == "deferred" ? 0 : 100;
            return group + (match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0);
        }
        var screenPasses = passes.Select(file => (File: file, Order: StageOrder(file)))
            .Where(stage => stage.Order >= 0).OrderBy(stage => stage.Order).Select(stage => stage.File).ToArray();
        var supportedPassFiles = screenPasses.SelectMany(file => new[] { file, Path.ChangeExtension(file, ".vsh") })
            .ToHashSet(StringComparer.Ordinal);
        string[] unsupported = passes.Where(file => !supportedPassFiles.Contains(file))
            .Select(file => Path.GetFileNameWithoutExtension(file)).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (unsupported.Length > 0)
            throw new InvalidDataException($"Unsupported Iris passes: {string.Join(", ", unsupported.Take(3))}");

        var post = new RenderShaderSource[screenPasses.Length];
        for (int i = 0; i < post.Length; i++)
        {
            string stage = screenPasses[i];
            string vertexSource = programs.TryGetValue(Path.ChangeExtension(stage, ".vsh"), out var vertexFile)
                ? ExpandShaderIncludes(vertexFile, shaders, read) :
                "void main() { gl_Position = ftransform(); gl_TexCoord[0] = gl_MultiTexCoord0; }";
            post[i] = new RenderShaderSource(AdaptJavaShader(vertexSource, vertex: true, screen: true),
                AdaptJavaShader(ExpandShaderIncludes(programs[stage], shaders, read), vertex: false, screen: true));
        }

        if (programs.ContainsKey("gbuffers_terrain.gsh"))
            throw new InvalidDataException("Java geometry shader pass is unsupported");

        return new ShaderPackSource(AdaptJavaShader(ExpandShaderIncludes(vertexPath, shaders, read), vertex: true),
            AdaptJavaShader(ExpandShaderIncludes(fragment, shaders, read), vertex: false), true, post);
    }

    static string AdaptJavaShader(string source, bool vertex, bool screen = false)
    {
        source = source.ReplaceLineEndings("\n");
        var version = javaVersion.Match(source);
        if (version.Success && int.Parse(version.Groups[1].Value) > 330)
            throw new InvalidDataException("Java pack requires GLSL newer than 330");
        source = javaVersion.Replace(source, "");
        if (screen)
        {
            source = Regex.Replace(source, @"\buniform\s+sampler2D\s+(?:colortex0|gcolor)\s*;", "");
            source = Regex.Replace(source, @"\b(?:colortex0|gcolor)\b", "texture0");
            source = Regex.Replace(source, @"\buniform\s+sampler2D\s+(?:depthtex0|gdepthtex)\s*;", "");
            source = Regex.Replace(source, @"\b(?:depthtex0|gdepthtex)\b", "javaSceneDepth");
        }
        source = Regex.Replace(source, @"\buniform\s+sampler2D\s+(?:texture|gtexture|lightmap)\s*;", "");
        source = Regex.Replace(source, @"\b(?:texture|gtexture)\b(?!\s*\()", "texture0");
        source = Regex.Replace(source, @"\blightmap\b", "texture2");
        if (Regex.IsMatch(source, @"\b(?:uniform\s+[iu]?sampler\w*|shadowtex\w*|colortex\w*|depthtex\w*|gdepthtex)\b") ||
            Regex.IsMatch(source, @"(?:DRAWBUFFERS\s*:\s*0\s*[^\s*]|DRAWBUFFERS\s*:\s*[1-9]|RENDERTARGETS\s*:\s*(?:0\s*,|[1-9]))"))
            throw new InvalidDataException("Java pack needs additional render textures");
        source = fragmentTarget.Replace(source, "javaFragment");
        if (source.Contains("gl_FragData", StringComparison.Ordinal))
            throw new InvalidDataException("Java pack writes to multiple render targets");
        source = Regex.Replace(source, @"\bgl_TextureMatrix\s*\[\s*0\s*\]", "mat4(1.0)");
        source = Regex.Replace(source, @"\bgl_TextureMatrix\s*\[\s*1\s*\]", "mat4(0.00390625)");
        const string shared = """
            #version 330 core
            #define texture2D texture
            #define texture2DLod textureLod
            #define gl_TexCoord javaTexCoord
            uniform sampler2D texture0;
            uniform sampler2D texture2;
            uniform sampler2D javaSceneDepth;
            """;
        string stage = vertex ? """
            #define varying out
            #define attribute in
            in vec3 vertexPosition;
            in vec2 vertexTexCoord;
            in vec3 vertexNormal;
            in vec4 vertexColor;
            uniform mat4 mvp;
            uniform mat4 matView;
            uniform mat4 matProjection;
            uniform mat4 matNormal;
            out vec4 javaColor;
            out vec4 javaTexCoord[2];
            #define gl_Vertex vec4(vertexPosition, 1.0)
            #define gl_Normal vertexNormal
            #define gl_NormalMatrix mat3(matNormal)
            #define gl_MultiTexCoord0 vec4(vertexTexCoord, 0.0, 1.0)
            #define gl_MultiTexCoord1 vec4(240.0, 240.0, 0.0, 1.0)
            #define gl_Color vertexColor
            #define gl_FrontColor javaColor
            #define gl_ModelViewMatrix matView
            #define gl_ProjectionMatrix matProjection
            #define gl_ModelViewProjectionMatrix mvp
            #define ftransform() (mvp * gl_Vertex)
            #define main javaVertexMain
            """ : """
            #define varying in
            in vec4 javaColor;
            in vec4 javaTexCoord[2];
            out vec4 javaFragment;
            #define gl_Color javaColor
            #define gl_FragColor javaFragment
            """;
        string wrapper = vertex ? """

            #undef main
            void main() {
                javaColor = vertexColor;
                javaTexCoord[0] = gl_MultiTexCoord0;
                javaTexCoord[1] = vec4(1.0);
                javaVertexMain();
            }
            """ : "";
        return shared + "\n" + stage + "\n" + source + wrapper;
    }
}
