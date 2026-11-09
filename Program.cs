using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Raylib_cs;

namespace MinecraftCS;

// MinecraftCS — воксельная песочница на C# + Raylib-cs 6.1.0 (OpenGL 3.3).
// Генерация, движение и сохранения вынесены в отдельные части Program.
internal static unsafe partial class Program
{
    // ════════════════════════ ДАННЫЕ И КОНСТАНТЫ ════════════════════════
    // Размер чанка расширен с 8x8 до 16x16 (по высоте — весь мир H = 32)
    const int ChunkSize = 16;                   // чанк 16x16 (по высоте — весь мир)
    const int ChunksX = 12, ChunksZ = 12;       // размер подвижного окна теней и прежнего формата сохранений
    const int W = ChunkSize * ChunksX;          // ширина окна теней (16 * 12 = 192)
    const int D = ChunkSize * ChunksZ;          // глубина окна теней (16 * 12 = 192)
    const int H = 32;                           // высота мира
    const int MaxVertsPerMesh = 65532;          // предел 16-битных индексов (ushort), кратно 4

    const byte Air = 0, Grass = 1, Dirt = 2, Stone = 3, Sand = 4, Wood = 5, Leaves = 6, Planks = 7, Bricks = 8, Cobblestone = 9;
    const int SandLevel = 10;                   // ниже этой высоты — пляж (песок)
    const int TileSize = 32, TileCount = 11;     // атлас 352x32, Faithful 32x
    const uint Seed = 1337;





    // Окно и видео
    const int WindowW = 1280, WindowH = 720;
    static string gpuName = "unknown";

    // Физика игрока
    const float Gravity = 30f, JumpSpeed = 8.6f;
    const float WalkSpeed = 4.3f, RunSpeed = 6.5f;
    const float HalfW = 0.3f;
    static float Height => crouching ? 1.5f : 1.8f;
    static float EyeHeight => crouching ? 1.32f : 1.62f;
    const float Eps = 0.0001f;
    const float ReachDist = 7f;                 // дальность рейкаста
    const float MouseSens = 0.0022f;

    static Texture2D atlas;
    static Material material;

    static Vector3 pos;                         // pos.Y — уровень ног
    static Vector3 vel;
    static bool onGround;
    static float yaw, pitch;
    static int frame;
    static bool diagnosticRender;

    // Хотбар на 9 слотов
    static readonly byte[] Hotbar = { Grass, Dirt, Stone, Sand, Wood, Leaves, Planks, Bricks, Cobblestone };
    static readonly string[] BlockNames = { "Grass", "Dirt", "Stone", "Sand", "Wood", "Leaves", "Planks", "Bricks", "Cobblestone" };
    static readonly int[] HotbarIcon = { 0, 2, 3, 4, 5, 7, 8, 9, 10 };   // тайл для иконки
    static int selected;

    // Результат рейкаста
    static bool hasHit;
    static int hitX, hitY, hitZ, prevX, prevY, prevZ;

    // Тайлы блоков: [блок, (верх, бок, низ)]
    static readonly int[,] Tiles =
    {
        { 0, 0, 0 },   // воздух
        { 0, 1, 2 },   // трава
        { 2, 2, 2 },   // земля
        { 3, 3, 3 },   // камень
        { 4, 4, 4 },   // песок
        { 6, 5, 6 },   // дерево
        { 7, 7, 7 },   // листва
        { 8, 8, 8 },   // доски
        { 9, 9, 9 },   // кирпич
        { 10, 10, 10 }, // булыжник
    };

    // Грани: +X, -X, +Y, -Y, +Z, -Z (вершины против часовой стрелки, если смотреть снаружи)
    static readonly int[,] FaceDir =
    {
        { 1, 0, 0 }, { -1, 0, 0 }, { 0, 1, 0 }, { 0, -1, 0 }, { 0, 0, 1 }, { 0, 0, -1 },
    };
    static readonly int[,,] FaceVerts =
    {
        { { 1, 0, 1 }, { 1, 0, 0 }, { 1, 1, 0 }, { 1, 1, 1 } },   // +X
        { { 0, 0, 0 }, { 0, 0, 1 }, { 0, 1, 1 }, { 0, 1, 0 } },   // -X
        { { 0, 1, 1 }, { 1, 1, 1 }, { 1, 1, 0 }, { 0, 1, 0 } },   // +Y
        { { 0, 0, 0 }, { 1, 0, 0 }, { 1, 0, 1 }, { 0, 0, 1 } },   // -Y
        { { 0, 0, 1 }, { 1, 0, 1 }, { 1, 1, 1 }, { 0, 1, 1 } },   // +Z
        { { 1, 0, 0 }, { 0, 0, 0 }, { 0, 1, 0 }, { 1, 1, 0 } },   // -Z
    };
    static readonly int[,] QuadUV = { { 0, 1 }, { 1, 1 }, { 1, 0 }, { 0, 0 } };

    static readonly List<float> vBuf = new(), uvBuf = new(), nBuf = new();

    static readonly List<byte> aoBuf = new();

    static Color Col(int r, int g, int b, int a = 255) => new Color((byte)r, (byte)g, (byte)b, (byte)a);

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

    // ════════════════════════ ТЕКСТУРЫ (атлас 128x16) ════════════════════════
    // Тайлы: 0 трава-верх, 1 трава-бок, 2 земля, 3 камень, 4 песок, 5 дерево-бок, 6 дерево-торец, 7 листва
    static void Paint(out int r, out int g, out int b, int br, int bg, int bb, int amp, float f)
    {
        int d = (int)((f - 0.5f) * 2f * amp);
        r = Math.Clamp(br + d, 0, 255);
        g = Math.Clamp(bg + d, 0, 255);
        b = Math.Clamp(bb + d, 0, 255);
    }

    static void TileColor(int t, int x, int y, out int r, out int g, out int b)
    {
        uint hv = Hash(x, y, t + 100);
        float f = (hv % 1000) / 999f;
        switch (t)
        {
            case 0: Paint(out r, out g, out b, 95, 159, 53, 14, f); break;
            case 1:
                if (y < 3 + (int)(Hash(x, 0, 77) % 2)) Paint(out r, out g, out b, 95, 159, 53, 14, f);
                else Paint(out r, out g, out b, 134, 96, 67, 14, f);
                break;
            case 2: Paint(out r, out g, out b, 134, 96, 67, 14, f); break;
            case 3:
                if (hv % 11 == 0) Paint(out r, out g, out b, 100, 100, 100, 10, f);
                else Paint(out r, out g, out b, 125, 125, 125, 12, f);
                break;
            case 4: Paint(out r, out g, out b, 219, 207, 155, 8, f); break;
            case 5:
                if (x % 4 == 0) Paint(out r, out g, out b, 80, 62, 38, 5, f);
                else Paint(out r, out g, out b, 102, 81, 51, 6, f);
                break;
            case 6:
                if (x == 0 || y == 0 || x == 15 || y == 15) Paint(out r, out g, out b, 102, 81, 51, 6, f);
                else
                {
                    float dx = x - 7.5f, dy = y - 7.5f;
                    int ring = (int)MathF.Sqrt(dx * dx + dy * dy);
                    if (ring % 2 == 0) Paint(out r, out g, out b, 166, 134, 84, 6, f);
                    else Paint(out r, out g, out b, 140, 110, 66, 6, f);
                }
                break;
            case 8:
                if (y % 4 == 0 || (x + (y / 4 % 2) * 8) % 16 == 0)
                    Paint(out r, out g, out b, 100, 72, 40, 5, f);
                else Paint(out r, out g, out b, 171, 132, 76, 10, f);
                break;
            case 9:
                if (y % 4 == 0 || (x + (y / 4 % 2) * 4) % 8 == 0)
                    Paint(out r, out g, out b, 176, 166, 150, 5, f);
                else Paint(out r, out g, out b, 157, 72, 52, 16, f);
                break;
            case 10:
                if (y % 6 == 0 || (x + (y / 6 % 2) * 3) % 7 == 0)
                    Paint(out r, out g, out b, 65, 65, 65, 8, f);
                else Paint(out r, out g, out b, 116, 117, 112, 18, f);
                break;
            default:
                if (hv % 5 == 0) Paint(out r, out g, out b, 28, 80, 22, 10, f);
                else Paint(out r, out g, out b, 46, 118, 36, 18, f);
                break;
        }
    }

    static Texture2D MakeAtlas()
    {
        int aw = TileSize * TileCount;
        Image img = Raylib.GenImageColor(aw, TileSize, Col(0, 0, 0, 0));   // RGBA8, память выделена самой Raylib
        byte* px = (byte*)img.Data;
        for (int t = 0; t < TileCount; t++)
        for (int y = 0; y < TileSize; y++)
        for (int x = 0; x < TileSize; x++)
        {
            TileColor(t, x * 16 / TileSize, y * 16 / TileSize, out int r, out int g, out int b);
            int o = (y * aw + t * TileSize + x) * 4;
            px[o] = (byte)r; px[o + 1] = (byte)g; px[o + 2] = (byte)b; px[o + 3] = 255;
        }
        ApplyBlockTextures(ref img);
        Texture2D tex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        Raylib.SetTextureFilter(tex, TextureFilter.Point);   // пиксельный стиль без размытия
        return tex;
    }

    // ════════════════════════ МЕШИ ЧАНКОВ ════════════════════════
    static void BuildChunk(int cx, int cz)
    {
        var chunk = chunks[(cx, cz)];
        if (chunk.HasMesh) { Raylib.UnloadMesh(chunk.Mesh); chunk.HasMesh = false; }

        vBuf.Clear(); uvBuf.Clear(); nBuf.Clear(); aoBuf.Clear();
        int vc = 0;
        const float e = 0.002f;   // отступ от краёв тайла, чтобы соседние текстуры не «просачивались»

        for (int y = 0; y < H; y++)
        for (int lz = 0; lz < ChunkSize; lz++)
        for (int lx = 0; lx < ChunkSize; lx++)
        {
            int x = cx * ChunkSize + lx, z = cz * ChunkSize + lz;
            byte b = Get(x, y, z);
            if (b == Air) continue;

            for (int f = 0; f < 6; f++)
            {
                // Face culling: рисуем грань, только если за ней воздух
                if (Get(x + FaceDir[f, 0], y + FaceDir[f, 1], z + FaceDir[f, 2]) != Air) continue;
                if (vc + 4 > MaxVertsPerMesh) goto Done;   // защита от переполнения ushort-индексов

                int tile = Tiles[b, f == 2 ? 0 : f == 3 ? 2 : 1];
                for (int k = 0; k < 4; k++)
                {
                    vBuf.Add(x + FaceVerts[f, k, 0]);
                    vBuf.Add(y + FaceVerts[f, k, 1]);
                    vBuf.Add(z + FaceVerts[f, k, 2]);
                    uvBuf.Add((tile + (QuadUV[k, 0] == 0 ? e : 1f - e)) / TileCount);
                    uvBuf.Add(QuadUV[k, 1] == 0 ? e : 1f - e);
                    nBuf.Add(FaceDir[f, 0]); nBuf.Add(FaceDir[f, 1]); nBuf.Add(FaceDir[f, 2]);
                    byte ao = VertexAO(x,y,z,f,k);
                    aoBuf.Add(ao); aoBuf.Add(ao); aoBuf.Add(ao); aoBuf.Add(255);
                }
                vc += 4;
            }
        }
    Done:
        if (vc == 0) return;

        Mesh mesh = new Mesh();
        mesh.VertexCount = vc;
        mesh.TriangleCount = vc / 2;
        // Память для Raylib — только через Raylib.New, иначе UnloadMesh сломает кучу (Access Violation 0xC0000005)
        mesh.Vertices = Raylib.New<float>((uint)(vc * 3));
        mesh.TexCoords = Raylib.New<float>((uint)(vc * 2));
        mesh.Normals = Raylib.New<float>((uint)(vc * 3));
        mesh.Colors = Raylib.New<byte>((uint)(vc * 4));
        for (int i = 0; i < vc * 4; i++) mesh.Colors[i] = aoBuf[i];
        mesh.Indices = Raylib.New<ushort>((uint)(vc / 4 * 6));

        for (int i = 0; i < vc * 3; i++) mesh.Vertices[i] = vBuf[i];
        for (int i = 0; i < vc * 2; i++) mesh.TexCoords[i] = uvBuf[i];
        for (int i = 0; i < vc * 3; i++) mesh.Normals[i] = nBuf[i];
        for (int q = 0; q < vc / 4; q++)
        {
            int vi = q * 4, ii = q * 6;
            mesh.Indices[ii] = (ushort)vi;
            mesh.Indices[ii + 1] = (ushort)(vi + 1);
            mesh.Indices[ii + 2] = (ushort)(vi + 2);
            mesh.Indices[ii + 3] = (ushort)vi;
            mesh.Indices[ii + 4] = (ushort)(vi + 2);
            mesh.Indices[ii + 5] = (ushort)(vi + 3);
        }

        Raylib.UploadMesh(&mesh, false);   // загрузка вершин в VRAM
        chunk.Mesh = mesh;
        chunk.HasMesh = true;
    }

    static void RebuildDirtyChunks(int budget = 3)
    {
        bool limitTime = budget != int.MaxValue;
        long started = Stopwatch.GetTimestamp();
        foreach (var key in NearbyChunkIndices(renderRadius))
        {
            if (!chunks.TryGetValue(key, out var chunk) || !chunk.Dirty) continue;
            chunk.Dirty = false;
            BuildChunk(key.X, key.Z);
            if (--budget == 0 || (limitTime && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 2)) break;
        }
    }

    static bool Solid(int x, int y, int z)
    {
        if (y < 0) return true;
        if (!chunks.ContainsKey((FloorChunk(x), FloorChunk(z)))) return true;
        return y < H && Get(x,y,z) != Air;
    }

    static bool Collides()
    {
        int x0 = (int)MathF.Floor(pos.X - HalfW), x1 = (int)MathF.Floor(pos.X + HalfW - Eps);
        int y0 = (int)MathF.Floor(pos.Y), y1 = (int)MathF.Floor(pos.Y + Height - Eps);
        int z0 = (int)MathF.Floor(pos.Z - HalfW), z1 = (int)MathF.Floor(pos.Z + HalfW - Eps);
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
            if (Solid(x, y, z)) return true;
        return false;
    }

    // Движение по осям по очереди — так получается скольжение вдоль стен
    static void MoveX(float d)
    {
        if (d == 0) return;
        if (crouching && onGround && !flying) d = SafeSneakStep(d, true);
        pos.X += d;
        if (!Collides()) return;
        pos.X = d > 0 ? MathF.Floor(pos.X + HalfW - Eps) - HalfW - 0.001f
                      : MathF.Floor(pos.X - HalfW) + 1f + HalfW + 0.001f;
    }

    static void MoveZ(float d)
    {
        if (d == 0) return;
        if (crouching && onGround && !flying) d = SafeSneakStep(d, false);
        pos.Z += d;
        if (!Collides()) return;
        pos.Z = d > 0 ? MathF.Floor(pos.Z + HalfW - Eps) - HalfW - 0.001f
                      : MathF.Floor(pos.Z - HalfW) + 1f + HalfW + 0.001f;
    }

    static void MoveY(float d)
    {
        pos.Y += d;
        if (!Collides()) { onGround = false; return; }
        if (d < 0) { pos.Y = MathF.Floor(pos.Y) + 1f; onGround = true; }
        else pos.Y = MathF.Floor(pos.Y + Height - Eps) - Height - 0.001f;
        vel.Y = 0;
    }

    static void UpdatePlayer(float dt)
    {
        UpdateMovementMode();
        SimulatePlayer(dt, ReadCoopKeys());
    }

    static void SimulatePlayer(float dt, int keys)
    {
        float fx = MathF.Cos(yaw), fz = MathF.Sin(yaw);   // вперёд
        float rx = -fz, rz = fx;                          // вправо
        float mx = 0, mz = 0;
        if ((keys & 1) != 0) { mx += fx; mz += fz; }
        if ((keys & 2) != 0) { mx -= fx; mz -= fz; }
        if ((keys & 4) != 0) { mx += rx; mz += rz; }
        if ((keys & 8) != 0) { mx -= rx; mz -= rz; }
        float len = MathF.Sqrt(mx * mx + mz * mz);
        if (len > 0) { mx /= len; mz /= len; }

        float speed = crouching ? 1.4f : (keys & 16) != 0 ? RunSpeed : WalkSpeed;
        if (flying) speed *= 2f;
        vel.X = mx * speed;
        vel.Z = mz * speed;
        if (flying)
        {
            vel.Y = ((keys & 32) != 0 ? speed : 0) -
                    ((keys & 64) != 0 ? speed : 0);
            onGround = false;
        }
        if (!flying && (keys & 32) != 0 && onGround) { vel.Y = JumpSpeed; onGround = false; }

        // Мелкие шаги, чтобы быстрое падение не «пролетало» сквозь блоки
        int steps = Math.Max(1, (int)MathF.Ceiling(dt / 0.01f));
        float sub = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            if (!flying) vel.Y = MathF.Max(vel.Y - Gravity * sub, -50f);
            MoveX(vel.X * sub);
            MoveZ(vel.Z * sub);
            MoveY(vel.Y * sub);
            if (flying && pos.Y > H + 16) { pos.Y = H + 16; vel.Y = 0; }
        }
    }

    static bool IntersectsPlayer(int bx, int by, int bz) =>
        bx + 1 > pos.X - HalfW && bx < pos.X + HalfW &&
        by + 1 > pos.Y && by < pos.Y + Height &&
        bz + 1 > pos.Z - HalfW && bz < pos.Z + HalfW;

    // ════════════════════════ RAYCAST (Amanatides & Woo) ════════════════════════
    static Vector3 LookDir() => new(MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Sin(yaw) * MathF.Cos(pitch));

    static bool Raycast(Vector3 o, Vector3 dir, float maxDist)
    {
        int x = (int)MathF.Floor(o.X), y = (int)MathF.Floor(o.Y), z = (int)MathF.Floor(o.Z);
        int sx = dir.X > 0 ? 1 : -1, sy = dir.Y > 0 ? 1 : -1, sz = dir.Z > 0 ? 1 : -1;
        float tdx = dir.X != 0 ? MathF.Abs(1f / dir.X) : float.PositiveInfinity;
        float tdy = dir.Y != 0 ? MathF.Abs(1f / dir.Y) : float.PositiveInfinity;
        float tdz = dir.Z != 0 ? MathF.Abs(1f / dir.Z) : float.PositiveInfinity;
        float tx = dir.X != 0 ? (dir.X > 0 ? x + 1 - o.X : o.X - x) * tdx : float.PositiveInfinity;
        float ty = dir.Y != 0 ? (dir.Y > 0 ? y + 1 - o.Y : o.Y - y) * tdy : float.PositiveInfinity;
        float tz = dir.Z != 0 ? (dir.Z > 0 ? z + 1 - o.Z : o.Z - z) * tdz : float.PositiveInfinity;

        prevX = x; prevY = y; prevZ = z;
        float t = 0;
        while (t <= maxDist)
        {
            if (Get(x, y, z) != Air) { hitX = x; hitY = y; hitZ = z; return true; }
            prevX = x; prevY = y; prevZ = z;
            if (tx < ty && tx < tz) { x += sx; t = tx; tx += tdx; }
            else if (ty < tz) { y += sy; t = ty; ty += tdy; }
            else { z += sz; t = tz; tz += tdz; }
        }
        return false;
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

        // Прицел
        Raylib.DrawRectangle(cx - S(10), cy - one, S(20), S(3), Col(0, 0, 0, 160));
        Raylib.DrawRectangle(cx - one, cy - S(10), S(3), S(20), Col(0, 0, 0, 160));
        Raylib.DrawRectangle(cx - S(9), cy, S(18), one, Col(255, 255, 255));
        Raylib.DrawRectangle(cx, cy - S(9), one, S(18), Col(255, 255, 255));

        string mode = flying ? "CREATIVE - FLYING (Space up / Ctrl down)" : crouching ? "SNEAKING" : "CREATIVE (F - flight / Ctrl - sneak)";
        Raylib.DrawText(mode, S(10), sh - S(32), S(16), Col(255, 255, 255));
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

        // Информация: FPS, видеокарта, дисплей
        bool integrated = gpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        bool vsync = Raylib.IsWindowState(ConfigFlags.VSyncHint);
        int ty = S(10), line = S(24);
        Raylib.DrawText($"FPS: {Raylib.GetFPS()} (uncapped) | Chunks: {chunks.Count}   XYZ: {pos.X:F1} {pos.Y:F1} {pos.Z:F1}", S(10), ty, S(20), Col(255, 255, 255));
        ty += line;
        Raylib.DrawText($"GPU: {gpuName}", S(10), ty, S(18), integrated ? Col(255, 170, 60) : Col(120, 255, 120));
        ty += line;
        Raylib.DrawText($"Display: {sw}x{sh} @ {monitorHz} Hz | VSync {(vsync ? "ON" : "OFF")} (V) | Fullscreen (F11)", S(10), ty, S(16), Col(255, 255, 255, 220));
        ty += line;
        float hours = (6f + timeOfDay * 24f) % 24f;
        int hh = (int)hours, mm = (int)((hours - hh) * 60f);
        Raylib.DrawText($"Time: {hh:00}:{mm:00}{(timePaused ? " (paused)" : "")} (T) | Shadows {(shadowsOn ? "ON" : "OFF")} (G) | Soft {(softShadows ? "ON" : "OFF")} (O){(shaderOk ? "" : " | SHADER FAILED - see console")}", S(10), ty, S(16), Col(255, 255, 255, 220));
        ty += line;
        if (integrated)
        {
            Raylib.DrawText("Integrated GPU in use! Windows Settings > System > Display > Graphics > MinecraftCS.exe > High performance",
                S(10), ty, S(16), Col(255, 170, 60));
            ty += line;
        }
        Raylib.DrawText("WASD move | Shift run | Space jump | LMB break | RMB place | 1-9 / wheel select | Esc pause | F5 save",
            S(10), ty, S(16), Col(255, 255, 255, 200));
    }

    // ════════════════════════ СОЛНЦЕ, ТЕНИ, ШЕЙДЕР ════════════════════════
    // Тени — честные вокселные: фрагментный шейдер пускает луч от каждой точки к солнцу (DDA) по карте
    // занятости мира. Карта лежит в текстуре 3072x96: воксель (x, y, z) -> пиксель (x + W*y, z).
    const int OccW = W * H;
    const float DayLength = 300f;          // секунд на полные сутки

    static readonly byte[] occ = new byte[OccW * D * 4];
    static bool occDirty;
    static Texture2D occTex;
    static Shader worldShader;
    static bool shaderOk;
    static int locSunDir, locSunColor, locAmbient, locShadows, locOccOrigin, locEye, locFog, locSoft, locFogNear, locFogFar;
    static bool softShadows = true;
    static int occX, occZ;

    static float timeOfDay = 0.15f;        // 0 восход, 0.25 полдень, 0.5 закат, 0.75 полночь
    static bool timePaused;
    static bool shadowsOn = true;
    static Vector3 sunDir = new(0.575f, 0.728f, -0.372f);
    static Vector3 sunColor = new(0.8f, 0.77f, 0.7f);
    static Vector3 ambientColor = new(0.38f, 0.42f, 0.5f);
    static Color skyColor = Col(135, 206, 235);

    const string VertexShaderSrc = """
        #version 330
        in vec3 vertexPosition;
        in vec2 vertexTexCoord;
        in vec3 vertexNormal;
        in vec4 vertexColor;
        out float fragAO;
        uniform mat4 mvp;
        out vec3 fragPos;
        out vec2 fragUV;
        flat out vec3 fragNormal;
        void main()
        {
            fragPos = vertexPosition;   // меши лежат в мировых координатах
            fragUV = vertexTexCoord;
            fragNormal = vertexNormal;
            fragAO = vertexColor.r;
            gl_Position = mvp * vec4(vertexPosition, 1.0);
        }
        """;

    const string FragmentShaderSrc = """
        #version 330
        in float fragAO;
        in vec3 fragPos;
        in vec2 fragUV;
        flat in vec3 fragNormal;
        uniform sampler2D texture0;   // атлас блоков
        uniform sampler2D texture1;   // карта занятости мира
        uniform vec3 sunDir;          // направление НА солнце
        uniform vec3 sunColor;
        uniform vec3 ambientColor;
        uniform float shadowsOn;
        uniform vec3 occOrigin;
        uniform vec3 eyePosition;
        uniform vec3 fogColor;
        uniform float softShadows;
        uniform float fogNear;
        uniform float fogFar;
        out vec4 finalColor;

        const int WX = @WX@;
        const int WY = @WY@;
        const int WZ = @WZ@;


        const int MAX_STEPS = 128;

        float safeDir(float v) { return abs(v) < 1e-5 ? (v < 0.0 ? -1e-5 : 1e-5) : v; }

        bool occupied(ivec3 c)
        {
            if (any(lessThan(c, ivec3(0))) || any(greaterThanEqual(c, ivec3(WX,WY,WZ)))) return false;
            return texelFetch(texture1, ivec2(c.x + WX * c.y, c.z), 0).r > 0.5;
        }

        // 1.0 — солнце видно, 0.0 — точка в тени
        float sunVisibility(vec3 origin, vec3 dir)
        {
            vec3 d = vec3(safeDir(dir.x), safeDir(dir.y), safeDir(dir.z));
            ivec3 cell = ivec3(floor(origin));
            ivec3 stepDir = ivec3(d.x > 0.0 ? 1 : -1, d.y > 0.0 ? 1 : -1, d.z > 0.0 ? 1 : -1);
            vec3 tDelta = abs(1.0 / d);
            vec3 edge = vec3(cell) + vec3(d.x > 0.0 ? 1.0 : 0.0, d.y > 0.0 ? 1.0 : 0.0, d.z > 0.0 ? 1.0 : 0.0);
            vec3 tMax = (edge - origin) / d;
            for (int i = 0; i < MAX_STEPS; i++)
            {
                if (tMax.x < tMax.y && tMax.x < tMax.z) { cell.x += stepDir.x; tMax.x += tDelta.x; }
                else if (tMax.y < tMax.z)               { cell.y += stepDir.y; tMax.y += tDelta.y; }
                else                                    { cell.z += stepDir.z; tMax.z += tDelta.z; }
                if (cell.y >= WY || cell.x < 0 || cell.x >= WX || cell.z < 0 || cell.z >= WZ) return 1.0;
                if (cell.y >= 0 && occupied(cell)) return 0.0;
            }
            return 1.0;
        }

        float filteredShadow(vec3 p, float distanceXZ)
        {
            // Preserve detailed soft edges nearby; one ray is enough in the haze.
            // Мягкие тени рядом с игроком, далее одного луча достаточно
            if (softShadows < 0.5 || distanceXZ > 35.0) return sunVisibility(p,sunDir);
            vec3 tangent = normalize(cross(sunDir,abs(sunDir.y) < 0.95 ? vec3(0,1,0) : vec3(1,0,0)));
            vec3 bitangent = cross(sunDir,tangent);
            // Fixed area-light samples: no animated noise or temporal flicker.
            float radius = 0.025;
            float v = sunVisibility(p,normalize(sunDir + tangent*radius));
            v += sunVisibility(p,normalize(sunDir + (-0.5*tangent + 0.8660254*bitangent)*radius));
            v += sunVisibility(p,normalize(sunDir + (-0.5*tangent - 0.8660254*bitangent)*radius));
            return v / 3.0;
        }
        void main()
        {
            float distanceXZ = length((fragPos-eyePosition).xz);
            if (distanceXZ >= fogFar) { finalColor = vec4(fogColor,1.0); return; }
            // Voxel normals are exact axes. Do not normalize then truncate to integers:
            // GPU reciprocal-square-root rounding can turn 1 into 0.99999994.
            vec3 n = round(fragNormal);
            vec3 p = fragPos - occOrigin;
            float ndl = max(dot(n, sunDir), 0.0);
            float vis = 1.0;
            if (shadowsOn > 0.5 && ndl > 0.0 && dot(sunColor, vec3(1.0)) > 0.01)
                vis = filteredShadow(p + n * 0.025,distanceXZ);
            float ao = fragAO;
            float hemisphere = 0.66 + 0.34 * max(n.y,0.0);
            vec3 bounce = vec3(0.055,0.043,0.028) * (1.0 - max(n.y,0.0));
            vec3 light = (ambientColor * hemisphere + bounce) * ao + sunColor * ndl * vis;
            vec3 albedo = pow(texture(texture0,fragUV).rgb,vec3(2.2));
            vec3 mapped = vec3(1.0) - exp(-albedo * light * 1.65);
            vec3 color = pow(mapped,vec3(1.0/2.2));
            float fog = smoothstep(fogNear,fogFar,distanceXZ);
            finalColor = vec4(mix(color,fogColor,fog),1.0);
        }
        """;

    static void SetOcc(int x, int y, int z, bool solid)
    {
        x -= occX; z -= occZ;
        if (x < 0 || x >= W || z < 0 || z >= D || y < 0 || y >= H) return;
        int o = (z * OccW + x + W * y) * 4;
        byte v = (byte)(solid ? 255 : 0);
        occ[o] = v; occ[o + 1] = v; occ[o + 2] = v; occ[o + 3] = 255;
        occDirty = true;
    }

    static void BuildOccupancy()
    {
        for (int y = 0; y < H; y++)
        for (int z = 0; z < D; z++)
        for (int x = 0; x < W; x++)
            SetOcc(x + occX, y, z + occZ, Get(x + occX, y, z + occZ) != Air);
    }

    static void CreateOccupancyTexture()
    {
        Image img = Raylib.GenImageColor(OccW, D, Col(0, 0, 0, 255));
        occTex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        Raylib.SetTextureFilter(occTex, TextureFilter.Point);
        occDirty = true;
        FlushOccupancy();
    }

    // Отправляем карту занятости в видеопамять, только если мир менялся (сломали / поставили блок)
    static void FlushOccupancy()
    {
        if (!occDirty) return;
        occDirty = false;
        fixed (byte* px = occ) Raylib.UpdateTexture(occTex, px);
    }

    static void InitLighting()
    {
        string fs = FragmentShaderSrc
            .Replace("@WX@", W.ToString()).Replace("@WY@", H.ToString()).Replace("@WZ@", D.ToString());


        worldShader = Raylib.LoadShaderFromMemory(VertexShaderSrc, fs);
        shaderOk = worldShader.Id != 0;
        if (!shaderOk) return;   // шейдер не собрался — играем без освещения (ошибка видна в консоли)

        locEye = Raylib.GetShaderLocation(worldShader, "eyePosition");
        locFog = Raylib.GetShaderLocation(worldShader, "fogColor");
        locFogNear = Raylib.GetShaderLocation(worldShader, "fogNear");
        locFogFar = Raylib.GetShaderLocation(worldShader, "fogFar");
        locSoft = Raylib.GetShaderLocation(worldShader, "softShadows");
        locOccOrigin = Raylib.GetShaderLocation(worldShader, "occOrigin");
        locSunDir = Raylib.GetShaderLocation(worldShader, "sunDir");
        locSunColor = Raylib.GetShaderLocation(worldShader, "sunColor");
        locAmbient = Raylib.GetShaderLocation(worldShader, "ambientColor");
        locShadows = Raylib.GetShaderLocation(worldShader, "shadowsOn");
        material.Shader = worldShader;
        // DrawMesh сам привяжет эти текстуры к texture0 / texture1
        material.Maps[(int)MaterialMapIndex.Metalness].Texture = occTex;
    }

    static float SmoothStep(float e0, float e1, float x) => Smooth(Math.Clamp((x - e0) / (e1 - e0), 0f, 1f));

    // Ход солнца: восток -> полдень -> запад -> ночь. От высоты солнца зависят цвет света и неба.
    static void UpdateSun(float dt)
    {
        if (!timePaused) timeOfDay = (timeOfDay + dt / DayLength) % 1f;
        float a = timeOfDay * MathF.PI * 2f;
        sunDir = Vector3.Normalize(new Vector3(MathF.Cos(a), MathF.Sin(a) * 0.92f, -0.38f));

        float e = sunDir.Y;
        float day = SmoothStep(-0.05f, 0.25f, e);         // 0 ночью, 1 днём
        float warm = 1f - SmoothStep(0.05f, 0.45f, e);    // 1 у горизонта (рассвет / закат)

        sunColor = Vector3.Lerp(new Vector3(1.00f, 0.96f, 0.88f), new Vector3(1.00f, 0.52f, 0.25f), warm) * (0.85f * day);
        ambientColor = Vector3.Lerp(new Vector3(0.14f, 0.16f, 0.28f), new Vector3(0.38f, 0.42f, 0.50f), day);

        Vector3 sky = Vector3.Lerp(new Vector3(0.03f, 0.04f, 0.10f), new Vector3(0.53f, 0.81f, 0.92f), day);
        float glow = MathF.Max(0f, 1f - MathF.Abs(e) / 0.3f) * 0.55f;
        sky = Vector3.Lerp(sky, new Vector3(1.0f, 0.55f, 0.30f), glow);
        skyColor = Col((int)(sky.X * 255f), (int)(sky.Y * 255f), (int)(sky.Z * 255f));
    }

    static void ApplyLighting()
    {
        if (!shaderOk) return;
        Vector3 eye = pos + new Vector3(0,EyeHeight,0);
        Vector3 fog = new(skyColor.R/255f,skyColor.G/255f,skyColor.B/255f);
        float soft = softShadows ? 1 : 0;
        Raylib.SetShaderValue(worldShader, locEye, &eye, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(worldShader, locFog, &fog, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(worldShader, locSoft, &soft, ShaderUniformDataType.Float);
        // Динамический расчет дистанции тумана под текущую дальность прорисовки (renderRadius)
        float fNear = renderRadius * ChunkSize * 0.70f;
        float fFar = renderRadius * ChunkSize - 4.0f;
        Raylib.SetShaderValue(worldShader, locFogNear, &fNear, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(worldShader, locFogFar, &fFar, ShaderUniformDataType.Float);
        float gt = (float)Raylib.GetTime();
        if (locGameTime != -1)
            Raylib.SetShaderValue(worldShader, locGameTime, &gt, ShaderUniformDataType.Float);
        Vector3 origin = new(occX, 0, occZ);
        Raylib.SetShaderValue(worldShader, locOccOrigin, &origin, ShaderUniformDataType.Vec3);
        Vector3 d = sunDir, c = sunColor, a = ambientColor;
        float on = shadowsOn ? 1f : 0f;
        Raylib.SetShaderValue(worldShader, locSunDir, &d, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(worldShader, locSunColor, &c, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(worldShader, locAmbient, &a, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(worldShader, locShadows, &on, ShaderUniformDataType.Float);
    }

    // Солнце и луна — квадраты в небе (как в Minecraft). Рисуются до 3D-сцены, поэтому горы их закрывают.
    static void DrawCelestials(Camera3D cam, Vector3 look)
    {
        int sh = Raylib.GetScreenHeight();
        DrawBody(cam, look, sunDir, sh * 0.10f, Col(255, 244, 176), Col(255, 214, 110, 50));
        DrawBody(cam, look, -sunDir, sh * 0.07f, Col(226, 230, 240), Col(170, 190, 255, 35));
    }

    static void DrawBody(Camera3D cam, Vector3 look, Vector3 dir, float size, Color core, Color halo)
    {
        if (dir.Y < -0.08f || Vector3.Dot(dir, look) < 0.15f) return;   // под горизонтом или за спиной
        Vector2 s = Raylib.GetWorldToScreen(cam.Position + dir * 100f, cam);
        int cx = (int)s.X, cy = (int)s.Y, hs = (int)(size * 2.4f), cs = (int)size;
        Raylib.DrawRectangle(cx - hs / 2, cy - hs / 2, hs, hs, halo);
        Raylib.DrawRectangle(cx - cs / 2, cy - cs / 2, cs, cs, core);
    }

    // ════════════════════════ ВИДЕОКАРТА И ЭКРАН ════════════════════════
    [DllImport("opengl32.dll", EntryPoint = "glGetString")]
    static extern IntPtr GlGetString(uint name);

    static int monitorHz;

    // Имя GPU, который реально создал OpenGL-контекст (0x1F01 = GL_RENDERER)
    static string QueryGpuName()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return "unknown";
            IntPtr ptr = GlGetString(0x1F01);
            return ptr == IntPtr.Zero ? "unknown" : Marshal.PtrToStringAnsi(ptr) ?? "unknown";
        }
        catch { return "unknown"; }
    }

    // Windows сам выбирает GPU для каждой программы (то же, что «Параметры → Система → Дисплей → Графика →
    // Высокая производительность»). Записываем это предпочтение для MinecraftCS.exe и один раз
    // перезапускаемся, чтобы окно сразу создавалось на дискретной карте (Nvidia / AMD).
    // Отключить: запуск с аргументом --no-gpu-setup.
    static void EnsureDiscreteGpu(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Contains("--no-gpu-setup") || args.Contains("--gpu-ready")) return;
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "MinecraftCS.exe");
            if (!File.Exists(exe)) return;

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            string cur = key.GetValue(exe) as string ?? "";
            var parts = cur.Split(';', StringSplitOptions.RemoveEmptyEntries)
                           .Where(x => !x.StartsWith("GpuPreference=")).ToList();
            parts.Add("GpuPreference=2");
            string want = string.Join(";", parts) + ";";
            bool changed = cur != want;
            if (changed) key.SetValue(exe, want, RegistryValueKind.String);

            bool isThatExe = string.Equals(Environment.ProcessPath, exe, StringComparison.OrdinalIgnoreCase);
            if (isThatExe && !changed) return;   // уже запущены как MinecraftCS.exe с нужной настройкой

            ProcessStartInfo psi = new(exe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--gpu-ready");
            using Process child = Process.Start(psi);
            if (child == null) return;
            child.WaitForExit();
            Environment.Exit(child.ExitCode);
        }
        catch { /* не вышло — играем на том GPU, который выбрал Windows */ }
    }

    static void ToggleFullscreenMode()
    {
        if (!Raylib.IsWindowFullscreen())
        {
            int m = Raylib.GetCurrentMonitor();
            Raylib.SetWindowSize(Raylib.GetMonitorWidth(m), Raylib.GetMonitorHeight(m));
            Raylib.ToggleFullscreen();
        }
        else
        {
            Raylib.ToggleFullscreen();
            Raylib.SetWindowSize(WindowW, WindowH);
        }
        monitorHz = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
        frame = 0;   // пропустить «скачок» мыши после смены режима
    }

    static void ToggleVSync()
    {
        if (Raylib.IsWindowState(ConfigFlags.VSyncHint)) Raylib.ClearWindowState(ConfigFlags.VSyncHint);
        else Raylib.SetWindowState(ConfigFlags.VSyncHint);
    }

    // ════════════════════════ ГЛАВНЫЙ ЦИКЛ ════════════════════════
    static void Update(float dt)
    {
        PollCoop(dt);
        if (coopPanel) { UpdateCoopMenu(); return; }
        if (shaderMenuOpen) { UpdateShaderMenu(); return; }
        if (Raylib.IsKeyPressed(KeyboardKey.Escape) || (!Raylib.IsWindowFocused() && !paused))
        {
            paused = !paused;
            if (paused) Raylib.EnableCursor();
            else { Raylib.DisableCursor(); frame = 0; }
        }
        if (Raylib.IsKeyPressed(KeyboardKey.F5)) SaveWorld();
        if (paused)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.S)) { shaderMenuOpen = true; ScanShaderPacks(); }
            if (Raylib.IsKeyPressed(KeyboardKey.M)) coopPanel = true;
            if (Raylib.IsKeyPressed(KeyboardKey.Q)) quitRequested = true;
            return;
        }
        autosaveTimer += dt;
        if (autosaveTimer >= 30f) { SaveWorld(); autosaveTimer = 0; }
        bool altEnter = (Raylib.IsKeyDown(KeyboardKey.LeftAlt) || Raylib.IsKeyDown(KeyboardKey.RightAlt))
                        && Raylib.IsKeyPressed(KeyboardKey.Enter);
        if (Raylib.IsKeyPressed(KeyboardKey.F11) || altEnter) ToggleFullscreenMode();
        if (Raylib.IsKeyPressed(KeyboardKey.V)) ToggleVSync();
        if (Raylib.IsKeyPressed(KeyboardKey.T)) timePaused = !timePaused;
        if (Raylib.IsKeyPressed(KeyboardKey.O)) softShadows = !softShadows;
        if (Raylib.IsKeyPressed(KeyboardKey.G)) shadowsOn = !shadowsOn;

        frame++;
        Vector2 md = Raylib.GetMouseDelta();
        if (frame > 3 && !diagnosticRender)   // первые кадры после DisableCursor дают «скачок» мыши
        {
            yaw += md.X * MouseSens;
            pitch = Math.Clamp(pitch - md.Y * MouseSens, -1.55f, 1.55f);
        }

        for (int i = 0; i < Hotbar.Length; i++)
            if (Raylib.IsKeyPressed((KeyboardKey)((int)KeyboardKey.One + i))) selected = i;
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0) selected = ((selected - Math.Sign(wheel)) % Hotbar.Length + Hotbar.Length) % Hotbar.Length;

        StreamChunks();
        if (Raylib.IsMouseButtonPressed(MouseButton.Left) || Raylib.IsMouseButtonPressed(MouseButton.Right))
            swingTime = 0.26f;
        if (!coopMatched || coopHost) { UpdatePlayer(dt); UpdateSun(dt); }
        else SendPlayerInput();
        UpdateAnimations(dt);

        Vector3 eye = new(pos.X, pos.Y + EyeHeight, pos.Z);
        hasHit = Raycast(eye, LookDir(), ReachDist);
        if (hasHit && (!coopMatched || coopHost))
        {
            if (Raylib.IsMouseButtonPressed(MouseButton.Middle))
                PickBlock(Get(hitX, hitY, hitZ));
            if (Raylib.IsMouseButtonPressed(MouseButton.Left) && hitY > 0)
                CommitCoopBlock(hitX, hitY, hitZ, Air);
            else if (Raylib.IsMouseButtonPressed(MouseButton.Right) &&
                     Get(prevX, prevY, prevZ) == Air && !IntersectsPlayer(prevX, prevY, prevZ))
                CommitCoopBlock(prevX, prevY, prevZ, Hotbar[selected]);
        }

        RebuildDirtyChunks();
        FlushOccupancy();
    }

    static void Draw()
    {
        Camera3D cam = new()
        {
            Position = new Vector3(pos.X, pos.Y + EyeHeight, pos.Z) + AnimationCameraOffset(),
            Up = new Vector3(0, 1, 0),
            FovY = 70f,
            Projection = CameraProjection.Perspective,
        };
        cam.Target = cam.Position + LookDir();

        Vector3 look = LookDir();

        Raylib.BeginDrawing();
        Raylib.ClearBackground(skyColor);
        ApplyLighting();                 // параметры солнца -> шейдер
        DrawCelestials(cam, look);       // солнце и луна

        Raylib.BeginMode3D(cam);
        foreach (var chunk in chunks.Values)
        {
            if (!chunk.HasMesh || ChunkDistance((chunk.X,chunk.Z)) > renderRadius) continue;
            Vector3 center = new Vector3(chunk.X * ChunkSize + ChunkSize / 2f, H / 2f,
                                         chunk.Z * ChunkSize + ChunkSize / 2f) - cam.Position;
            // Для чанка 16x16 радиус описанной сферы ~19.6, порог отсечения увеличен до -24f
            if (Vector3.Dot(center, look) < -24f) continue;
            Raylib.DrawMesh(chunk.Mesh, material, Matrix4x4.Identity);
        }
        if (hasHit)   // подсветка блока под прицелом
            Raylib.DrawCubeWiresV(new Vector3(hitX + 0.5f, hitY + 0.5f, hitZ + 0.5f),
                new Vector3(1.004f, 1.004f, 1.004f), Col(0, 0, 0));
        DrawCoopFriend();
        DrawBlockParticles();
        Raylib.EndMode3D();

        if (!diagnosticRender) DrawFirstPersonHand();

        DrawHud();   // 2D поверх 3D-сцены
        DrawPauseAndStatus();
        if (coopPanel) DrawCoopMenu();
        if (shaderMenuOpen) DrawShaderMenu();
        Raylib.EndDrawing();
    }

    static void Main(string[] args)
    {
        if (args.Contains("--self-test")) { RunSelfTests(); return; }
        if (args.Contains("--steam-check"))
        {
            InitCoop();
            Console.WriteLine(coopStatus);
            if (steamReady) Steamworks.SteamAPI.Shutdown();
            else Environment.ExitCode = 2;
            return;
        }
        diagnosticRender = args.Contains("--benchmark") || args.Contains("--render-test");
        bool benchmark = args.Contains("--benchmark");
        bool renderTest = args.Contains("--render-test") || benchmark;
        if (!renderTest) EnsureDiscreteGpu(args);   // обязательно до создания окна и контекста OpenGL

        if (!renderTest) InitCoop();
        Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint);
        Raylib.InitWindow(WindowW, WindowH, "MinecraftCS");
        if (!renderTest) InitAmbientAudio();
        Raylib.SetExitKey(KeyboardKey.Null);
        Raylib.SetTargetFPS(0); // без ограничения FPS
        gpuName = QueryGpuName();
        monitorHz = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
        if (args.Contains("--fullscreen") || args.Contains("-f")) ToggleFullscreenMode();

        atlas = MakeAtlas();
        material = Raylib.LoadMaterialDefault();
        material.Maps[(int)MaterialMapIndex.Albedo].Texture = atlas;

        GenerateWorld();
        SpawnPlayer();
        if (!renderTest) LoadWorld();
        foreach (var key in NearbyChunkIndices(2)) GenerateChunk(key.X, key.Z);
        UpdateOccupancyOrigin();
        UpdateSun(0);
        BuildOccupancy();
        CreateOccupancyTexture();
        InitLighting();
        InitShaderManager();
        RebuildDirtyChunks(int.MaxValue);
        if (renderTest) { pitch = -0.65f; shadowsOn = benchmark; }
        if (benchmark)
        {
            foreach (var key in NearbyChunkIndices(renderRadius)) GenerateChunk(key.X,key.Z);
            RebuildDirtyChunks(int.MaxValue);
        }
        double benchSeconds = 0;
        int benchFrames = 0;
        Raylib.DisableCursor();

        ulong launchLobby = ParseLobbyArgument(args);
        if (!renderTest && launchLobby != 0)
        {
            if (steamReady) JoinCoop(new Steamworks.CSteamID(launchLobby));
            else { paused = coopPanel = true; Raylib.EnableCursor(); }
        }
#if STEAM_TEST
        else if (!renderTest && args.Contains("--host-steam"))
        {
            paused = coopPanel = true;
            Raylib.EnableCursor();
            CreateCoop();
        }
#endif

        while (!Raylib.WindowShouldClose() && !quitRequested)
        {
            Update(MathF.Min(Raylib.GetFrameTime(), 0.05f));
            UpdateAmbientAudio(MathF.Min(Raylib.GetFrameTime(), 0.05f));
            UpdateGameplayAudio();
            Draw();
            if (benchmark && frame > 60) { benchSeconds += Raylib.GetFrameTime(); benchFrames++; }
            if (renderTest && frame >= (benchmark ? 300 : 40))
            {
                Raylib.TakeScreenshot(Path.Combine(Environment.CurrentDirectory,"lighting-preview.png"));
                if (benchmark) Console.WriteLine($"BENCHMARK: {benchSeconds * 1000 / benchFrames:F2} ms/frame, {benchFrames / benchSeconds:F1} FPS");
                Console.WriteLine($"RENDER CHECK: shader={shaderOk}, GPU={gpuName}");
                if (!shaderOk) Environment.ExitCode = 1;
                break;
            }
        }

        if (!renderTest) SaveWorld();
        if (steamReady)
        {
            EndCoop("", closing:true);
            inviteCallback?.Dispose();
            connectionCallback?.Dispose();
            createResult?.Dispose();
            joinResult?.Dispose();
            Steamworks.SteamAPI.Shutdown();
        }
        foreach (var chunk in chunks.Values)
            if (chunk.HasMesh) Raylib.UnloadMesh(chunk.Mesh);
        Raylib.UnloadTexture(atlas);
        Raylib.UnloadTexture(occTex);
        if (dirtUiLoaded) Raylib.UnloadTexture(dirtUiTex);
        if (shaderOk) Raylib.UnloadShader(worldShader);
        Raylib.CloseWindow();
        CloseAmbientAudio();
    }
}
