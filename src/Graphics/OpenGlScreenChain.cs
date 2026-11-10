using System.Numerics;
using Raylib_cs;

namespace MinecraftCS.Graphics;

internal sealed unsafe class OpenGlScreenChain(int stageCount) : IScreenRenderChain
{
    readonly struct ScreenPass(Shader shader)
    {
        public readonly Shader Shader = shader;
        public readonly int WidthLocation = Raylib.GetShaderLocation(shader, "viewWidth");
        public readonly int HeightLocation = Raylib.GetShaderLocation(shader, "viewHeight");
        public readonly int DepthLocation = Raylib.GetShaderLocation(shader, "javaSceneDepth");
        public readonly int TimeLocation = Raylib.GetShaderLocation(shader, "frameTimeCounter");
    }

    readonly ScreenPass[] passes = new ScreenPass[stageCount];
    RenderTexture2D sceneTarget, scratchTarget;
    bool readsDepth;

    static bool HasTargetSize(RenderTexture2D target, int width, int height) =>
        target.Id != 0 && target.Texture.Id != 0 && target.Texture.Width == width && target.Texture.Height == height;

    static void ReleaseTarget(ref RenderTexture2D target)
    {
        if (target.Id != 0) Raylib.UnloadRenderTexture(target);
        target = default;
    }

    static RenderTexture2D LoadSceneWithDepth(int width, int height)
    {
        uint framebuffer = Rlgl.LoadFramebuffer(width, height);
        if (framebuffer == 0) return default;
        const PixelFormat format = PixelFormat.UncompressedR8G8B8A8;
        uint color = Rlgl.LoadTexture(null, width, height, format, 1);
        uint depth = Rlgl.LoadTextureDepth(width, height, false);
        if (color == 0 || depth == 0)
        {
            if (color != 0) Rlgl.UnloadTexture(color);
            if (depth != 0) Rlgl.UnloadTexture(depth);
            Rlgl.UnloadFramebuffer(framebuffer);
            return default;
        }

        Rlgl.FramebufferAttach(framebuffer, color, FramebufferAttachType.ColorChannel0, FramebufferAttachTextureType.Texture2D, 0);
        Rlgl.FramebufferAttach(framebuffer, depth, FramebufferAttachType.Depth, FramebufferAttachTextureType.Texture2D, 0);
        RenderTexture2D result = new()
        {
            Id = framebuffer,
            Texture = new Texture2D { Id = color, Width = width, Height = height, Mipmaps = 1, Format = format },
            Depth = new Texture2D { Id = depth, Width = width, Height = height, Mipmaps = 1 }
        };
        if (Rlgl.FramebufferComplete(framebuffer)) return result;
        ReleaseTarget(ref result);
        return default;
    }

    public void CompileStage(int index, RenderShaderSource source)
    {
        Shader shader = Raylib.LoadShaderFromMemory(source.Vertex, source.Fragment);
        if (shader.Id == 0 || shader.Id == Rlgl.GetShaderIdDefault())
            throw new InvalidDataException($"Screen pass {index + 1} failed");
        passes[index] = new ScreenPass(shader);
    }

    public bool EnsureSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        bool sampledDepth = passes.Any(pass => pass.DepthLocation >= 0);
        bool replaceScene = !HasTargetSize(sceneTarget, width, height) || readsDepth != sampledDepth;
        bool replaceScratch = passes.Length > 1 && !HasTargetSize(scratchTarget, width, height);
        RenderTexture2D scene = default, scratch = default;
        try
        {
            if (replaceScene)
            {
                scene = sampledDepth ? LoadSceneWithDepth(width, height) : Raylib.LoadRenderTexture(width, height);
                if (!HasTargetSize(scene, width, height)) return false;
            }
            if (replaceScratch)
            {
                scratch = Raylib.LoadRenderTexture(width, height);
                if (!HasTargetSize(scratch, width, height)) return false;
            }
            if (replaceScene)
            {
                ReleaseTarget(ref sceneTarget);
                sceneTarget = scene;
                readsDepth = sampledDepth;
                scene = default;
            }
            if (replaceScratch)
            {
                ReleaseTarget(ref scratchTarget);
                scratchTarget = scratch;
                scratch = default;
            }
            return true;
        }
        finally
        {
            ReleaseTarget(ref scene);
            ReleaseTarget(ref scratch);
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < passes.Length; i++)
        {
            if (passes[i].Shader.Id != 0) Raylib.UnloadShader(passes[i].Shader);
            passes[i] = default;
        }
        ReleaseTarget(ref sceneTarget);
        ReleaseTarget(ref scratchTarget);
    }

    public void BeginScene() => Raylib.BeginTextureMode(sceneTarget);

    public void PresentScene(float time)
    {
        Raylib.EndTextureMode();
        if (readsDepth)
            Rlgl.FramebufferAttach(sceneTarget.Id, 0, FramebufferAttachType.Depth, FramebufferAttachTextureType.Texture2D, 0);
        try { DrawScreenChain(time); }
        finally
        {
            // Ping-pong writes may target the scene's color attachment. Detach its depth while sampling it,
            // then restore the attachment so Raylib owns it at teardown and can render depth next frame.
            if (readsDepth)
                Rlgl.FramebufferAttach(sceneTarget.Id, sceneTarget.Depth.Id,
                    FramebufferAttachType.Depth, FramebufferAttachTextureType.Texture2D, 0);
        }
    }

    void DrawScreenChain(float time)
    {
        RenderTexture2D input = sceneTarget, output = scratchTarget;
        float width = input.Texture.Width, height = input.Texture.Height;
        Rectangle source = new(0, 0, width, -height), destination = new(0, 0, width, height);
        for (int i = 0; i < passes.Length; i++)
        {
            bool last = i == passes.Length - 1;
            if (!last) Raylib.BeginTextureMode(output);
            Raylib.ClearBackground(Color.Black);

            var pass = passes[i];
            Raylib.SetShaderValue(pass.Shader, pass.WidthLocation, &width, ShaderUniformDataType.Float);
            Raylib.SetShaderValue(pass.Shader, pass.HeightLocation, &height, ShaderUniformDataType.Float);
            Raylib.SetShaderValue(pass.Shader, pass.TimeLocation, &time, ShaderUniformDataType.Float);
            Raylib.BeginShaderMode(pass.Shader);
            if (pass.DepthLocation >= 0)
                Raylib.SetShaderValueTexture(pass.Shader, pass.DepthLocation, sceneTarget.Depth);
            // The pass replaces scene color, including alpha. Never sample from the target being written.
            Rlgl.DisableColorBlend();
            Raylib.DrawTexturePro(input.Texture, source, destination, Vector2.Zero, 0f, Color.White);
            Raylib.EndShaderMode();
            Rlgl.EnableColorBlend();

            if (!last)
            {
                Raylib.EndTextureMode();
                (input, output) = (output, input);
            }
        }
    }
}
