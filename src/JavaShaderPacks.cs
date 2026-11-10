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

        // Автоматическая адаптация для пакетов Realis (OptiFine / Iris)
        if (IsRealisShaderPack(files, read))
        {
            return LoadAdaptedRealisPack();
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
            #define saturate(x) clamp(x, 0.0, 1.0)
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

    static bool IsRealisShaderPack(HashSet<string> files, Func<string, string> read)
    {
        if (files.Any(f => f.Contains("Realis", StringComparison.OrdinalIgnoreCase))) return true;
        if (files.Contains("shaders/program/gbuffers_solid.glsl")) return true;
        foreach (var f in files)
        {
            if (f.EndsWith("shaders.properties", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (read(f).Contains("Realis", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
        }
        return false;
    }

    static ShaderPackSource LoadAdaptedRealisPack()
    {
        string realisDir = Path.Combine(ShaderpacksDir, "Realis v1.0");
        string vertFile = Path.Combine(realisDir, "vertex.glsl");
        string fragFile = Path.Combine(realisDir, "fragment.glsl");
        string vert = File.Exists(vertFile) ? File.ReadAllText(vertFile) : RealisDefaultVertex;
        string frag = File.Exists(fragFile) ? File.ReadAllText(fragFile) : RealisDefaultFragment;
        return new ShaderPackSource(vert, frag, false);
    }

    const string RealisDefaultVertex = """
        #version 330
        in vec3 vertexPosition;
        in vec2 vertexTexCoord;
        in vec3 vertexNormal;
        in vec4 vertexColor;
        uniform mat4 mvp;
        uniform float gameTime;
        out float fragAO;
        out vec3 fragPos;
        out vec3 shadowPos;
        out vec2 fragUV;
        flat out vec3 fragNormal;
        out float isFoliage;

        void main()
        {
            vec3 p = vertexPosition;
            float tile = floor(vertexTexCoord.x * @TILE_COUNT@.0);
            float foliage = (tile == 7.0) ? 1.0 : 0.0;
            isFoliage = foliage;

            if (foliage > 0.5)
            {
                float t = gameTime * 2.2;
                float waveX = sin(t + p.x * 0.75 + p.z * 0.5) * 0.035;
                float waveZ = cos(t * 0.85 + p.y * 1.1 + p.x * 0.4) * 0.035;
                p.x += waveX;
                p.z += waveZ;
            }

            fragPos = p;
            shadowPos = vertexPosition;
            fragUV = vertexTexCoord;
            fragNormal = vertexNormal;
            fragAO = vertexColor.r;
            gl_Position = mvp * vec4(p, 1.0);
        }
        """;

    const string RealisDefaultFragment = """
        #version 330
        in float fragAO;
        in vec3 fragPos;
        in vec3 shadowPos;
        in vec2 fragUV;
        flat in vec3 fragNormal;
        in float isFoliage;

        uniform sampler2D texture0;
        uniform sampler2D texture1;
        uniform vec3 sunDir;
        uniform vec3 sunColor;
        uniform vec3 ambientColor;
        uniform float shadowsOn;
        uniform vec3 occOrigin;
        uniform vec3 eyePosition;
        uniform vec3 fogColor;
        uniform float softShadows;
        uniform float fogNear;
        uniform float fogFar;
        uniform float gameTime;

        out vec4 finalColor;
        uniform vec2 invViewport;
        const float PI = 3.14159265359;

        @SHADOW_TRACE@

        vec3 tonemapACES(vec3 x)
        {
            const float a = 2.51;
            const float b = 0.03;
            const float c = 2.43;
            const float d = 0.59;
            const float e = 0.14;
            return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
        }

        void main()
        {
            float distanceXZ = length((fragPos - eyePosition).xz);
            if (distanceXZ >= fogFar) { finalColor = vec4(fogColor, 1.0); return; }

            vec3 n = round(fragNormal);
            vec3 toView = normalize(eyePosition - fragPos);

            vec4 rawTex = texture(texture0, fragUV);
            vec3 albedo = pow(rawTex.rgb, vec3(2.2));

            float tile = floor(fragUV.x * @TILE_COUNT@.0);
            float roughness = 0.75;
            float f0 = 0.04;
            float sssAmount = 0.0;

            if (tile == 7.0 || isFoliage > 0.5) { roughness = 0.55; sssAmount = 0.85; }
            else if (tile == 0.0 || tile == 1.0) { roughness = 0.80; sssAmount = 0.30; }
            else if (tile == 3.0 || tile == 10.0) { roughness = 0.65; f0 = 0.05; }
            else if (tile == 9.0) { roughness = 0.50; f0 = 0.06; }
            else if (tile == 8.0) { roughness = 0.60; f0 = 0.04; }

            float ndl = max(dot(n, sunDir), 0.0);
            float vis = 1.0;
            if (shadowsOn > 0.5 && (ndl > 0.0 || sssAmount > 0.0) && dot(sunColor, vec3(1.0)) > 0.01)
                vis = filteredShadow(shadowPos + n * 0.025);

            vec3 diffuse = sunColor * ndl * vis;
            if (sssAmount > 0.0)
            {
                float wrap = clamp(ndl * 0.5 + 0.5, 0.0, 1.0);
                float through = pow(clamp(dot(-toView, sunDir) * 0.5 + 0.5, 0.0, 1.0), 4.0);
                vec3 sss = sunColor * (0.35 * wrap + 0.55 * through) * (1.0 - ndl) * vis;
                diffuse += sss * sssAmount * (0.6 * albedo + 0.4);
            }

            vec3 specular = vec3(0.0);
            if (ndl > 0.0 && vis > 0.01)
            {
                vec3 halfway = normalize(toView + sunDir);
                float NoH = max(dot(n, halfway), 0.0);
                float NoV = max(dot(n, toView), 0.001);
                float VoH = max(dot(toView, halfway), 0.0);
                float a = max(roughness * roughness, 0.004);
                float a2 = a * a;
                float d = NoH * NoH * (a2 - 1.0) + 1.0;
                float D_ggx = a2 / (PI * d * d);
                float k = a * 0.5;
                float V_smith = 0.25 / ((NoV * (1.0 - k) + k) * (ndl * (1.0 - k) + k));
                float F_schlick = f0 + (1.0 - f0) * pow(1.0 - VoH, 5.0);
                specular = sunColor * (D_ggx * V_smith * F_schlick * ndl * PI) * (0.65 * vis);
            }

            vec3 aoA = 2.0404 * albedo - 0.3324;
            vec3 aoB = -4.7951 * albedo + 0.6417;
            vec3 aoC = 2.7552 * albedo + 0.6903;
            vec3 occlusion = max(vec3(fragAO), ((fragAO * aoA + aoB) * fragAO + aoC) * fragAO);

            float hemisphere = 0.45 + 0.55 * (n.y * 0.5 + 0.5);
            vec3 bounce = sunColor * vec3(1.0, 0.94, 0.82) * (0.085 * (1.0 - 0.6 * n.y)) * (shadowsOn > 0.5 ? 1.0 : 0.5);
            vec3 ambient = (ambientColor * hemisphere + bounce) * occlusion;
            vec3 surfaceColor = albedo * (diffuse + ambient) + specular;

            float cosSun = dot(-toView, sunDir);
            float mie = pow(max(cosSun, 0.0), 16.0) * 0.4 + pow(max(cosSun, 0.0), 64.0) * 0.6;
            vec3 inscatterFog = mix(fogColor, sunColor * 1.15, mie * clamp(sunDir.y * 2.0, 0.0, 1.0));
            float fog = smoothstep(fogNear, fogFar, distanceXZ);
            vec3 litWithFog = mix(surfaceColor, inscatterFog, fog);

            vec3 hdrMapped = tonemapACES(litWithFog * 0.95);
            vec2 screenDist = gl_FragCoord.xy * invViewport - vec2(0.5);
            float vignette = 1.0 - dot(screenDist, screenDist) * 0.22;
            hdrMapped *= clamp(vignette, 0.0, 1.0);

            vec3 finalRgb = pow(hdrMapped, vec3(1.0 / 2.2));
            finalColor = vec4(finalRgb, rawTex.a);
        }
        """;
}
