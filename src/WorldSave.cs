using Raylib_cs;
using System.Numerics;
using System.Text.Json;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    static float autosaveTimer;
    static double statusUntil;
    static string status = "";
    static readonly WorldStorage worldStorage = new(Path.Combine(AppContext.BaseDirectory, "saves", "world.json"));
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
            (s.Height == H
                ? ValidBlocks(c.Blocks, ChunkSize * ChunkSize * H)
                : s.Height == 0 && (ValidBlocks(c.Blocks, ChunkSize * ChunkSize * LegacyHeight) ||
                                    ValidBlocks(c.Blocks, 8 * 8 * LegacyHeight)))) &&
            (s.Height == 0 || s.Height == H) &&
            s.Chunks.Select(c => (c.X,c.Z)).Distinct().Count() == s.Chunks.Count;
        return (s.Version == 1 || s.Version == 2) && s.Width == W && s.Height == LegacyHeight && s.Depth == D &&
            ValidBlocks(s.Blocks,W*LegacyHeight*D) && (s.Version == 1 || s.GeneratedChunks?.Length == ChunksX*ChunksZ);
    }

    static byte[] GetMigratedChunk(int cx, int cz)
    {
        if (savedChunks.TryGetValue((cx, cz), out var existing)) return existing;

        byte[] blocks = GenerateChunkBlocks(cx, cz);
        savedChunks[(cx, cz)] = blocks;
        return blocks;
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
        bool legacy = saved.Height != H;
        if (saved.Version == 3)
        {
            foreach (var c in saved.Chunks)
            {
                if (!legacy)
                {
                    savedChunks[(c.X, c.Z)] = c.Blocks;
                    continue;
                }

                int size = c.Blocks.Length == 8 * 8 * LegacyHeight ? 8 : ChunkSize;
                int wx = c.X * size, wz = c.Z * size;
                byte[] blocks = GetMigratedChunk(FloorChunk(wx), FloorChunk(wz));
                for (int y = 0; y < LegacyHeight; y++)
                for (int z = 0; z < size; z++)
                    Array.Copy(c.Blocks, size * (z + size * y),
                        blocks, LocalIndex(wx, y + AddedDepth, wz + z), size);
            }
        }
        else
        {
            for (int cz = 0; cz < ChunksZ; cz++)
            for (int cx = 0; cx < ChunksX; cx++)
            {
                if (saved.Version == 2 && !saved.GeneratedChunks[cx + cz * ChunksX]) continue;
                byte[] blocks = GetMigratedChunk(cx, cz);
                for (int y = 0; y < LegacyHeight; y++)
                for (int z = 0; z < ChunkSize; z++)
                    Array.Copy(saved.Blocks, cx * ChunkSize + W * (cz * ChunkSize + z + D * y),
                        blocks, LocalIndex(0, y + AddedDepth, z), ChunkSize);
            }
        }
        foreach (var chunk in chunks.Values) UnloadChunkMeshes(chunk);
        chunks.Clear();
        pos = new Vector3(saved.X, saved.Y + (legacy ? AddedDepth : 0), saved.Z);
        yaw=saved.Yaw; pitch=saved.Pitch; timeOfDay=saved.Time;
        selected=saved.Selected; flying=saved.Flying; crouching=saved.Crouching;
        foreach (var key in NearbyChunkIndices(1)) GenerateChunk(key.X,key.Z);
        if (Collides()) { GenerateChunk(ChunksX/2, ChunksZ/2); SpawnPlayer(); }
    }
    sealed class WorldStorage(string path)
    {
        readonly string backup = path + ".bak";
        bool preserveBackup;
        public bool Blocked { get; private set; }
        public bool Recovered => preserveBackup;

        static SavedWorld TryRead(string candidate, out bool unreadable)
        {
            unreadable = false;
            try
            {
                using var stream = File.OpenRead(candidate);
                var saved = JsonSerializer.Deserialize<SavedWorld>(stream);
                if (ValidSave(saved)) return saved;
                Console.Error.WriteLine($"Invalid world data: {candidate}");
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { Console.Error.WriteLine(ex.Message); }
            unreadable = true;
            return null;
        }

        public SavedWorld Read()
        {
            Blocked = preserveBackup = false;
            var saved = TryRead(path, out bool mainUnreadable);
            if (saved != null) return saved;

            saved = TryRead(backup, out bool backupUnreadable);
            preserveBackup = saved != null;
            Blocked = saved == null && (mainUnreadable || backupUnreadable);
            return saved;
        }

        public bool TryWrite(SavedWorld saved, out string error)
        {
            if (Blocked)
            {
                error = "No readable world save. Existing files were preserved.";
                return false;
            }
            if (!ValidSave(saved))
            {
                error = "World state is invalid.";
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary = path + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, saved);
                    stream.Flush(true);
                }
                // Do not rotate the broken primary over the backup that was just recovered.
                if (File.Exists(path)) File.Replace(temporary, path, preserveBackup ? null : backup);
                else File.Move(temporary, path);
                preserveBackup = false;
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(ex.Message);
                error = "Could not write world save. See console.";
                return false;
            }
        }
    }

    static void LoadWorld()
    {
        var saved = worldStorage.Read();
        if (saved != null)
        {
            RestoreWorld(saved);
            ShowStatus(worldStorage.Recovered ? "World loaded from backup" : "World loaded");
        }
        else if (worldStorage.Blocked) ShowStatus("World saves unreadable - files preserved");
    }

    static SavedWorld CaptureWorld()
    {
        foreach (var c in chunks.Values.Where(c => c.Modified)) savedChunks[(c.X,c.Z)] = (byte[])c.Blocks.Clone();
        return new SavedWorld
        {
            Version=3, Height=H, Chunks=savedChunks.Select(p => new ChunkSave { X=p.Key.X,Z=p.Key.Z,Blocks=p.Value }).ToList(),
            X=pos.X,Y=pos.Y,Z=pos.Z,Yaw=yaw,Pitch=pitch,Time=timeOfDay,Selected=selected,Flying=flying,Crouching=crouching
        };
    }
    static bool TrySaveActiveWorld(WorldStorage storage, out string error)
    {
        error = null;
        return soloWorld == null && storage.TryWrite(CaptureWorld(), out error);
    }

    static void SaveWorld()
    {
        if (TrySaveActiveWorld(worldStorage, out string error)) ShowStatus("World saved");
        else if (error != null) ShowStatus(error);
    }
}
