using MinecraftCS.Graphics;
using System.Numerics;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    const int ChunkSize = 16;                   // чанк 16x16 (по высоте — весь мир)
    const int ChunksX = 12, ChunksZ = 12;       // размер подвижного окна теней и прежнего формата сохранений
    const int W = ChunkSize * ChunksX;          // ширина окна теней (16 * 12 = 192)
    const int D = ChunkSize * ChunksZ;          // глубина окна теней (16 * 12 = 192)
    const int LegacyHeight = 32, H = 64;
    const int AddedDepth = H - LegacyHeight;

    const byte Air = 0, Grass = 1, Dirt = 2, Stone = 3, Sand = 4, Wood = 5, Leaves = 6, Planks = 7, Bricks = 8, Cobblestone = 9, CoalOre = 10, IronOre = 11, GoldOre = 12;
    const int SandLevel = 10 + AddedDepth;                   // ниже этой высоты — пляж (песок)

    const uint DefaultSeed = 1337;
    static uint Seed = DefaultSeed;

    // ════════════════════════ ШУМ (Value Noise) ════════════════════════
    static uint Hash(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791) ^ 0x9E3779B9u ^ Seed;
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            return h;
        }
    }

    static float Rand(int x, int y, int z) => (Hash(x, y, z) & 0xFFFF) / 65535f;
    static float Smooth(float t) => t * t * (3f - 2f * t);
    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    static float Noise2(float x, float z, int octave)
    {
        int xi = (int)MathF.Floor(x), zi = (int)MathF.Floor(z);
        float u = Smooth(x - xi), v = Smooth(z - zi);
        return Lerp(Lerp(Rand(xi, octave, zi), Rand(xi + 1, octave, zi), u),
                    Lerp(Rand(xi, octave, zi + 1), Rand(xi + 1, octave, zi + 1), u), v);
    }

    static float Noise3(float x, float y, float z)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y), zi = (int)MathF.Floor(z);
        float u = Smooth(x - xi), v = Smooth(y - yi), w = Smooth(z - zi);
        float Corner(int dx, int dy, int dz) => Rand(xi + dx + 5000, yi + dy, zi + dz);
        float a = Lerp(Lerp(Corner(0, 0, 0), Corner(1, 0, 0), u), Lerp(Corner(0, 1, 0), Corner(1, 1, 0), u), v);
        float b = Lerp(Lerp(Corner(0, 0, 1), Corner(1, 0, 1), u), Lerp(Corner(0, 1, 1), Corner(1, 1, 1), u), v);
        return Lerp(a, b, w);
    }

    // Сумма нескольких октав, результат 0..1
    static float Fbm2(float x, float z)
    {
        float sum = 0, amp = 0.5f, freq = 1f, norm = 0;
        for (int o = 0; o < 4; o++)
        {
            sum += Noise2(x * freq, z * freq, o) * amp;
            norm += amp; amp *= 0.5f; freq *= 2f;
        }
        return sum / norm;
    }

    // ════════════════════════ МИР И ГЕНЕРАЦИЯ ════════════════════════
    static int LocalIndex(int x, int y, int z) => ChunkLocal(x) + ChunkSize * (ChunkLocal(z) + ChunkSize * y);
    static byte Get(int x, int y, int z)
    {
        if (y < 0 || y >= H) return Air;
        return chunks.TryGetValue((FloorChunk(x), FloorChunk(z)), out var chunk) ? chunk.Blocks[LocalIndex(x,y,z)] : Air;
    }
    static void SetBlock(int x, int y, int z, byte b)
    {
        if (y < 0 || y >= H || !chunks.TryGetValue((FloorChunk(x), FloorChunk(z)), out var chunk)) return;
        int index = LocalIndex(x, y, z);
        byte previous = chunk.Blocks[index];
        if (previous == b) return;
        chunk.Blocks[index] = b;
        chunk.Modified = true;
        MarkDirtyChunks(FloorChunk(x - 1), FloorChunk(x + 1), FloorChunk(z - 1), FloorChunk(z + 1));
        if ((previous == Air) != (b == Air)) SetOcc(x, y, z, b != Air);
    }

    static int TopY(int x, int z)
    {
        for (int y = H - 1; y >= 0; y--)
            if (Get(x, y, z) != Air) return y;
        return -1;
    }

    static void GenerateWorld() { GenerateChunk(6, 6); }

    static void SpawnPlayer()
    {
        int sx = W / 2, sz = D / 2;
        pos = new Vector3(sx + 0.5f, TopY(sx, sz) + 1f, sz + 0.5f);
        vel = Vector3.Zero;
    }

    // Настраиваемая дальность прорисовки (регулируется ползунком в паузе Esc)
    const int MinRenderRadius = 2;              // минимум 2 чанка (32 блока)
    const int MaxRenderRadius = 8;              // максимум 8 чанков (128 блоков)
    static int renderRadius = 5;                // текущий радиус видимости чанков (по умолчанию 5 = 80 блоков)
    sealed class Chunk
    {
        public int X, Z;
        public byte[] Blocks;
        public List<OpenGlRenderBackend.ChunkMesh> Meshes;
        public bool Dirty = true, Modified;
    }
    static readonly Dictionary<(int X, int Z), Chunk> chunks = new();
    static readonly Dictionary<(int X, int Z), byte[]> savedChunks = new();
    // ChunkSize is a power of two; masks and signed shifts also work west/north of zero.
    static int ChunkLocal(int value) => value & (ChunkSize - 1);
    static int FloorChunk(int value) => value >> BitOperations.TrailingZeroCount((uint)ChunkSize);
    static (int X, int Z) PlayerChunk => ((int)MathF.Floor(pos.X / ChunkSize), (int)MathF.Floor(pos.Z / ChunkSize));

    static readonly (int X, int Z)[] nearbyOffsets =
        (from x in Enumerable.Range(-MaxRenderRadius, MaxRenderRadius * 2 + 1)
         from z in Enumerable.Range(-MaxRenderRadius, MaxRenderRadius * 2 + 1)
         orderby Math.Max(Math.Abs(x), Math.Abs(z))
         select (x, z)).ToArray();
    static readonly List<(int X, int Z)> chunksToEvict = new();

    static int ChunkDistance((int X, int Z) key, (int X, int Z) center) =>
        Math.Max(Math.Abs(key.X - center.X), Math.Abs(key.Z - center.Z));

    static IEnumerable<(int X, int Z)> NearbyChunkIndices(int radius)
    {
        var center = PlayerChunk;
        int width = radius * 2 + 1;
        for (int i = 0; i < width * width; i++)
            yield return (center.X + nearbyOffsets[i].X, center.Z + nearbyOffsets[i].Z);
    }

    static void MarkDirtyChunks(int minX, int maxX, int minZ, int maxZ)
    {
        for (int x = minX; x <= maxX; x++)
        for (int z = minZ; z <= maxZ; z++)
            if (chunks.TryGetValue((x, z), out var chunk)) chunk.Dirty = true;
    }

    static void MarkNeighbors(int x, int z) => MarkDirtyChunks(x - 1, x + 1, z - 1, z + 1);

    static void UpdateOccupancyOrigin()
    {
        var center = PlayerChunk;
        int x = (center.X - ChunksX / 2) * ChunkSize;
        int z = (center.Z - ChunksZ / 2) * ChunkSize;
        if (x == occX && z == occZ) return;
        occX = x; occZ = z;
        BuildOccupancy();
    }

    static void StreamChunks()
    {
        UpdateOccupancyOrigin();
        foreach (var key in NearbyChunkIndices(1)) GenerateChunk(key.X, key.Z);
        foreach (var key in NearbyChunkIndices(renderRadius))
        {
            if (chunks.ContainsKey(key)) continue;
            GenerateChunk(key.X, key.Z);
            break;
        }

        var center = PlayerChunk;
        bool keepGuestChunks = coopMatched && coopHost && friend != null;
        (int X, int Z) guestCenter = keepGuestChunks
            ? ((int)MathF.Floor(friend.X / ChunkSize), (int)MathF.Floor(friend.Z / ChunkSize)) : default;
        chunksToEvict.Clear();
        foreach (var key in chunks.Keys)
            if (ChunkDistance(key, center) > renderRadius + 2 &&
                (!keepGuestChunks || ChunkDistance(key, guestCenter) > 2))
                chunksToEvict.Add(key);

        foreach (var key in chunksToEvict)
        {
            var chunk = chunks[key];
            if (chunk.Modified) savedChunks[key] = chunk.Blocks;
            UnloadChunkMeshes(chunk);
            WriteChunkOccupancy(chunk, clear: true);
            chunks.Remove(key);
            MarkNeighbors(key.X, key.Z);
        }
    }

    static byte UndergroundBlock(int x, int y, int z)
    {
        if (y <= 1 || y >= AddedDepth - 2) return Stone;

        // Intersect two noise bands to form tunnels across chunk borders.
        float first = Noise3(x * 0.055f, y * 0.085f, z * 0.055f);
        if (MathF.Abs(first - 0.5f) > 0.095f) return Stone;
        float second = Noise3(x * 0.047f + 61f, y * 0.072f - 19f, z * 0.047f + 37f);
        return MathF.Abs(second - 0.5f) < 0.09f ? Air : Stone;
    }

    static void GenerateChunk(int cx, int cz)
    {
        if (chunks.ContainsKey((cx,cz))) return;
        var chunk = new Chunk
        {
            X = cx, Z = cz,
            Blocks = savedChunks.TryGetValue((cx, cz), out var saved) ? (byte[])saved.Clone() : GenerateChunkBlocks(cx, cz)
        };
        chunks[(cx, cz)] = chunk;
        MarkNeighbors(cx, cz);
        WriteChunkOccupancy(chunk);
    }

    static byte[] GenerateChunkBlocks(int cx, int cz)
    {
        // Generate a terrain halo to determine trees independently of load order.
        int x0 = cx * ChunkSize - 2, z0 = cz * ChunkSize - 2;
        int x1 = (cx + 1) * ChunkSize + 2, z1 = (cz + 1) * ChunkSize + 2;
        int width = x1 - x0, depthZ = z1 - z0;
        byte[] target = new byte[width * depthZ * H];
        int Index(int x, int y, int z) => x - x0 + width * (z - z0 + depthZ * y);
        for (int x = x0; x < x1; x++)
        for (int z = z0; z < z1; z++)
        {
            float n = Fbm2(x * 0.02f + 50f, z * 0.02f + 50f);
            float h = AddedDepth + 13f + (n - 0.5f) * 22f;
            int depth = 0;
            bool sandColumn = false;

            for (int y = H - 1; y >= AddedDepth; y--)
            {
                bool solid = y == AddedDepth;
                if (!solid)
                {
                    float dy = y - h;
                    if (dy < -2.5f) solid = true;
                    else if (dy <= 2.5f) solid = dy <= (Noise3(x * 0.11f, y * 0.11f, z * 0.11f) - 0.5f) * 5f;
                }

                byte b = Air;
                if (!solid) depth = 0;
                else
                {
                    if (depth == 0) { sandColumn = y <= SandLevel; b = sandColumn ? Sand : Grass; }   // биомы: пляж / равнина
                    else if (depth <= 3) b = sandColumn ? Sand : Dirt;
                    else b = Stone;
                    depth++;
                }
                target[Index(x, y, z)] = b;
            }
            for (int y = 0; y < AddedDepth; y++) target[Index(x, y, z)] = UndergroundBlock(x, y, z);
        }
        int[] tops = new int[width * depthZ];
        bool[] grassy = new bool[tops.Length];
        for (int x = x0; x < x1; x++)
        for (int z = z0; z < z1; z++)
        {
            int y = H - 1;
            while (y >= 0 && target[Index(x, y, z)] == Air) y--;
            tops[x - x0 + width * (z - z0)] = y;
            grassy[x - x0 + width * (z - z0)] = y >= 0 && target[Index(x, y, z)] == Grass;
        }
        for (int x = x0; x < x1; x++)
        for (int z = z0; z < z1; z++)
        {
            if (Hash(x, 7, z) % 55 != 0 || (x - W / 2) * (x - W / 2) + (z - D / 2) * (z - D / 2) < 36) continue;
            int y = tops[x - x0 + width * (z - z0)];
            if (!grassy[x - x0 + width * (z - z0)]) continue;
            int trunk = 4 + (int)(Hash(x, 8, z) % 2);
            if (y + trunk + 2 >= H) continue;
            for (int t = 1; t <= trunk; t++) target[Index(x, y + t, z)] = Wood;
            int top = y + trunk;
            for (int dy = -2; dy <= 1; dy++)
            {
                int radius = dy >= 0 ? 1 : 2;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int tx = x + dx, tz = z + dz;
                    if (tx < x0 || tx >= x1 || tz < z0 || tz >= z1) continue;
                    if (radius == 2 && Math.Abs(dx) == 2 && Math.Abs(dz) == 2 && Hash(tx, top + dy, tz) % 2 == 0) continue;
                    int index = Index(tx, top + dy, tz);
                    if (target[index] == Air) target[index] = Leaves;
                }
            }
        }
        byte[] blocks = new byte[ChunkSize * ChunkSize * H];
        for (int y = 0; y < H; y++)
        for (int z = 0; z < ChunkSize; z++)
        for (int x = 0; x < ChunkSize; x++)
        {
            int wx = cx * ChunkSize + x, wz = cz * ChunkSize + z;
            byte block = target[Index(wx, y, wz)];
            blocks[x + ChunkSize * (z + ChunkSize * y)] = block == Stone ? OreAt(wx, y, wz) : block;
        }
        return blocks;
    }

}
