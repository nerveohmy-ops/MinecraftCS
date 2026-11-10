using System.Diagnostics;
using System.Numerics;
using MinecraftCS.Graphics;
using Raylib_cs;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    static int RunRenderPreview(string[] args, bool filament)
    {
        string rendererName = filament ? "Filament" : "bgfx";
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine($"The {rendererName} preview requires Windows x64.");
            return 2;
        }
        bool test = args.Contains(filament ? "--filament-test" : "--bgfx-test");
        bool fullscreen = args.Contains("--fullscreen") || args.Contains("-f");
        if (!filament) Raylib.InitWindow(WindowW, WindowH, $"MinecraftCS — {rendererName}");
        IPreviewRenderBackend backend = null;
        try
        {
            if (!filament)
            {
                Raylib.SetExitKey(KeyboardKey.Escape);
                if (fullscreen) ToggleFullscreenMode();
                Raylib.DisableCursor();
            }

            Image image = BuildAtlasImage();
            try
            {
                if (image.Data == null) throw new InvalidOperationException("Could not read the block atlas");
                Raylib.ImageFormat(ref image, PixelFormat.UncompressedR8G8B8A8);
                ushort atlasWidth = (ushort)image.Width, atlasHeight = (ushort)image.Height;
                backend = filament
                    ? new FilamentRenderBackend(WindowW, WindowH, fullscreen, !test, image.Data, atlasWidth, atlasHeight)
                    : new BgfxRenderBackend((nint)Raylib.GetWindowHandle(),
                        (ushort)Raylib.GetRenderWidth(), (ushort)Raylib.GetRenderHeight(), image.Data, atlasWidth, atlasHeight);
            }
            finally { if (image.Data != null) Raylib.UnloadImage(image); }

            var input = ReadPreviewInput(backend);
            ushort width = input.Width, height = input.Height;
            renderBackend = backend;

            GenerateWorld();
            SpawnPlayer();
            pos.Y += 12;
            pitch = -0.48f;
            foreach (var key in NearbyChunkIndices(renderRadius)) GenerateChunk(key.X, key.Z);
            RebuildDirtyChunks(int.MaxValue);

            long previous = Stopwatch.GetTimestamp();
            long started = previous;
            uint draws = 0;
            int frames = 0;
            while (!test || (frames < 60 && Stopwatch.GetElapsedTime(started).TotalSeconds < 15))
            {
                input = ReadPreviewInput(backend);
                if (!input.Open) break;
                ushort newWidth = input.Width, newHeight = input.Height;
                if (newWidth == 0 || newHeight == 0) { Thread.Sleep(16); continue; }
                if (newWidth != width || newHeight != height)
                {
                    width = newWidth;
                    height = newHeight;
                    backend.Resize(width, height);
                }
                long now = Stopwatch.GetTimestamp();
                float dt = (float)Math.Min(Stopwatch.GetElapsedTime(previous, now).TotalSeconds, 0.05);
                previous = now;
                if (!test)
                {
                    MovePreviewCamera(input.Mouse, input.Motion, input.Fast, dt);
                    StreamChunks();
                    RebuildDirtyChunks();
                }

                backend.Begin(pos, LookDir(), width, height);
                var center = PlayerChunk;
                foreach (var chunk in chunks.Values)
                {
                    if (chunk.Meshes is not { Count: > 0 } || ChunkDistance((chunk.X, chunk.Z), center) > renderRadius) continue;
                    foreach (var mesh in chunk.Meshes) renderBackend.DrawChunk(mesh);
                }
                if (test && frames == 59) backend.Screenshot(Path.Combine(Environment.CurrentDirectory, backend.ScreenshotName));
                uint submitted = backend.Present();
                if (submitted > 0) { draws = submitted; frames++; }
                else Thread.Sleep(1);
            }

            if (test)
            {
                Console.WriteLine($"{rendererName.ToUpperInvariant()} CHECK: {backend.Description}, draws={draws}, frames={frames}, fullscreen={(filament ? fullscreen : Raylib.IsWindowFullscreen())}, resolution={width}x{height}");
                return frames == 60 && draws > 0 ? 0 : 1;
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            Console.Error.WriteLine($"{rendererName} preview: {ex.Message}");
            Console.Error.WriteLine(filament ? "Build tools/Filament/Build-Filament.ps1, then rebuild MinecraftCS." : "Build tools/Bgfx/Build-Bgfx.ps1, then rebuild MinecraftCS.");
            return 2;
        }
        finally
        {
            foreach (var chunk in chunks.Values) UnloadChunkMeshes(chunk);
            backend?.Dispose();
            if (!filament) Raylib.CloseWindow();
        }
    }

    static (bool Open, ushort Width, ushort Height, Vector2 Mouse, Vector3 Motion, bool Fast)
        ReadPreviewInput(IPreviewRenderBackend backend)
    {
        if (backend is FilamentRenderBackend filament) return filament.Poll();

        Raylib.PollInputEvents();
        int Axis(KeyboardKey positive, KeyboardKey negative) =>
            (Raylib.IsKeyDown(positive) ? 1 : 0) - (Raylib.IsKeyDown(negative) ? 1 : 0);
        Vector3 motion = new(Axis(KeyboardKey.D, KeyboardKey.A), Axis(KeyboardKey.Space, KeyboardKey.LeftControl),
            Axis(KeyboardKey.W, KeyboardKey.S));
        return (!Raylib.WindowShouldClose(), (ushort)Raylib.GetRenderWidth(), (ushort)Raylib.GetRenderHeight(),
            Raylib.GetMouseDelta(), motion, Raylib.IsKeyDown(KeyboardKey.LeftShift));
    }

    static void MovePreviewCamera(Vector2 delta, Vector3 motion, bool fast, float dt)
    {
        yaw += delta.X * MouseSens;
        pitch = Math.Clamp(pitch - delta.Y * MouseSens, -1.55f, 1.55f);
        Vector3 forward = new(MathF.Cos(yaw), 0, MathF.Sin(yaw));
        Vector3 right = Vector3.Cross(forward, Vector3.UnitY);
        Vector3 move = forward * motion.Z + right * motion.X + Vector3.UnitY * motion.Y;
        if (move.LengthSquared() > 0)
            pos += Vector3.Normalize(move) * (fast ? 25f : 10f) * dt;
    }
}
