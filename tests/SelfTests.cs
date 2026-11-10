using System.Numerics;
using System.Text.Json;
namespace MinecraftCS;
internal static unsafe partial class Program
{
    static void RunSelfTests()
    {
        int count=0;
        void Check(bool ok,string name) { if (!ok) throw new InvalidOperationException(name); Console.WriteLine("PASS: "+name); count++; }
        // Проверка корректности маппинга отрицательных координат для размера чанка ChunkSize
        Check(FloorChunk(-1)==-1 && FloorChunk(-ChunkSize)==-1 && FloorChunk(-ChunkSize-1)==-2 && ChunkLocal(-1)==ChunkSize-1,"Negative coordinate mapping");
        GenerateWorld(); SpawnPlayer();
        Check(!Collides(),"Spawn clear");
        Check(H == 64 && pos.Y > 32, "World has room beneath the surface");
        bool hasCave = false, solidFloor = true;
        for (int cz = 5; cz <= 7; cz++)
        for (int cx = 5; cx <= 7; cx++)
        {
            GenerateChunk(cx, cz);
            byte[] blocks = chunks[(cx, cz)].Blocks;
            solidFloor &= blocks.Take(ChunkSize * ChunkSize).All(b => b != Air);
            hasCave |= blocks.Skip(ChunkSize * ChunkSize).Take((AddedDepth - 1) * ChunkSize * ChunkSize).Contains(Air);
        }
        Check(solidFloor, "Bedrock stays solid");
        Check(hasCave, "Generated underground contains caves");
        GenerateChunk(-1,-1); GenerateChunk(-2,-1);
        var expected=(byte[])chunks[(-1,-1)].Blocks.Clone();
        chunks.Clear(); GenerateChunk(-2,-1); GenerateChunk(-1,-1);
        Check(expected.SequenceEqual(chunks[(-1,-1)].Blocks),"Generation independent of load order");
        SetBlock(-1,20,-1,Bricks);
        Check(Get(-1,20,-1)==Bricks && Get(-ChunkSize-1,20,-1)!=Bricks,"Negative coordinate edits stay in correct chunk");
        pos=new Vector3(-1,25,-1);
        var saved=CaptureWorld();
        var roundTrip=JsonSerializer.Deserialize<SavedWorld>(JsonSerializer.Serialize(saved));
        Check(ValidSave(roundTrip),"Infinite world JSON validation");
        RestoreWorld(roundTrip);
        Check(Get(-1,20,-1)==Bricks,"Edited chunk restored");
        pos=new Vector3(1000,25,1000); StreamChunks();
        Check(!chunks.ContainsKey((-1,-1)) && savedChunks.ContainsKey((-1,-1)),"Distant terrain evicted while edits retained");
        GenerateChunk(-1,-1);
        Check(Get(-1,20,-1)==Bricks,"Edited chunk reloaded after eviction");
        for(int i=0;i<40;i++) { pos=new Vector3(i*16,25,-i*16); StreamChunks(); }
        Check(chunks.Count<=169,"Resident terrain remains bounded during travel");
        int farCoord = 750 * ChunkSize;
        Check(Get(farCoord,0,-farCoord)==Air,"Missing chunk reads as air");
        GenerateChunk(750,-750);
        Check(Get(farCoord,0,-farCoord)!=Air,"Terrain generated beyond original world borders");
        roundTrip.Chunks[0].Blocks[0]=255;
        Check(!ValidSave(roundTrip),"Invalid blocks rejected");
        var legacy=new SavedWorld { Width=W,Height=32,Depth=D,Blocks=new byte[W*32*D],X=48,Y=20,Z=48 };
        legacy.Blocks[2+W*(3+D*4)]=Wood;
        Check(ValidSave(legacy),"Legacy world accepted");
        RestoreWorld(legacy);
        Check(Get(2,H-32+4,3)==Air,"Legacy distant chunk initially unloaded");
        GenerateChunk(0,0);
        Check(Get(2,H-32+4,3)==Wood && pos.Y==20+H-32,"Legacy builds and player migrated together");
        legacy.Version=2; legacy.GeneratedChunks=new bool[144]; legacy.GeneratedChunks[0]=true;
        RestoreWorld(legacy);
        Check(savedChunks.Count==1,"Legacy partial world imports generated chunks only");
        foreach (int size in new[] { 8, ChunkSize })
        {
            byte[] oldBlocks = new byte[size * size * LegacyHeight];
            oldBlocks[1 + size * (2 + size * 5)] = Bricks;
            var oldChunkWorld = new SavedWorld
            {
                Version = 3, X = 2, Y = 20, Z = 3,
                Chunks = new() { new ChunkSave { X = -1, Z = -1, Blocks = oldBlocks } }
            };
            Check(ValidSave(oldChunkWorld), $"Legacy {size}x{size} chunk accepted");
            RestoreWorld(oldChunkWorld);
            Check(Get(1 - size, AddedDepth + 5, 2 - size) == Bricks &&
                Get(2 - size, AddedDepth + 5, 2 - size) == Air,
                $"Legacy {size}x{size} chunk keeps placed and removed blocks");
        }
        // A missing 8x8 quadrant must retain generated terrain at both coordinate signs.
        foreach (int cx in new[] { -1, 0 })
        {
            int x = cx * ChunkSize, z = x;
            GenerateChunk(cx, cx);
            // Obtain a clean procedural reference even if an earlier test loaded a legacy chunk.
            savedChunks.Remove((cx, cx));
            chunks.Remove((cx, cx));
            GenerateChunk(cx, cx);
            var baseline = (byte[])chunks[(cx, cx)].Blocks.Clone();
            var old = new SavedWorld
            {
                Version = 3, X = x, Y = 24, Z = z,
                Chunks = new() { new ChunkSave { X = cx * 2, Z = cx * 2, Blocks = new byte[8 * 8 * LegacyHeight] } }
            };
            old.Chunks[0].Blocks[1] = Bricks;
            RestoreWorld(old);
            Check(baseline[LocalIndex(x + 2, AddedDepth, z)] != Air &&
                Get(x + 1, AddedDepth, z) == Bricks && Get(x + 2, AddedDepth, z) == Air,
                $"Legacy quadrant keeps edits and explicit air ({cx})");
            Check(Enumerable.Range(AddedDepth, LegacyHeight).All(y =>
                Get(x + 9, y, z) == baseline[LocalIndex(x + 9, y, z)]),
                $"Missing legacy quadrant matches generation ({cx})");
            old.Chunks.Add(new ChunkSave { X = cx * 2 + 1, Z = cx * 2, Blocks = new byte[8 * 8 * LegacyHeight] });
            old.Chunks[1].Blocks[2] = Wood;
            RestoreWorld(old);
            Check(Get(x + 1, AddedDepth, z) == Bricks && Get(x + 10, AddedDepth, z) == Wood &&
                Get(x + 9, AddedDepth, z) == Air &&
                Get(x, AddedDepth, z + 9) == baseline[LocalIndex(x, AddedDepth, z + 9)],
                $"Multiple legacy quadrants share one procedural base ({cx})");
        }
        chunks.Clear(); savedChunks.Clear();
        GenerateChunk(0,0); Array.Clear(chunks[(0,0)].Blocks);
        pos=new Vector3(2.5f,1,2.5f); crouching=true;
        SetBlock(2,0,2,Stone);
        Check(SafeSneakStep(0.9f,true)<0.8f,"Sneaking protects edge");
        Array.Clear(chunks[(0,0)].Blocks); pos.Y=0.4f; SetBlock(2,2,2,Stone);
        Check(!Collides(),"Crouching fits below ceiling"); crouching=false;
        Check(Collides(),"Standing detects ceiling");
        PickBlock(Bricks); Check(Hotbar[selected]==Bricks,"Pick block");
        occX=-48; occZ=-48; BuildOccupancy();
        int offset = OccupancyIndex(2 - occX, 2, 2 - occZ);
        Check(occ[offset]==255,"Moving shadow window handles world offsets");
        SetBlock(2, AddedDepth + 2, 2, Stone);
        int upperOffset = OccupancyIndex(2 - occX, AddedDepth + 2, 2 - occZ);
        Check(occ[upperOffset] == 255, "Upper shadow layer tracks tall-world blocks");
        Array.Clear(chunks[(0,0)].Blocks);
        int above = MeshBlockIndex(2, 1, 2);
        PrepareMeshBlocks(0, 0);
        Check(VertexAO(above, 2, 0)==255,"Open face has no baked occlusion");
        SetBlock(1,1,2,Stone);
        PrepareMeshBlocks(0, 0);
        Check(VertexAO(above, 2, 0)==214,"One neighboring side darkens corner");
        SetBlock(2,1,3,Stone);
        PrepareMeshBlocks(0, 0);
        Check(VertexAO(above, 2, 0)==122,"Two neighboring sides fully occlude corner");
        Check(ParseLobbyArgument(new[]{"--gpu-ready","+connect_lobby","109775241234567890"}) == 109775241234567890UL,"Steam launch lobby argument");
        Check(ParseLobbyArgument(new[]{"+connect_lobby","oops"}) == 0,"Malformed Steam lobby argument rejected");
        Check(ValidCoopInput(127,0,0,8) && !ValidCoopInput(128,0,0,8) &&
            !ValidCoopInput(0,float.NaN,0,0) && !ValidCoopInput(0,0,0,9),"Host rejects malformed player input");
        // Проверяем переход через границу углов: голова не должна делать полный оборот.
        float smoothedYaw = SmoothModelAngle(MathF.PI - 0.05f, -MathF.PI + 0.05f, 0.5f);
        Check(MathF.Abs(smoothedYaw - MathF.PI) < 0.001f,"Coop model turns across angle boundary by shortest path");
        modelReady = true; modelStride = 1; friendSwing = 0.2f;
        ResetFriendModel();
        Check(!modelReady && modelStride == 0 && friendSwing == 0,"Coop model pose resets between sessions");
        Check(MathF.Abs(SkinPixel * 32 - 1.8f) < 0.001f,"Coop model matches standing player height");
        TestWorldStorage(Check);
        TestGuestWorldStorage(Check);
        TestChunkGeometry(Check);
        TestOccupancyEviction(Check);
        TestChunkNeighborhood(Check);
        TestShadowHeight(Check);
        Check(AdvanceShaderAnimation(1, freeze: false) == 1f &&
            AdvanceShaderAnimation(10, freeze: true) == 1f &&
            AdvanceShaderAnimation(11, freeze: true) == 1f &&
            AdvanceShaderAnimation(12, freeze: false) == 2f,
            "Paused shader animation resumes without jumping");
        TestSurvival(Check);
        TestJavaShaderPacks(Check);
        Console.WriteLine($"{count} checks passed");
    }

    static SavedWorld TestWorld() => new()
    {
        Version = 3, Height = H, Chunks = new(), X = 96.5f, Y = H + 1, Z = 96.5f
    };

    static DirectoryInfo CreateSaveTestDirectory() =>
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "save-test-" + Guid.NewGuid().ToString("N")));

    static void TestWorldStorage(Action<bool, string> check)
    {
        var folder = CreateSaveTestDirectory();
        try
        {
            string path = Path.Combine(folder.FullName, "world.json");
            string backup = path + ".bak";
            var initial = TestWorld();
            var next = TestWorld();
            next.Time = 0.5f;

            foreach (bool mainPresent in new[] { true, false })
            {
                if (mainPresent) File.WriteAllText(path, "broken JSON");
                else File.Delete(path);
                string goodBackup = JsonSerializer.Serialize(initial);
                File.WriteAllText(backup, goodBackup);

                var storage = new WorldStorage(path);
                string label = mainPresent ? "corrupt primary" : "missing primary";
                check(storage.Read()?.Time == initial.Time && storage.Recovered && !storage.Blocked,
                    $"Backup loads with {label}");
                check(storage.TryWrite(next, out _) && File.ReadAllText(backup) == goodBackup &&
                    new WorldStorage(path).Read()?.Time == next.Time, $"First write preserves backup with {label}");
                check(storage.TryWrite(initial, out _) &&
                    JsonSerializer.Deserialize<SavedWorld>(File.ReadAllText(backup))?.Time == next.Time,
                    $"Subsequent write rotates the valid primary with {label}");
            }

            const string badPrimary = "{\"Version\":99}", badBackup = "broken backup";
            File.WriteAllText(path, badPrimary);
            File.WriteAllText(backup, badBackup);
            var blocked = new WorldStorage(path);
            check(blocked.Read() == null && blocked.Blocked && !blocked.TryWrite(next, out _) &&
                File.ReadAllText(path) == badPrimary && File.ReadAllText(backup) == badBackup,
                "Two unreadable copies block writes and are preserved");

            File.Delete(path);
            File.Delete(backup);
            var fresh = new WorldStorage(path);
            check(fresh.Read() == null && !fresh.Blocked && fresh.TryWrite(next, out _) && File.Exists(path),
                "No save files permits creating a world");
        }
        finally { folder.Delete(recursive: true); }
    }

    static void TestGuestWorldStorage(Action<bool, string> check)
    {
        var folder = CreateSaveTestDirectory();
        try
        {
            string path = Path.Combine(folder.FullName, "world.json");
            var storage = new WorldStorage(path);
            RestoreWorld(TestWorld());
            const int x = 96, y = H - 1, z = 96;
            check(TrySaveActiveWorld(storage, out _), "Initial solo save before joining");
            SetBlock(x, y, z, Bricks);
            var guest = TestWorld();

            Directory.CreateDirectory(path + ".tmp");
            check(!TryEnterGuestWorld(guest, storage, out string error) && error != null &&
                soloWorld == null && Get(x, y, z) == Bricks && storage.Read()?.Chunks.Count == 0,
                "Guest entry refused on write failure without losing live solo edits");
            Directory.Delete(path + ".tmp");

            check(TryEnterGuestWorld(guest, storage, out _) && Get(x, y, z) == Air,
                "Guest entry saves solo edits before switching");
            SetBlock(x + 1, y, z, Wood);
            check(!TrySaveActiveWorld(storage, out _), "Guest world excluded from normal and shutdown saves");
            LeaveGuestWorld(closing: true);
            RestoreWorld(new WorldStorage(path).Read());
            check(Get(x, y, z) == Bricks && Get(x + 1, y, z) == Air,
                "Reload after guest shutdown retains only the solo edit");

            check(TryEnterGuestWorld(guest, storage, out _), "Guest re-entry for leave and disconnect");
            SetBlock(x + 1, y, z, Wood);
            LeaveGuestWorld(closing: false);
            check(soloWorld == null && Get(x, y, z) == Bricks && Get(x + 1, y, z) == Air,
                "Shared leave and disconnect path restores solo world");
            coopHost = coopMatched = true;
            SetBlock(x + 1, y, z, Wood);
            check(TrySaveActiveWorld(storage, out _), "Host can still save its active world");
            coopHost = coopMatched = false;
            RestoreWorld(new WorldStorage(path).Read());
            check(Get(x + 1, y, z) == Wood, "Host edit persists");
        }
        finally { folder.Delete(recursive: true); }
    }

    static void TestChunkGeometry(Action<bool, string> check)
    {
        chunks.Clear();
        var chunk = new Chunk { Blocks = new byte[ChunkSize * ChunkSize * H] };
        chunks[(0, 0)] = chunk;
        int parts = 0, faces = 0;
        bool valid = true;
        bool Count(int vertices, ReadOnlySpan<ushort> indices)
        {
            parts++;
            faces += indices.Length / 6;
            valid &= vertices <= MaxVertsPerMesh && vertices % 4 == 0 &&
                indices.Length == vertices / 4 * 6 &&
                vBuf.Count == vertices * 3 && uvBuf.Count == vertices * 2 &&
                nBuf.Count == vertices * 3 && aoBuf.Count == vertices * 4;
            foreach (ushort index in indices) valid &= index < vertices;
            return true;
        }

        check(BuildChunkGeometry(0, 0, Count) && parts == 0, "Empty chunk emits no mesh");
        chunk.Blocks[LocalIndex(ChunkSize - 1, 2, 0)] = Stone;
        check(BuildChunkGeometry(0, 0, Count) && parts == 1 && faces == 6 && valid,
            "Single block emits one indexed mesh");
        var neighbor = new Chunk { X = 1, Blocks = new byte[chunk.Blocks.Length] };
        neighbor.Blocks[LocalIndex(ChunkSize, 2, 0)] = Stone;
        chunks[(1, 0)] = neighbor;
        parts = faces = 0;
        check(BuildChunkGeometry(0, 0, Count) && parts == 1 && faces == 5 && valid,
            "Solid neighbor hides the shared chunk-border face");
        chunks.Remove((1, 0));
        var diagonal = new Chunk { X = 1, Z = 1, Blocks = new byte[chunk.Blocks.Length] };
        diagonal.Blocks[LocalIndex(ChunkSize, 3, ChunkSize)] = Stone;
        chunks[(1, 1)] = diagonal;
        PrepareMeshBlocks(0, 0);
        check(VertexAO(MeshBlockIndex(ChunkSize - 1, 3, ChunkSize - 1), 2, 1) == 214,
            "Mesh halo includes the diagonal neighbor for corner AO");
        chunks.Remove((1, 1));
        parts = faces = 0;
        check(BuildChunkGeometry(0, 0, Count) && parts == 1 && faces == 6 && valid,
            "Mesh halo forgets a neighbor after eviction");

        for (int y = 0; y < H; y++)
        for (int z = 0; z < ChunkSize; z++)
        for (int x = 0; x < ChunkSize; x++)
            chunk.Blocks[LocalIndex(x, y, z)] = ((x + y + z) & 1) == 0 ? Stone : Air;

        parts = faces = 0;
        int expectedFaces = chunk.Blocks.Length / 2 * 6;
        check(BuildChunkGeometry(0, 0, Count) && parts > 1 && faces == expectedFaces && valid,
            "Dense checkerboard splits into valid indexed parts without missing faces");
    }

    static void TestOccupancyEviction(Action<bool, string> check)
    {
        chunks.Clear();
        savedChunks.Clear();
        pos = new Vector3(96.5f, 48, 96.5f);
        int originalRadius = renderRadius;
        renderRadius = MinRenderRadius;
        UpdateOccupancyOrigin();
        int x = ChunkSize, z = 6 * ChunkSize;
        GenerateChunk(1, 6);
        SetBlock(x, H - 1, z, Stone);
        int offset = OccupancyIndex(x - occX, H - 1, z - occZ);
        check(occ[offset] == 255, "Loaded chunk populates shadow occupancy");
        StreamChunks();
        check(!chunks.ContainsKey((1, 6)) && occ[offset] == 0,
            "Eviction inside the shadow window removes stale occupancy");
        renderRadius = originalRadius;
    }

    static void TestChunkNeighborhood(Action<bool, string> check)
    {
        pos = new Vector3(-0.25f, 48, -16.25f);
        var center = PlayerChunk;
        int radius = MaxRenderRadius;
        var expected =
            from x in Enumerable.Range(center.X - radius, radius * 2 + 1)
            from z in Enumerable.Range(center.Z - radius, radius * 2 + 1)
            orderby Math.Max(Math.Abs(x - center.X), Math.Abs(z - center.Z))
            select (x, z);
        check(center == (-1, -2) && NearbyChunkIndices(radius).SequenceEqual(expected),
            "Cached chunk order matches distance priority at negative coordinates");

        chunks.Clear();
        savedChunks.Clear();
        GenerateChunk(-1, -1);
        GenerateChunk(0, -1);
        GenerateChunk(-1, 0);
        GenerateChunk(0, 0);
        foreach (var chunk in chunks.Values) chunk.Dirty = false;
        SetBlock(-2, H - 1, -2, Bricks);
        check(chunks.Where(pair => pair.Value.Dirty).Select(pair => pair.Key).SequenceEqual(new[] { (-1, -1) }),
            "Interior block edit only dirties its own chunk");
        SetBlock(-1, H - 1, -1, Bricks);
        check(chunks.Values.All(chunk => chunk.Dirty),
            "Corner block edit dirties all chunks that sample its geometry or AO");
    }

    static void TestShadowHeight(Action<bool, string> check)
    {
        Array.Clear(occ);
        check(FindShadowHeight() == 0, "Empty shadow map has no occupied height");
        SetOcc(occX, 12, occZ, true);
        SetOcc(occX + W - 1, H - 1, occZ + D - 1, true);
        check(FindShadowHeight() == H, "Shadow bound includes the top voxel of the farthest column");
        SetOcc(occX + W - 1, H - 1, occZ + D - 1, false);
        check(FindShadowHeight() == 13, "Shadow bound falls after the highest voxel is removed");
    }

    static void TestSurvival(Action<bool, string> check)
    {
        const uint seed = 421;
        RestoreWorld(NewWorldState(seed, true));
        byte[] terrain = GenerateChunkBlocks(6, 6);
        check(terrain.Contains(CoalOre) && terrain.Contains(IronOre) && terrain.Contains(GoldOre),
            "Seeded underground chunk contains all three ores");
        check(terrain.SequenceEqual(GenerateChunkBlocks(6, 6)), "Ore generation is repeatable");
        const int x = 96, y = H - 1, z = 96;
        SetBlock(x, y, z, Wood);
        SurvivalEdit(x, y, z, Air);
        check(Get(x, y, z) == Air && items[Wood] == 1, "Survival mining collects the removed block");
        check(TryCraft(recipes[0]) && items[Wood] == 0 && items[Planks] == 4 &&
            !TryCraft(recipes[0]) && items[Planks] == 4,
            "Crafting consumes inputs once and rejects missing materials");
        SurvivalEdit(x, y, z, Planks);
        check(Get(x, y, z) == Planks && items[Planks] == 3, "Survival placement spends one block");
        SurvivalEdit(x + 1, y, z, Bricks);
        check(Get(x + 1, y, z) == Air, "Survival placement requires inventory stock");

        var folder = CreateSaveTestDirectory();
        try
        {
            string path = Path.Combine(folder.FullName, "first.json");
            string another = Path.Combine(folder.FullName, "second.json");
            health = 13;
            var storage = new WorldStorage(path);
            check(storage.TryWrite(CaptureWorld(), out _, createOnly: true), "First named world saves");
            RestoreWorld(NewWorldState(seed + 1, false));
            check(!terrain.SequenceEqual(GenerateChunkBlocks(6, 6)) && !survivalMode && items[Planks] == 0,
                "Changing worlds changes seed and resets survival state");
            check(new WorldStorage(another).TryWrite(CaptureWorld(), out _, createOnly: true) &&
                !storage.TryWrite(CaptureWorld(), out _, createOnly: true), "Named worlds cannot overwrite one another");
            RestoreWorld(storage.Read());
            check(survivalMode && Seed == seed && health == 13 && items[Planks] == 3 &&
                Get(x, y, z) == Planks, "Survival seed, health, inventory and edits reload together");
            check(ValidWorldName("New World-2") && !ValidWorldName("../world") &&
                !ValidWorldName("world.json"), "World names cannot escape the save directory");
        }
        finally { folder.Delete(recursive: true); }
    }

    static void TestJavaShaderPacks(Action<bool, string> check)
    {
        const string root = "Wrapped/shaders/";
        var files = new Dictionary<string, string>
        {
            [root + "gbuffers_terrain.vsh"] = "#version 120\nvoid main() { gl_Position = ftransform(); }",
            [root + "gbuffers_terrain.fsh"] = "#version 120\n#include \"/lib/color.glsl\"\nvoid main() { gl_FragData[0] = tint; }",
            [root + "lib/color.glsl"] = "const vec4 tint = vec4(1.0);"
        };
        var pack = ReadShaderEntries(files.Keys, file => files[file]);
        check(pack.Java && pack.Vertex.StartsWith("#version 330 core") &&
            !pack.Vertex.Contains("#version 120") && !pack.Fragment.Contains("#version 120") &&
            pack.Fragment.Contains("const vec4 tint") && pack.Fragment.Contains("javaFragment = tint"),
            "Wrapped Java pack adapts terrain shaders and resolves absolute includes");

        bool Rejects()
        {
            try { ReadShaderEntries(files.Keys, file => files[file]); return false; }
            catch (InvalidDataException) { return true; }
        }

        const string post = "#version 120\nuniform sampler2D colortex0;\nvoid main() { gl_FragColor = texture2D(colortex0, gl_TexCoord[0].st); }";
        files[root + "final.fsh"] = post + " // final pass";
        pack = ReadShaderEntries(files.Keys, file => files[file]);
        check(pack.Post is { Length: 1 } && pack.Post[0].Vertex.Contains("javaVertexMain") &&
            pack.Post[0].Fragment.Contains("texture2D(texture0"), "Java final pass samples scene color");
        files[root + "composite.fsh"] = post + " // base pass";
        files[root + "composite10.fsh"] = post + " // tenth pass";
        files[root + "composite2.fsh"] = post + " // second pass";
        pack = ReadShaderEntries(files.Keys, file => files[file]);
        check(pack.Post.Length == 4 && pack.Post[0].Fragment.Contains("base pass") &&
            pack.Post[1].Fragment.Contains("second pass") && pack.Post[2].Fragment.Contains("tenth pass") &&
            pack.Post[3].Fragment.Contains("final pass"),
            "Java composite passes use numeric order and final runs last");
        files[root + "shadow.fsh"] = post;
        check(Rejects(), "A Java pack requiring shadow passes is rejected");
        files.Remove(root + "shadow.fsh");
        files.Remove(root + "composite10.fsh");
        files.Remove(root + "composite2.fsh");
        files.Remove(root + "final.fsh");
        pack = ReadShaderEntries(files.Keys, file => files[file]);
        check(pack.Post is { Length: 1 }, "A single Java composite pass is accepted");
        files[root + "composite.fsh"] = post.Replace("colortex0", "colortex1");
        check(Rejects(), "A Java screen pass cannot read an unsupported color buffer");
        string depthPass = post.Replace("colortex0", "depthtex0");
        files[root + "composite.fsh"] = depthPass;
        pack = ReadShaderEntries(files.Keys, file => files[file]);
        check(pack.Post[0].Fragment.Contains("texture2D(javaSceneDepth"), "Screen pass can sample the scene depth texture");
        files[root + "composite.fsh"] = depthPass.Replace("depthtex0", "gdepthtex");
        pack = ReadShaderEntries(files.Keys, file => files[file]);
        check(pack.Post[0].Fragment.Contains("texture2D(javaSceneDepth"), "Legacy gdepthtex aliases scene depth");
        files[root + "composite.fsh"] = depthPass.Replace("depthtex0", "depthtex1");
        check(Rejects(), "Java depth snapshots beyond depthtex0 are rejected");
        files.Remove(root + "composite.fsh");
        files[root + "lib/color.glsl"] = "#include \"../../outside.glsl\"";
        check(Rejects(), "Java shader include cannot escape the shaders directory");
        files[root + "lib/color.glsl"] = "#include \"/lib/color.glsl\"";
        check(Rejects(), "Recursive Java shader include is rejected");
        files[root + "lib/color.glsl"] = "const vec4 tint = vec4(1.0);";
        files[root + "gbuffers_terrain.fsh"] = "#version 120\nvoid main() { gl_FragData[1] = vec4(1.0); }";
        check(Rejects(), "Java shader cannot write unsupported render targets");

        files[root + "world0/gbuffers_terrain.fsh"] = "#version 130\n#include \"/lib/color.glsl\"\nvoid main() { gl_FragColor = tint; }";
        files[root + "world0/final.fsh"] = post + " // overworld final";
        files[root + "final.fsh"] = post + " // fallback final";
        files[root + "composite.fsh"] = post + " // fallback composite";
        pack = ReadShaderEntries(files.Keys, file => files[file]);
        check(pack.Fragment.Contains("const vec4 tint") && pack.Post.Length == 2 &&
            pack.Post[0].Fragment.Contains("fallback composite") && pack.Post[1].Fragment.Contains("overworld final"),
            "Overworld shader overrides use root includes and common passes");
        files[root + "world0/deferred10.fsh"] = post + " // deferred ten";
        files[root + "world0/deferred1.fsh"] = post + " // deferred one";
        pack = ReadShaderEntries(files.Keys, file => files[file]);
        check(pack.Post.Length == 4 && pack.Post[0].Fragment.Contains("deferred one") &&
            pack.Post[1].Fragment.Contains("deferred ten") &&
            pack.Post[2].Fragment.Contains("fallback composite") && pack.Post[3].Fragment.Contains("overworld final"),
            "Java deferred passes run in numeric order before composite and final");
        files[root + "world0/shadow.fsh"] = post;
        files[root + "world0/shadowcomp.fsh"] = post;
        try
        {
            ReadShaderEntries(files.Keys, file => files[file]);
            check(false, "Overworld package reports unsupported pass names");
        }
        catch (InvalidDataException ex)
        {
            check(ex.Message.Contains("shadow, shadowcomp"), "Overworld package reports unsupported pass names");
        }
        if (File.Exists("shaderpacks/Realis_v1.0_2.zip"))
        {
            var realisPack = ReadShaderPack("shaderpacks/Realis_v1.0_2.zip");
            check(realisPack != null && !string.IsNullOrEmpty(realisPack.Fragment) && realisPack.Fragment.Contains("tonemapACES"),
                "Realis Java shader pack adapts successfully to native engine");
        }
    }
}

