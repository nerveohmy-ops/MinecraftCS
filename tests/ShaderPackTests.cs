using System.IO.Compression;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    static void TestMinecraftCsShaderPacks(Action<bool, string> check)
    {
        const string root = "Wrapped/";
        var files = new Dictionary<string, string>
        {
            [root + "fragment.glsl"] = "#version 330\n#include \"/lib/color.glsl\"\n",
            [root + "lib/color.glsl"] = "const vec4 tint = vec4(1.0);"
        };
        var pack = ReadShaderEntries(files.Keys, file => files[file]);
        check(pack.Vertex == VertexShaderSrc && pack.Fragment.Contains("const vec4 tint"),
            "Wrapped MinecraftCS pack uses the default vertex shader and resolves includes");

        files[root + "vertex.glsl"] = "#version 330\n// custom vertex";
        pack = ReadShaderEntries(files.Keys, file => files[file]);
        check(pack.Vertex.Contains("// custom vertex"), "MinecraftCS pack uses its supplied vertex shader");

        bool Rejects()
        {
            try { ReadShaderEntries(files.Keys, file => files[file]); return false; }
            catch (InvalidDataException) { return true; }
        }

        files[root + "fragment.glsl"] = "#include \"../outside.glsl\"";
        check(Rejects(), "Shader includes cannot escape a wrapped pack");
        files[root + "fragment.glsl"] = "#include \"fragment.glsl\"";
        check(Rejects(), "Recursive shader includes are rejected");

        files.Clear();
        files["shaders/program/gbuffers_solid.glsl"] = "void main() {}";
        check(Rejects(), "Unsupported shader formats are rejected without substituting another pack");

        using var zipBytes = new MemoryStream();
        using (var archive = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry(root + "fragment.glsl").Open()))
            writer.Write("#version 330\nvoid main() {}");
        string zipPath = Path.Combine(Path.GetTempPath(), $"MinecraftCS-shader-{Guid.NewGuid():N}.zip");
        try
        {
            File.WriteAllBytes(zipPath, zipBytes.ToArray());
            check(IsMinecraftCsShaderArchive(zipPath) && ReadShaderPack(zipPath).Fragment.Contains("void main()"),
                "MinecraftCS ZIP pack is listed and loads directly");

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            {
                archive.GetEntry(root + "fragment.glsl").Delete();
                archive.CreateEntry("shaders/gbuffers_terrain.fsh");
            }
            check(!IsMinecraftCsShaderArchive(zipPath), "Unsupported ZIP packs stay out of the shader menu");
        }
        finally { File.Delete(zipPath); }
    }
}
