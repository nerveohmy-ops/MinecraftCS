namespace MinecraftCS.Graphics;

internal interface IRenderMesh : IDisposable
{
}

internal readonly record struct RenderShaderSource(string Vertex, string Fragment);

internal interface IScreenRenderChain : IDisposable
{
    bool EnsureSize(int width, int height);
    void BeginScene();
    void PresentScene(float time);
}

internal interface IRenderBackend
{
    IRenderMesh UploadChunk(ReadOnlySpan<float> positions, ReadOnlySpan<float> uv,
        ReadOnlySpan<float> normals, ReadOnlySpan<byte> colors, ReadOnlySpan<ushort> indices);
    void DrawChunk(IRenderMesh mesh);
    IScreenRenderChain CreateScreenChain(IReadOnlyList<RenderShaderSource> stages, int width, int height);
}

internal interface IPreviewRenderBackend : IRenderBackend, IDisposable
{
    string Description { get; }
    string ScreenshotName { get; }
    void Begin(System.Numerics.Vector3 position, System.Numerics.Vector3 direction, ushort width, ushort height);
    void Resize(ushort width, ushort height);
    uint Present();
    void Screenshot(string path);
}
