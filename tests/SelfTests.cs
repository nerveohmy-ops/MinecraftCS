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
        Check(FloorChunk(-1)==-1 && FloorChunk(-ChunkSize)==-1 && FloorChunk(-ChunkSize-1)==-2 && Mod(-1,ChunkSize)==ChunkSize-1,"Negative coordinate mapping");
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
        int offset=((2-occZ)*OccW+(2-occX)+W*2)*4;
        Check(occ[offset]==255,"Moving shadow window handles world offsets");
        SetBlock(2, AddedDepth + 2, 2, Stone);
        int upperOffset = offset + D * OccW * 4;
        Check(occ[upperOffset] == 255, "Upper shadow layer tracks tall-world blocks");
        Array.Clear(chunks[(0,0)].Blocks);
        Check(VertexAO(2,0,2,2,0)==255,"Open face has no baked occlusion");
        SetBlock(1,1,2,Stone);
        Check(VertexAO(2,0,2,2,0)==214,"One neighboring side darkens corner");
        SetBlock(2,1,3,Stone);
        Check(VertexAO(2,0,2,2,0)==122,"Two neighboring sides fully occlude corner");
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
        Console.WriteLine($"{count} checks passed");
    }
}

