using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MinecraftCS.Graphics;

internal sealed unsafe class BgfxRenderBackend : IPreviewRenderBackend
{
    const string Library = "BgfxNative/MinecraftCS.Bgfx.dll";
    bool initialized;

    [StructLayout(LayoutKind.Sequential)]
    struct Vertex
    {
        public Vector3 Position, Normal;
        public Vector2 Uv;
        public uint Color;
    }

    sealed class BgfxMesh(uint id) : IRenderMesh
    {
        public uint Id { get; private set; } = id;

        public void Dispose()
        {
            if (Id == uint.MaxValue) return;
            MinecraftBgfxRelease(Id);
            Id = uint.MaxValue;
        }
    }

    public BgfxRenderBackend(nint window, ushort width, ushort height, void* pixels, ushort atlasWidth, ushort atlasHeight)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "BgfxNative");
        byte[] vs = File.ReadAllBytes(Path.Combine(folder, "terrain.vs.bin"));
        byte[] fs = File.ReadAllBytes(Path.Combine(folder, "terrain.fs.bin"));
        fixed (byte* v = vs, f = fs)
            initialized = MinecraftBgfxInit(window, width, height, v, (uint)vs.Length, f, (uint)fs.Length,
                pixels, atlasWidth, atlasHeight) != 0;
        if (!initialized) throw new InvalidOperationException("bgfx could not initialize Direct3D 11");
    }

    public IRenderMesh UploadChunk(ReadOnlySpan<float> positions, ReadOnlySpan<float> uv,
        ReadOnlySpan<float> normals, ReadOnlySpan<byte> colors, ReadOnlySpan<ushort> indices)
    {
        int count = positions.Length / 3;
        Vertex[] data = ArrayPool<Vertex>.Shared.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                int p = i * 3, t = i * 2, c = i * 4;
                data[i] = new Vertex
                {
                    Position = new Vector3(positions[p], positions[p + 1], positions[p + 2]),
                    Normal = new Vector3(normals[p], normals[p + 1], normals[p + 2]),
                    Uv = new Vector2(uv[t], uv[t + 1]),
                    Color = (uint)(colors[c] | colors[c + 1] << 8 | colors[c + 2] << 16 | colors[c + 3] << 24)
                };
            }
            fixed (Vertex* vertices = data)
            fixed (ushort* indexData = indices)
            {
                uint id = MinecraftBgfxUpload(vertices, (uint)count, indexData, (uint)indices.Length);
                return id == uint.MaxValue ? null : new BgfxMesh(id);
            }
        }
        finally { ArrayPool<Vertex>.Shared.Return(data); }
    }

    public void DrawChunk(IRenderMesh mesh) => MinecraftBgfxDraw(((BgfxMesh)mesh).Id);

    public IScreenRenderChain CreateScreenChain(IReadOnlyList<RenderShaderSource> stages, int width, int height) =>
        throw new NotSupportedException("The bgfx preview uses precompiled terrain shaders");

    public void Begin(Vector3 position, Vector3 direction, ushort width, ushort height)
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(position, position + direction, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(70f * MathF.PI / 180f,
            width / (float)height, 0.05f, 512f);
        MinecraftBgfxBegin(&view, &projection, width, height);
    }

    public void Resize(ushort width, ushort height) => MinecraftBgfxResize(width, height);
    public uint Present() => MinecraftBgfxFrame();
    public string Description => $"Direct3D11, vendor=0x{MinecraftBgfxVendor():X4}";
    public string ScreenshotName => "bgfx-preview";
    public void Screenshot(string path) => MinecraftBgfxScreenshot(path);

    public void Dispose()
    {
        if (!initialized) return;
        MinecraftBgfxShutdown();
        initialized = false;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int MinecraftBgfxInit(nint hwnd, ushort width, ushort height,
        byte* vs, uint vsSize, byte* fs, uint fsSize, void* pixels, ushort atlasWidth, ushort atlasHeight);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void MinecraftBgfxShutdown();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern uint MinecraftBgfxUpload(Vertex* vertices, uint count, ushort* indices, uint indexCount);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void MinecraftBgfxRelease(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void MinecraftBgfxDraw(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void MinecraftBgfxBegin(Matrix4x4* view, Matrix4x4* projection, ushort width, ushort height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void MinecraftBgfxResize(ushort width, ushort height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern uint MinecraftBgfxFrame();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern ushort MinecraftBgfxVendor();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void MinecraftBgfxScreenshot([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
}
