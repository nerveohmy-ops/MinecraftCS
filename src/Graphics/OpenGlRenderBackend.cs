using System.Numerics;
using Raylib_cs;

namespace MinecraftCS.Graphics;

internal sealed unsafe class OpenGlRenderBackend
{
    Material worldMaterial;

    public void SetWorldMaterial(Material material) => worldMaterial = material;

    internal sealed class ChunkMesh(Mesh handle) : IDisposable
    {
        public Mesh Handle { get; private set; } = handle;

        public void Dispose()
        {
            if (Handle.Vertices == null) return;
            Raylib.UnloadMesh(Handle);
            Handle = default;
        }
    }

    public ChunkMesh UploadChunk(ReadOnlySpan<float> positions, ReadOnlySpan<float> uv,
        ReadOnlySpan<float> normals, ReadOnlySpan<byte> colors, ReadOnlySpan<ushort> indices)
    {
        Mesh mesh = new() { VertexCount = positions.Length / 3, TriangleCount = indices.Length / 3 };
        bool uploaded = false;
        try
        {
            // Raylib owns these arrays after upload and releases them together with the VBOs.
            mesh.Vertices = Raylib.New<float>((uint)positions.Length);
            mesh.TexCoords = Raylib.New<float>((uint)uv.Length);
            mesh.Normals = Raylib.New<float>((uint)normals.Length);
            mesh.Colors = Raylib.New<byte>((uint)colors.Length);
            mesh.Indices = Raylib.New<ushort>((uint)indices.Length);
            if (mesh.Vertices == null || mesh.TexCoords == null || mesh.Normals == null ||
                mesh.Colors == null || mesh.Indices == null) return null;

            positions.CopyTo(new Span<float>(mesh.Vertices, positions.Length));
            uv.CopyTo(new Span<float>(mesh.TexCoords, uv.Length));
            normals.CopyTo(new Span<float>(mesh.Normals, normals.Length));
            colors.CopyTo(new Span<byte>(mesh.Colors, colors.Length));
            indices.CopyTo(new Span<ushort>(mesh.Indices, indices.Length));

            Raylib.UploadMesh(&mesh, false);
            bool valid = mesh.VboId != null && mesh.VboId[0] != 0 && mesh.VboId[1] != 0 &&
                mesh.VboId[2] != 0 && mesh.VboId[3] != 0 && mesh.VboId[6] != 0;
            if (!valid) return null;
            var resource = new ChunkMesh(mesh);
            uploaded = true;
            return resource;
        }
        finally
        {
            if (!uploaded) Raylib.UnloadMesh(mesh);
        }
    }

    public void DrawChunk(ChunkMesh mesh) =>
        Raylib.DrawMesh(mesh.Handle, worldMaterial, Matrix4x4.Identity);
}
