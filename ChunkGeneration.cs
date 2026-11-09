namespace MinecraftCS;

internal static unsafe partial class Program
{
    // Настраиваемая дальность прорисовки (регулируется ползунком в паузе Esc)
    const int MinRenderRadius = 2;              // минимум 2 чанка (32 блока)
    const int MaxRenderRadius = 8;              // максимум 8 чанков (128 блоков)
    static int renderRadius = 5;                // текущий радиус видимости чанков (по умолчанию 5 = 80 блоков)
    sealed class Chunk
    {
        public int X, Z;
        public byte[] Blocks = new byte[ChunkSize * ChunkSize * H];
        public Raylib_cs.Mesh Mesh;
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
            if (pair.Value.HasMesh) Raylib_cs.Raylib.UnloadMesh(pair.Value.Mesh);
            chunks.Remove(pair.Key);
            MarkNeighbors(pair.Key.X,pair.Key.Z);
        }
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
            float h = 13f + (n - 0.5f) * 22f;
            int depth = 0;
            bool sandColumn = false;

            for (int y = H - 1; y >= 0; y--)
            {
                bool solid = y == 0;
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
                target[(x - x0) + width * ((z - z0) + depthZ * y)] = b;
            }

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
