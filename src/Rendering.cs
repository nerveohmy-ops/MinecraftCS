using Raylib_cs;
using System.Diagnostics;
using System.Numerics;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    const int MaxVertsPerMesh = 65532;          // предел 16-битных индексов (ushort), кратно 4

    const int TileSize = 32, TileCount = 11;     // атлас 352x32, Faithful 32x

    static Texture2D atlas;
    static Material material;

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

    static readonly string[] PackTiles = {
        "grass_block_top", "grass_block_side", "dirt", "stone", "sand",
        "oak_log", "oak_log_top", "oak_leaves", "oak_planks", "bricks", "cobblestone"
    };

    static void ApplyBlockTextures(ref Image atlasImage)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "Faithful");
        for (int tile = 0; tile < PackTiles.Length; tile++)
        {
            string path = Path.Combine(directory, PackTiles[tile] + ".png");
            if (!File.Exists(path)) continue;
            Image source = Raylib.LoadImage(path);
            if (source.Data == null) continue;
            // Keep every pixel of the supplied 32x32 pack without downsampling.
            if (source.Width != TileSize || source.Height != TileSize)
            {
                Raylib.UnloadImage(source);
                continue;
            }
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                Color color = Raylib.GetImageColor(source, x, y);
                if (tile is 0 or 7)
                {
                    color.R = (byte)(color.R * (tile == 0 ? 0.56f : 0.48f));
                    color.G = (byte)(color.G * (tile == 0 ? 0.94f : 0.88f));
                    color.B = (byte)(color.B * (tile == 0 ? 0.32f : 0.36f));
                }
                // Leaves remain opaque to match the world's solid-block face culling.
                Color background = tile == 7 ? Col(54, 100, 35) : Raylib.GetImageColor(atlasImage, tile * TileSize + x, y);
                int alpha = color.A;
                color = Col((color.R * alpha + background.R * (255 - alpha)) / 255,
                    (color.G * alpha + background.G * (255 - alpha)) / 255,
                    (color.B * alpha + background.B * (255 - alpha)) / 255);
                Raylib.ImageDrawPixel(ref atlasImage, tile * TileSize + x, y, color);
            }
            Raylib.UnloadImage(source);
        }
        string overlayPath = Path.Combine(directory, "grass_block_side_overlay.png");
        if (!File.Exists(overlayPath) || !File.Exists(Path.Combine(directory, "grass_block_side.png"))) return;
        Image overlay = Raylib.LoadImage(overlayPath);
        if (overlay.Data == null) return;
        if (overlay.Width == TileSize && overlay.Height == TileSize)
        {
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                Color mask = Raylib.GetImageColor(overlay, x, y);
                Color background = Raylib.GetImageColor(atlasImage, TileSize + x, y);
                int alpha = mask.A;
                Color result = Col(((int)(mask.R * 0.56f) * alpha + background.R * (255 - alpha)) / 255,
                    ((int)(mask.G * 0.94f) * alpha + background.G * (255 - alpha)) / 255,
                    ((int)(mask.B * 0.32f) * alpha + background.B * (255 - alpha)) / 255);
                Raylib.ImageDrawPixel(ref atlasImage, TileSize + x, y, result);
            }
        }
        Raylib.UnloadImage(overlay);
    }

    // Static contact lighting belongs to geometry, not to every screen pixel.
    static byte VertexAO(int x, int y, int z, int face, int corner)
    {
        int nx=FaceDir[face,0], ny=FaceDir[face,1], nz=FaceDir[face,2];
        int axisA = nx == 0 ? 0 : 1;
        int axisB = nz == 0 ? 2 : 1;
        int a = FaceVerts[face,corner,axisA] == 0 ? -1 : 1;
        int b = FaceVerts[face,corner,axisB] == 0 ? -1 : 1;
        int ax=axisA==0?a:0, ay=axisA==1?a:0;
        int by=axisB==1?b:0, bz=axisB==2?b:0;
        x+=nx; y+=ny; z+=nz;
        int sideA=Get(x+ax,y+ay,z)!=Air?1:0;
        int sideB=Get(x,y+by,z+bz)!=Air?1:0;
        int diagonal=Get(x+ax,y+ay+by,z+bz)!=Air?1:0;
        float ao=sideA*sideB==1?0.48f:1f-(sideA+sideB+diagonal)*0.16f;
        return (byte)MathF.Round(255*ao);
    }

    // ════════════════════════ СОЛНЦЕ, ТЕНИ, ШЕЙДЕР ════════════════════════
    // Тени — честные вокселные: фрагментный шейдер пускает луч от каждой точки к солнцу (DDA) по карте
    // занятости мира. Два слоя по 32 уровня лежат друг под другом.
    const int OccW = W * 32, OccH = D * (H / 32);
    const float DayLength = 300f;          // секунд на полные сутки

    static readonly byte[] occ = new byte[OccW * OccH * 4];
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
            return texelFetch(texture1, ivec2(c.x + WX * (c.y % 32), c.z + WZ * (c.y / 32)), 0).r > 0.5;
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
        int o = ((z + D * (y / 32)) * OccW + x + W * (y % 32)) * 4;
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
        Image img = Raylib.GenImageColor(OccW, OccH, Col(0, 0, 0, 255));
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

    static bool showChunkBorders;

    static void DrawChunkBorders()
    {
        int cx = FloorChunk((int)MathF.Floor(pos.X));
        int cz = FloorChunk((int)MathF.Floor(pos.Z));
        int y = Math.Clamp((int)MathF.Floor(pos.Y / ChunkSize) * ChunkSize, 0, H - ChunkSize);
        Color neighbor = Col(95, 195, 255, 190);
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dz == 0 || !chunks.ContainsKey((cx + dx, cz + dz))) continue;
            Vector3 min = new((cx + dx) * ChunkSize, y, (cz + dz) * ChunkSize);
            Raylib.DrawBoundingBox(new BoundingBox(min, min + new Vector3(ChunkSize)), neighbor);
        }

        Vector3 current = new(cx * ChunkSize, y, cz * ChunkSize);
        Raylib.DrawBoundingBox(new BoundingBox(current, current + new Vector3(ChunkSize)), Col(255, 216, 80));
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
        if (showChunkBorders) DrawChunkBorders();
        DrawCoopFriend();
        DrawBlockParticles();
        Raylib.EndMode3D();

        if (!diagnosticRender) DrawFirstPersonHand();

        DrawHud();   // 2D поверх 3D-сцены
        if (inventoryOpen) DrawInventory();
        DrawPauseAndStatus();
        if (coopPanel) DrawCoopMenu();
        if (shaderMenuOpen) DrawShaderMenu();
        Raylib.EndDrawing();
    }
}
