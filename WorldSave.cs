using System.Numerics;
using System.Text.Json;
using Raylib_cs;

namespace MinecraftCS;
internal static unsafe partial class Program
{
    static bool paused, quitRequested, saveBlocked;
    static float autosaveTimer;
    static double statusUntil;
    static string status = "";
    static readonly string SavePath = Path.Combine(AppContext.BaseDirectory, "saves", "world.json");
    internal sealed class ChunkSave
    {
        public int X { get; set; }
        public int Z { get; set; }
        public byte[] Blocks { get; set; }
    }
    internal sealed class SavedWorld
    {
        public int Version { get; set; } = 1;
        public int Width { get; set; }
        public int Height { get; set; }
        public int Depth { get; set; }
        public byte[] Blocks { get; set; }
        public bool[] GeneratedChunks { get; set; }
        public List<ChunkSave> Chunks { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public float Time { get; set; }
        public int Selected { get; set; }
        public bool Flying { get; set; }
        public bool Crouching { get; set; }
    }
    static void ShowStatus(string message) { status = message; statusUntil = Raylib.GetTime()+4; }
    static bool ValidBlocks(byte[] data, int size) => data != null && data.Length == size && data.All(b => b <= Cobblestone);
    static bool ValidSave(SavedWorld s)
    {
        if (s == null || !float.IsFinite(s.X) || !float.IsFinite(s.Z) ||
            MathF.Abs(s.X) > 100000000 || MathF.Abs(s.Z) > 100000000 ||
            !float.IsFinite(s.Y) || s.Y < 0 || s.Y > H+16 || !float.IsFinite(s.Yaw) ||
            !float.IsFinite(s.Pitch) || MathF.Abs(s.Pitch)>1.55f ||
            !float.IsFinite(s.Time) || s.Time<0 || s.Time>=1 || s.Selected<0 || s.Selected>=Hotbar.Length) return false;
        if (s.Version == 3) return s.Chunks != null && s.Chunks.All(c => c != null &&
            Math.Abs((long)c.X) <= 12500000 && Math.Abs((long)c.Z) <= 12500000 &&
            // Поддержка как новых чанков 16x16 (ChunkSize*ChunkSize*H), так и старых сохранений 8x8 (8*8*H)
            (ValidBlocks(c.Blocks,ChunkSize*ChunkSize*H) || ValidBlocks(c.Blocks,8*8*H))) &&
            s.Chunks.Select(c => (c.X,c.Z)).Distinct().Count() == s.Chunks.Count;
        return (s.Version == 1 || s.Version == 2) && s.Width == W && s.Height == H && s.Depth == D &&
            ValidBlocks(s.Blocks,W*H*D) && (s.Version == 1 || s.GeneratedChunks?.Length == ChunksX*ChunksZ);
    }
    static void RestoreWorld(SavedWorld saved)
    {
        ResetAnimations();
        StopAmbientSounds();
        ResetGameplayAudio();
        ambientDelay = 15;
        ambientProbe = 0;
        ambientSheltered = false;
        savedChunks.Clear();
        if (saved.Version == 3)
        {
            foreach (var c in saved.Chunks)
            {
                // Если чанк совпадает с текущим размером (16x16) — загружаем напрямую
                if (c.Blocks.Length == ChunkSize * ChunkSize * H)
                    savedChunks[(c.X, c.Z)] = c.Blocks;
                else if (c.Blocks.Length == 8 * 8 * H)
                {
                    // Обратная совместимость: переносим данные из старых чанков 8x8 в новые 16x16
                    for (int y = 0; y < H; y++)
                    for (int lz = 0; lz < 8; lz++)
                    for (int lx = 0; lx < 8; lx++)
                    {
                        byte b = c.Blocks[lx + 8 * (lz + 8 * y)];
                        if (b == Air) continue;
                        int wx = c.X * 8 + lx, wz = c.Z * 8 + lz;
                        int ncx = FloorChunk(wx), ncz = FloorChunk(wz);
                        if (!savedChunks.TryGetValue((ncx, ncz), out var nblocks))
                        {
                            nblocks = new byte[ChunkSize * ChunkSize * H];
                            savedChunks[(ncx, ncz)] = nblocks;
                        }
                        nblocks[LocalIndex(wx, y, wz)] = b;
                    }
                }
            }
        }
        else
        {
            for (int cz=0;cz<ChunksZ;cz++)
            for (int cx=0;cx<ChunksX;cx++)
            {
                if (saved.Version == 2 && !saved.GeneratedChunks[cx+cz*ChunksX]) continue;
                byte[] data = new byte[ChunkSize*ChunkSize*H];
                for (int y=0;y<H;y++)
                for (int z=0;z<ChunkSize;z++)
                for (int x=0;x<ChunkSize;x++)
                    data[x+ChunkSize*(z+ChunkSize*y)] = saved.Blocks[cx*ChunkSize+x+W*(cz*ChunkSize+z+D*y)];
                savedChunks[(cx,cz)] = data;
            }
        }
        chunks.Clear(); // Called before mesh creation.
        pos = new Vector3(saved.X,saved.Y,saved.Z);
        yaw=saved.Yaw; pitch=saved.Pitch; timeOfDay=saved.Time;
        selected=saved.Selected; flying=saved.Flying; crouching=saved.Crouching;
        foreach (var key in NearbyChunkIndices(1)) GenerateChunk(key.X,key.Z);
        if (Collides()) { GenerateChunk(ChunksX/2, ChunksZ/2); SpawnPlayer(); }
    }
    static void LoadWorld()
    {
        if (!File.Exists(SavePath)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<SavedWorld>(File.ReadAllText(SavePath));
            if (!ValidSave(saved)) throw new InvalidDataException("Invalid world data");
            RestoreWorld(saved);
            ShowStatus("World loaded");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            saveBlocked=true;
            ShowStatus("Save unreadable - original preserved");
            Console.Error.WriteLine(ex.Message);
        }
    }
    static SavedWorld CaptureWorld()
    {
        foreach (var c in chunks.Values.Where(c => c.Modified)) savedChunks[(c.X,c.Z)] = (byte[])c.Blocks.Clone();
        return new SavedWorld
        {
            Version=3, Chunks=savedChunks.Select(p => new ChunkSave { X=p.Key.X,Z=p.Key.Z,Blocks=p.Value }).ToList(),
            X=pos.X,Y=pos.Y,Z=pos.Z,Yaw=yaw,Pitch=pitch,Time=timeOfDay,Selected=selected,Flying=flying,Crouching=crouching
        };
    }
    static void SaveWorld()
    {
        if (soloWorld != null) return;
        if (saveBlocked) { ShowStatus("Saving disabled: preserve unreadable world.json"); return; }
        try
        {
            var saved=CaptureWorld();
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
            string temporary=SavePath+".tmp";
            using (var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))
            { JsonSerializer.Serialize(stream,saved); stream.Flush(true); }
            if (File.Exists(SavePath)) File.Replace(temporary,SavePath,SavePath+".bak");
            else File.Move(temporary,SavePath);
            ShowStatus("World saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowStatus("Save failed - see console"); Console.Error.WriteLine(ex.Message); }
    }
    static bool sliderDragging;
    static void DrawPauseAndStatus()
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        if (paused)
        {
            Raylib.DrawRectangle(0, 0, w, h, Col(0, 0, 0, 180));
            void Center(string text, int y, int size) => Raylib.DrawText(text,
                (w - Raylib.MeasureText(text, size)) / 2, y, size, Col(255, 255, 255));
            Center("PAUSED", h / 2 - 120, 36);
            // Ползунок настройки дальности прорисовки
            int sliderW = 320, sliderH = 12;
            int sliderX = (w - sliderW) / 2;
            int sliderY = h / 2 - 40;
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

            Center("Esc - Resume", h / 2 + 15, 22);
            Center("F5 - Save world", h / 2 + 47, 22);
            Center("Q - Save and quit", h / 2 + 79, 22);
            Center("M - Steam co-op", h / 2 + 111, 22);
        }
        if (Raylib.GetTime() < statusUntil)
            Raylib.DrawText(status, (w - Raylib.MeasureText(status, 20)) / 2, h - 135, 20, Col(255, 240, 150));
    }

}
