using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MinecraftCS.Graphics;

internal sealed unsafe class FilamentRenderBackend : IPreviewRenderBackend
{
    const string Library = "FilamentNative/MinecraftCS.Filament.dll";
    bool initialized;

    [StructLayout(LayoutKind.Sequential)]
    struct Vertex
    {
        public Vector3 Position;
        public Vector2 Uv;
        public uint Color;
    }

    sealed class FilamentMesh(nint id) : IRenderMesh
    {
        public nint Id { get; private set; } = id;

        public void Dispose()
        {
            if (Id == 0) return;
            MinecraftFilamentRelease(Id);
            Id = 0;
        }
    }

    public FilamentRenderBackend(int width, int height, bool fullscreen, bool interactive,
        void* pixels, ushort atlasWidth, ushort atlasHeight)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "FilamentNative");
        byte[] package = File.ReadAllBytes(Path.Combine(folder, "terrain.filamat"));
        if (MinecraftFilamentOpenWindow(width, height, fullscreen ? 1 : 0, interactive ? 1 : 0) == 0)
            throw new InvalidOperationException("Could not create the Filament window");
        try
        {
            fixed (byte* data = package)
                initialized = MinecraftFilamentInit(data, (uint)package.Length, pixels, atlasWidth, atlasHeight) != 0;
            if (!initialized) throw new InvalidOperationException("Filament could not initialize Vulkan");
        }
        catch
        {
            MinecraftFilamentCloseWindow();
            throw;
        }
    }

    public (bool Open, ushort Width, ushort Height, Vector2 Mouse, Vector3 Motion, bool Fast) Poll()
    {
        int open = MinecraftFilamentPoll(out ushort width, out ushort height, out int keys, out float x, out float y);
        int Axis(int positive, int negative) => ((keys >> positive) & 1) - ((keys >> negative) & 1);
        return (open != 0, width, height, new Vector2(x, y),
            new Vector3(Axis(2, 3), Axis(4, 5), Axis(0, 1)), (keys & 64) != 0);
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
                float direct = MathF.Max(0f, normals[p] * 0.42f + normals[p + 1] * 0.82f + normals[p + 2] * 0.33f);
                uint light = (uint)(colors[c] * (0.55f + 0.45f * direct));
                data[i] = new Vertex
                {
                    Position = new Vector3(positions[p], positions[p + 1], positions[p + 2]),
                    Uv = new Vector2(uv[t], uv[t + 1]),
                    Color = 0xff000000u | light * 0x010101u
                };
            }
            fixed (Vertex* vertices = data)
            fixed (ushort* indexData = indices)
            {
                nint mesh = MinecraftFilamentUpload(vertices, (uint)count, indexData, (uint)indices.Length);
                return mesh == 0 ? null : new FilamentMesh(mesh);
            }
        }
        finally { ArrayPool<Vertex>.Shared.Return(data); }
    }

    public void DrawChunk(IRenderMesh mesh) => MinecraftFilamentDraw(((FilamentMesh)mesh).Id);

    public IScreenRenderChain CreateScreenChain(IReadOnlyList<RenderShaderSource> stages, int width, int height) =>
        throw new NotSupportedException("The Filament preview uses a compiled Filament material");

    public void Begin(Vector3 position, Vector3 direction, ushort width, ushort height) =>
        MinecraftFilamentBegin(position.X, position.Y, position.Z, direction.X, direction.Y, direction.Z, width, height);

    public string Description => "Vulkan";
    public string ScreenshotName => "filament-preview.ppm";
    public void Resize(ushort width, ushort height) => MinecraftFilamentResize();
    public uint Present() => MinecraftFilamentFrame();
    public void Screenshot(string path) => MinecraftFilamentScreenshot(path);

    public void Dispose()
    {
        if (!initialized) return;
        MinecraftFilamentShutdown();
        MinecraftFilamentCloseWindow();
        initialized = false;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int MinecraftFilamentOpenWindow(int width, int height, int fullscreen, int interactive);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void MinecraftFilamentCloseWindow();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int MinecraftFilamentPoll(out ushort width, out ushort height, out int keys, out float x, out float y);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int MinecraftFilamentInit(byte* package, uint packageSize, void* pixels, ushort width, ushort height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void MinecraftFilamentShutdown();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern nint MinecraftFilamentUpload(Vertex* vertices, uint count, ushort* indices, uint indexCount);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void MinecraftFilamentRelease(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void MinecraftFilamentDraw(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void MinecraftFilamentBegin(float x, float y, float z, float dx, float dy, float dz, ushort width, ushort height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void MinecraftFilamentResize();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern uint MinecraftFilamentFrame();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void MinecraftFilamentScreenshot([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
}
