using Raylib_cs;
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

    const byte Air = 0, Grass = 1, Dirt = 2, Stone = 3, Sand = 4, Wood = 5, Leaves = 6, Planks = 7, Bricks = 8, Cobblestone = 9;
    const int SandLevel = 10 + AddedDepth;                   // ниже этой высоты — пляж (песок)

    const uint Seed = 1337;

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
    static int LocalIndex(int x, int y, int z) => Mod(x, ChunkSize) + ChunkSize * (Mod(z, ChunkSize) + ChunkSize * y);
    static byte Get(int x, int y, int z)
    {
        if (y < 0 || y >= H) return Air;
        return chunks.TryGetValue((FloorChunk(x), FloorChunk(z)), out var chunk) ? chunk.Blocks[LocalIndex(x,y,z)] : Air;
    }
    static void SetBlock(int x, int y, int z, byte b)
    {
        if (y < 0 || y >= H || !chunks.TryGetValue((FloorChunk(x), FloorChunk(z)), out var chunk)) return;
        chunk.Blocks[LocalIndex(x,y,z)] = b;
        chunk.Modified = true;
        MarkNeighbors(chunk.X, chunk.Z);
        SetOcc(x,y,z,b != Air);
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
        public byte[] Blocks = new byte[ChunkSize * ChunkSize * H];
        public Mesh Mesh;
        public bool HasMesh, Dirty = true, Modified;
    }
    static readonly Dictionary<(int X, int Z), Chunk> chunks = new();
    static readonly Dictionary<(int X, int Z), byte[]> savedChunks = new();
    static int Mod(int v, int n) => (v % n + n) % n;
    static int FloorChunk(int v) => (v - Mod(v, ChunkSize)) / ChunkSize;
    static int ChunkDistance((int X, int Z) key) => Math.Max(
        Math.Abs(key.X - (int)MathF.Floor(pos.X / ChunkSize)),
        Math.Abs(key.Z - (int)MathF.Floor(pos.Z / ChunkSize)));
    static IEnumerable<(int X, int Z)> NearbyChunkIndices(int radius)
    {
        int cx = (int)MathF.Floor(pos.X / ChunkSize), cz = (int)MathF.Floor(pos.Z / ChunkSize);
        return Enumerable.Range(cx-radius, radius*2+1)
            .SelectMany(x => Enumerable.Range(cz-radius, radius*2+1).Select(z => (X:x,Z:z)))
            .OrderBy(ChunkDistance);
    }
    static void MarkNeighbors(int x, int z)
    {
        for (int dx=-1;dx<=1;dx++)
        for (int dz=-1;dz<=1;dz++)
            if (chunks.TryGetValue((x+dx,z+dz), out var c)) c.Dirty = true;
    }
    static void UpdateOccupancyOrigin()
    {
        int x = ((int)MathF.Floor(pos.X / ChunkSize) - ChunksX/2) * ChunkSize;
        int z = ((int)MathF.Floor(pos.Z / ChunkSize) - ChunksZ/2) * ChunkSize;
        if (x == occX && z == occZ) return;
        occX = x; occZ = z;
        BuildOccupancy();
    }
    static void StreamChunks()
    {
        UpdateOccupancyOrigin();
        foreach (var key in NearbyChunkIndices(1)) GenerateChunk(key.X,key.Z);
        foreach (var key in NearbyChunkIndices(renderRadius))
        {
            if (chunks.ContainsKey(key)) continue;
            GenerateChunk(key.X,key.Z);
            break;
        }
        foreach (var pair in chunks.Where(p => ChunkDistance(p.Key) > renderRadius+2 &&
            (!coopMatched || !coopHost || friend == null ||
                Math.Abs(p.Key.X-(int)MathF.Floor(friend.X/ChunkSize)) > 2 ||
                Math.Abs(p.Key.Z-(int)MathF.Floor(friend.Z/ChunkSize)) > 2)).ToArray())
        {
            if (pair.Value.Modified) savedChunks[pair.Key] = pair.Value.Blocks;
            if (pair.Value.HasMesh) Raylib.UnloadMesh(pair.Value.Mesh);
            chunks.Remove(pair.Key);
            MarkNeighbors(pair.Key.X,pair.Key.Z);
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
        var chunk = new Chunk { X = cx, Z = cz };
        if (savedChunks.TryGetValue((cx,cz), out var saved))
        {
            chunk.Blocks = (byte[])saved.Clone();
            chunks[(cx,cz)] = chunk;
            MarkNeighbors(cx,cz);
            CopyChunkOccupancy(chunk);
            return;
        }
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
        for (int y = 0; y < H; y++)
        for (int z = cz * ChunkSize; z < (cz + 1) * ChunkSize; z++)
        for (int x = cx * ChunkSize; x < (cx + 1) * ChunkSize; x++)
        {
            byte block = target[Index(x, y, z)];
            chunk.Blocks[LocalIndex(x,y,z)] = block;
            SetOcc(x, y, z, block != Air);
        }
        chunks[(cx,cz)] = chunk;
        MarkNeighbors(cx,cz);
    }
    static void CopyChunkOccupancy(Chunk chunk)
    {
        for (int y=0;y<H;y++)
        for (int z=0;z<ChunkSize;z++)
        for (int x=0;x<ChunkSize;x++)
            SetOcc(chunk.X*ChunkSize+x,y,chunk.Z*ChunkSize+z,
                chunk.Blocks[x+ChunkSize*(z+ChunkSize*y)] != Air);
    }
}
