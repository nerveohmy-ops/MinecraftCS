using Raylib_cs;
using System.Numerics;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    static bool paused, quitRequested;

    // Хотбар на 9 слотов
    static readonly byte[] Hotbar = { Grass, Dirt, Stone, Sand, Wood, Leaves, Planks, Bricks, Cobblestone };
    static readonly string[] BlockNames = { "Grass", "Dirt", "Stone", "Sand", "Wood", "Leaves", "Planks", "Bricks", "Cobblestone" };
    static readonly int[] HotbarIcon = { 0, 2, 3, 4, 5, 7, 8, 9, 10 };   // тайл для иконки
    static int selected;

    static bool debugHud, inventoryOpen;

    static void StopRemoteMovement()
    {
        if (coopMatched && !coopHost)
            SendCoop(new Packet { Kind = "input", Keys = 0, Yaw = yaw, Pitch = pitch, Slot = selected });
    }

    static void SetPaused(bool value)
    {
        if (paused == value) return;
        paused = value;
        sliderDragging = false;
        if (value)
        {
            StopRemoteMovement();
            Raylib.EnableCursor();
        }
        else
        {
            Raylib.DisableCursor();
            frame = 0;
        }
    }

    static void SetInventoryOpen(bool value)
    {
        inventoryOpen = value;
        if (value)
        {
            StopRemoteMovement();
            Raylib.EnableCursor();
        }
        else
        {
            Raylib.DisableCursor();
            frame = 0;
        }
    }

    static void OpenShaderMenu()
    {
        shaderMenuOpen = true;
        ScanShaderPacks();
    }

    static Rectangle PauseButton(int row) =>
        new(Raylib.GetScreenWidth() / 2f - 155, Raylib.GetScreenHeight() / 2f - 42 + row * 42, 310, 36);

    static bool DrawMenuButton(string label, Rectangle bounds)
    {
        bool hovered = Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), bounds);
        Raylib.DrawRectangleRec(bounds, hovered ? Col(67, 92, 113) : Col(38, 44, 52));
        Raylib.DrawRectangleLinesEx(bounds, 1, Col(108, 126, 143));
        Raylib.DrawText(label, (int)(bounds.X + (bounds.Width - Raylib.MeasureText(label, 20)) / 2),
            (int)bounds.Y + 8, 20, Col(255, 255, 255));
        return hovered && Raylib.IsMouseButtonPressed(MouseButton.Left);
    }

    static void DrawInventory()
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        Raylib.DrawRectangle(0, 0, w, h, Col(0, 0, 0, 160));
        int left = w / 2 - 224, top = h / 2 - 156;
        Raylib.DrawRectangle(left, top, 448, 312, Col(29, 35, 42));
        Raylib.DrawRectangleLines(left, top, 448, 312, Col(108, 126, 143));
        Raylib.DrawText("Creative inventory", left + 18, top + 14, 24, Col(255, 255, 255));
        Raylib.DrawText("Click a block to select it  |  E / Esc to close",
            left + 18, top + 47, 16, Col(208, 220, 230));

        Vector2 mouse = Raylib.GetMousePosition();
        for (int i = 0; i < Hotbar.Length; i++)
        {
            int x = left + 18 + i % 3 * 140, y = top + 78 + i / 3 * 74;
            Rectangle bounds = new(x, y, 132, 66);
            bool hovered = Raylib.CheckCollisionPointRec(mouse, bounds);
            Raylib.DrawRectangleRec(bounds, hovered ? Col(67, 92, 113) : Col(44, 52, 61));
            if (i == selected) Raylib.DrawRectangleLinesEx(bounds, 2, Col(245, 250, 255));
            Raylib.DrawTexturePro(atlas, new Rectangle(HotbarIcon[i] * TileSize, 0, TileSize, TileSize),
                new Rectangle(x + 50, y + 6, 32, 32), Vector2.Zero, 0f, Col(255, 255, 255));
            string name = BlockNames[i];
            Raylib.DrawText(name, x + (132 - Raylib.MeasureText(name, 16)) / 2, y + 44, 16, Col(255, 255, 255));
            if (hovered && Raylib.IsMouseButtonPressed(MouseButton.Left))
            {
                selected = i;
                SetInventoryOpen(false);
                return;
            }
        }
    }

    // ════════════════════════ UI (HUD) ════════════════════════
    // Шрифт Raylib по умолчанию не содержит кириллицу, поэтому подписи на английском.
    static void DrawHud()
    {
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        int cx = sw / 2, cy = sh / 2;
        float ui = Math.Max(1f, sh / 720f);   // масштаб интерфейса под Full HD / 2K / 4K
        int S(int v) => (int)(v * ui);
        int one = Math.Max(1, S(1));

        if (!paused && !inventoryOpen)
        {
            // Прицел
            Raylib.DrawRectangle(cx - S(10), cy - one, S(20), S(3), Col(0, 0, 0, 160));
            Raylib.DrawRectangle(cx - one, cy - S(10), S(3), S(20), Col(0, 0, 0, 160));
            Raylib.DrawRectangle(cx - S(9), cy, S(18), one, Col(255, 255, 255));
            Raylib.DrawRectangle(cx, cy - S(9), one, S(18), Col(255, 255, 255));
        }

        // Хотбар
        int slot = S(54), gap = S(6);
        int total = Hotbar.Length * slot + (Hotbar.Length - 1) * gap;
        int x0 = (sw - total) / 2, y0 = sh - slot - S(20);
        for (int i = 0; i < Hotbar.Length; i++)
        {
            int x = x0 + i * (slot + gap);
            Raylib.DrawRectangle(x, y0, slot, slot, Col(0, 0, 0, 140));
            Raylib.DrawTexturePro(atlas,
                new Rectangle(HotbarIcon[i] * TileSize, 0, TileSize, TileSize),
                new Rectangle(x + S(7), y0 + S(7), slot - S(14), slot - S(14)),
                Vector2.Zero, 0f, Col(255, 255, 255));
            if (i == selected)
            {
                Raylib.DrawRectangleLines(x - 2 * one, y0 - 2 * one, slot + 4 * one, slot + 4 * one, Col(255, 255, 255));
                Raylib.DrawRectangleLines(x - one, y0 - one, slot + 2 * one, slot + 2 * one, Col(255, 255, 255));
            }
        }
        Raylib.DrawText($"Block: {BlockNames[selected]}", x0, y0 - S(26), S(20), Col(255, 255, 255));

        if (debugHud) DrawDebugHud(sw, sh, ui);
    }

    static void DrawDebugHud(int width, int height, float scale)
    {
        int font = (int)(16 * scale);
        int padding = (int)(5 * scale);
        int y = padding * 2;
        void Line(string text)
        {
            int textWidth = Raylib.MeasureText(text, font);
            Raylib.DrawRectangle(padding, y, textWidth + 2 * padding, font + padding, Col(12, 16, 20, 185));
            Raylib.DrawText(text, 2 * padding, y + padding / 2, font, Col(240, 245, 250));
            y += font + padding + 1;
        }

        int x = (int)MathF.Floor(pos.X), z = (int)MathF.Floor(pos.Z);
        int blockY = (int)MathF.Floor(pos.Y);
        int cx = FloorChunk(x), cz = FloorChunk(z);
        int meshed = 0, pending = 0, modified = 0;
        foreach (var chunk in chunks.Values)
        {
            if (chunk.HasMesh) meshed++;
            if (chunk.Dirty) pending++;
            if (chunk.Modified) modified++;
        }

        Line($"FPS: {Raylib.GetFPS()} | Frame: {Raylib.GetFrameTime() * 1000f:F1} ms");
        Line($"GPU: {gpuName}");
        Line($"Display: {width}x{height} @ {monitorHz} Hz | VSync: {Raylib.IsWindowState(ConfigFlags.VSyncHint)}");
        Line($"Shader: {(shaderOk ? currentShaderPack : "unavailable")} | Shadows: {(shadowsOn ? (softShadows ? "soft" : "hard") : "off")}");
        y += padding;

        Line($"XYZ: {pos.X:F2} / {pos.Y:F2} / {pos.Z:F2} | Block: {x}, {blockY}, {z}");
        Line($"Chunk: {cx}, {cz} | Local X/Z: {Mod(x, ChunkSize)}, {Mod(z, ChunkSize)} | Borders: {(showChunkBorders ? "on" : "off")}");
        string mode = flying ? "flying" : crouching ? "sneaking" : onGround ? "grounded" : "airborne";
        const float degreesPerRadian = 180f / MathF.PI;
        Line($"Player: {mode} | Speed: {vel.Length():F2} blocks/s | Yaw: {yaw * degreesPerRadian:F1} | Pitch: {pitch * degreesPerRadian:F1}");
        string target = "none";
        if (hasHit)
        {
            int slot = Array.IndexOf(Hotbar, Get(hitX, hitY, hitZ));
            if (slot >= 0) target = $"{BlockNames[slot]} at {hitX}, {hitY}, {hitZ}";
        }
        Line($"Target: {target}");
        y += padding;

        Line($"Chunks: {chunks.Count} loaded, {meshed} meshed, {pending} pending, {modified} edited");
        Line($"Render distance: {renderRadius} chunks | Chunk size: {ChunkSize}x{H}x{ChunkSize} | Seed: {Seed}");
        float hours = (6f + timeOfDay * 24f) % 24f;
        string session = !coopMatched ? "solo" : coopHost ? "co-op host" : "co-op guest";
        Line($"Time: {(int)hours:00}:{(int)((hours % 1f) * 60f):00}{(timePaused ? " (paused)" : "")} | Session: {session}");
    }

    static bool sliderDragging;
    static void DrawPauseAndStatus()
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        if (paused && !shaderMenuOpen && !coopPanel)
        {
            Raylib.DrawRectangle(0, 0, w, h, Col(0, 0, 0, 180));
            void Center(string text, int y, int size) => Raylib.DrawText(text,
                (w - Raylib.MeasureText(text, size)) / 2, y, size, Col(255, 255, 255));
            Center(coopMatched ? "GAME MENU" : "PAUSED", h / 2 - 152, 36);
            // Ползунок настройки дальности прорисовки
            int sliderW = 320, sliderH = 12;
            int sliderX = (w - sliderW) / 2;
            int sliderY = h / 2 - 72;
            Rectangle sliderBounds = new(sliderX, sliderY, sliderW, sliderH);
            Rectangle hitBounds = new(sliderX - 10, sliderY - 14, sliderW + 20, sliderH + 28);

            Vector2 mouse = Raylib.GetMousePosition();
            if (Raylib.IsMouseButtonPressed(MouseButton.Left) && Raylib.CheckCollisionPointRec(mouse, hitBounds))
                sliderDragging = true;
            if (!Raylib.IsMouseButtonDown(MouseButton.Left))
                sliderDragging = false;

            if (sliderDragging)
            {
                float t = Math.Clamp((mouse.X - sliderX) / (float)sliderW, 0f, 1f);
                renderRadius = MinRenderRadius + (int)Math.Round(t * (MaxRenderRadius - MinRenderRadius));
            }

            // Подпись и текущее значение дистанции
            string distText = $"Render distance: {renderRadius} chunks ({renderRadius * ChunkSize} blocks)";
            Raylib.DrawText(distText, (w - Raylib.MeasureText(distText, 20)) / 2, sliderY - 28, 20, Col(220, 240, 255));

            // Полоса ползунка
            Raylib.DrawRectangleRounded(sliderBounds, 0.5f, 4, Col(38, 44, 52));
            float fillRatio = (float)(renderRadius - MinRenderRadius) / (MaxRenderRadius - MinRenderRadius);
            int fillW = (int)(sliderW * fillRatio);
            if (fillW > 0)
                Raylib.DrawRectangleRounded(new Rectangle(sliderX, sliderY, fillW, sliderH), 0.5f, 4, Col(53, 137, 200));

            // Бегунок ползунка
            int handleX = sliderX + fillW;
            int handleW = 14, handleH = 22;
            Raylib.DrawRectangleRounded(new Rectangle(handleX - handleW / 2, sliderY - (handleH - sliderH) / 2, handleW, handleH), 0.3f, 4, Col(245, 250, 255));

            if (DrawMenuButton("Resume  [Esc]", PauseButton(0))) SetPaused(false);
            if (DrawMenuButton("Save world  [F5]", PauseButton(1))) SaveWorld();
            if (DrawMenuButton("Shaders  [S]", PauseButton(2))) OpenShaderMenu();
            if (DrawMenuButton("Steam co-op  [M]", PauseButton(3))) coopPanel = true;
            if (DrawMenuButton("Save and quit  [Q]", PauseButton(4))) quitRequested = true;
        }
        if (Raylib.GetTime() < statusUntil)
            Raylib.DrawText(status, (w - Raylib.MeasureText(status, 20)) / 2, h - 135, 20, Col(255, 240, 150));
    }
}
