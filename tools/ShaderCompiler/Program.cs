using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;

const string usage = "Usage: ShaderCompiler <minecraft-pack.zip> <minecraftcs-pack.zip>";
if (args is ["--help"] or ["-h"])
{
    Console.WriteLine(usage);
    return 0;
}
if (args.Length != 2)
{
    Console.Error.WriteLine(usage);
    return 2;
}

try
{
    string input = Path.GetFullPath(args[0]);
    string output = Path.GetFullPath(args[1]);
    if (!Path.GetExtension(output).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The output path must end in .zip.");
    if (File.Exists(output)) throw new IOException($"Output already exists: {output}");

    using var source = ZipFile.OpenRead(input);
    if (source.Entries.Count > 20000) throw new InvalidDataException("Too many entries in the shader archive.");

    const string settingsPath = "shaders/lib/common.glsl";
    var settingsEntries = source.Entries.Where(entry =>
        entry.FullName == settingsPath || entry.FullName.EndsWith("/" + settingsPath, StringComparison.Ordinal))
        .Where(entry =>
        {
            string root = entry.FullName[..^settingsPath.Length];
            return source.GetEntry(root + "shaders/world0/gbuffers_terrain.fsh") != null ||
                source.GetEntry(root + "shaders/gbuffers_terrain.fsh") != null;
        }).ToArray();
    if (settingsEntries.Length != 1)
        throw new InvalidDataException("Expected one Minecraft terrain pack with shaders/lib/common.glsl.");
    var settings = settingsEntries[0];
    const int maxSettingsLength = 1024 * 1024;
    if (settings.Length > maxSettingsLength) throw new InvalidDataException("Shader settings file is too large.");

    using var reader = new StreamReader(settings.Open());
    var buffer = new char[maxSettingsLength + 1];
    int length = reader.ReadBlock(buffer);
    if (length > maxSettingsLength) throw new InvalidDataException("Shader settings file is too large.");
    string code = Regex.Replace(new string(buffer, 0, length).ReplaceLineEndings("\n"),
        @"/\*[\s\S]*?\*/|//[^\r\n]*", match =>
        " " + new string('\n', match.Value.Count(c => c == '\n')));
    var defines = Regex.Matches(code, @"(?m)^[^\S\r\n]*#[^\S\r\n]*define[^\S\r\n]+([A-Za-z_]\w*)(?:[^\S\r\n]+([^\r\n]*?))?[^\S\r\n]*$")
        .ToLookup(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);

    float ReadSetting(string name, float min, float max)
    {
        var values = defines[name].Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length != 1 ||
            !float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
            !float.IsFinite(value) || value < min || value > max)
            throw new InvalidDataException($"Expected one numeric {name} between {min} and {max} in {settings.FullName}.");
        return value;
    }

    float exposure = ReadSetting("TM_EXPOSURE", 0.05f, 8f);
    float contrast = ReadSetting("TM_CONTRAST", 0.1f, 4f);
    float saturation = ReadSetting("T_SATURATION", 0f, 4f);
    float vibrance = defines.Contains("T_VIBRANCE") ? ReadSetting("T_VIBRANCE", 0f, 4f) : 1f;
    bool vignette = defines.Contains("VIGNETTE_R");

    using var template = Assembly.GetExecutingAssembly().GetManifestResourceStream("ShaderCompiler.Terrain.frag.glsl")
        ?? throw new InvalidOperationException("Missing MinecraftCS terrain shader template.");
    using var templateReader = new StreamReader(template);
    string fragment = templateReader.ReadToEnd()
        .Replace("@EXPOSURE@", GlslFloat(exposure))
        .Replace("@CONTRAST@", GlslFloat(contrast))
        .Replace("@SATURATION@", GlslFloat(saturation))
        .Replace("@VIBRANCE@", GlslFloat(vibrance))
        .Replace("@VIGNETTE@", vignette ? "true" : "false");

    string description = FormattableString.Invariant(
        $"Source: {Path.GetFileName(input)}\nExposure: {exposure}; contrast: {contrast}; saturation: {saturation}; vibrance: {vibrance}; vignette: {vignette}\n");
    const string scope = "Terrain color adaptation. MinecraftCS supplies lighting, textures, fog and shadows. Source passes are not included.";

    string outputDirectory = Path.GetDirectoryName(output)!;
    if (!Directory.Exists(outputDirectory)) throw new DirectoryNotFoundException($"Output directory does not exist: {outputDirectory}");
    string temporary = Path.Combine(outputDirectory, $".shadercompiler-{Guid.NewGuid():N}.tmp");
    try
    {
        using (var target = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            WriteEntry(target, "fragment.glsl", fragment);
            WriteEntry(target, "pack.txt", description + scope + "\n");
        }
        File.Move(temporary, output);
    }
    finally
    {
        File.Delete(temporary);
    }

    Console.WriteLine($"Created {output}");
    Console.Write(description);
    Console.WriteLine(scope);
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"ShaderCompiler: {ex.Message}");
    return 1;
}

static string GlslFloat(float value) => value.ToString("0.0########", CultureInfo.InvariantCulture);

static void WriteEntry(ZipArchive archive, string name, string content)
{
    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
    writer.Write(content);
}
