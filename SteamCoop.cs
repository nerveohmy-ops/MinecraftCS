using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Raylib_cs;
using Steamworks;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    const int CoopPort = 72;
    const int MaxCoopPacket = 32768;
    static readonly string CoopVersion = typeof(Program).Module.ModuleVersionId.ToString("N");
    static bool steamReady, coopHost, coopMatched, coopReady, coopConnected, coopBusy, coopPanel;
    static string coopStatus = "Steam is unavailable";
    static double coopDeadline, nextCoopSend, lastCoopInput;
    static CSteamID coopLobby, expectedLobby;
    static HSteamListenSocket coopListen;
    static HSteamNetConnection coopPeer;
    static Callback<GameLobbyJoinRequested_t> inviteCallback;
    static Callback<SteamNetConnectionStatusChangedCallback_t> connectionCallback;
    static CallResult<LobbyCreated_t> createResult;
    static CallResult<LobbyEnter_t> joinResult;
    static SavedWorld soloWorld, incomingWorld;
    static List<ChunkSave> outgoingWorld;
    static int nextSnapshotChunk;
    static PlayerState friend;
    static Packet friendInput;
    static readonly IntPtr[] coopMessages = new IntPtr[16];

    internal sealed class Packet
    {
        public string Kind { get; set; }
        public string Version { get; set; }
        public int Keys { get; set; }
        public int Slot { get; set; }
        public int Action { get; set; }
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public PlayerState Player { get; set; }
        public PlayerState Host { get; set; }
        public SavedWorld World { get; set; }
        public ChunkSave Chunk { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public byte Block { get; set; }
        public float Time { get; set; }
    }

    internal sealed class PlayerState
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public bool Crouch { get; set; }
        public bool Fly { get; set; }
        internal Vector3 Velocity;
        internal bool Ground;
        internal Vector3 Position => new(X,Y,Z);
    }

    static ulong ParseLobbyArgument(string[] args)
    {
        int i = Array.IndexOf(args, "+connect_lobby");
        return i >= 0 && i + 1 < args.Length && ulong.TryParse(args[i+1], out ulong id) ? id : 0;
    }

    static bool ValidCoopInput(int keys, float angle, float tilt, int slot) =>
        keys is >= 0 and <= 127 && float.IsFinite(angle) && float.IsFinite(tilt) &&
        MathF.Abs(tilt) <= 1.55f && slot >= 0 && slot < Hotbar.Length;

    static void InitCoop()
    {
        try
        {
#if STEAM_TEST
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
#endif
            steamReady = SteamAPI.Init();
            if (!steamReady) { coopStatus = "Open Steam and launch with a configured AppID."; return; }
            uint appId = SteamUtils.GetAppID().m_AppId;
#if STEAM_TEST
            bool allowed = appId == 480;
#else
            string configured = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "SteamAppId")?.Value;
            bool allowed = appId != 0 && appId != 480 &&
                (string.IsNullOrEmpty(configured) || uint.TryParse(configured,out uint expected) && appId == expected);
#endif
            if (!allowed)
            {
                SteamAPI.Shutdown();
                steamReady = false;
                coopStatus = "Steam AppID is invalid for this build.";
                return;
            }
            SteamNetworkingUtils.InitRelayNetworkAccess();
            inviteCallback = Callback<GameLobbyJoinRequested_t>.Create(e => JoinCoop(e.m_steamIDLobby));
            connectionCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnCoopConnection);
            createResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            joinResult = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
            coopStatus = "Create a private game or accept a Steam invitation.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            steamReady = false;
            coopStatus = "Install the matching Steamworks steam_api64.dll.";
            Console.Error.WriteLine(ex.Message);
        }
    }

    static void CreateCoop()
    {
        if (!steamReady || coopBusy || coopLobby.IsValid()) return;
        coopHost = coopBusy = true;
        coopDeadline = Raylib.GetTime()+30;
        coopStatus = "Creating private lobby...";
        createResult.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate,2));
    }

    static void OnLobbyCreated(LobbyCreated_t result, bool failed)
    {
        coopBusy = false;
        if (failed || result.m_eResult != EResult.k_EResultOK)
        { EndCoop("Steam could not create the lobby."); return; }
        coopLobby = new CSteamID(result.m_ulSteamIDLobby);
        coopListen = SteamNetworkingSockets.CreateListenSocketP2P(CoopPort,0,null);
        if (coopListen == HSteamListenSocket.Invalid)
        { EndCoop("Could not open the host connection."); return; }
        SteamMatchmaking.SetLobbyData(coopLobby,"minecraftcs_build",CoopVersion);
        coopStatus = "Lobby created. Invite a friend through Steam.";
    }

    static void JoinCoop(CSteamID lobby)
    {
        if (!steamReady || !lobby.IsValid() || coopLobby == lobby || coopBusy) return;
        EndCoop("Joining Steam lobby...");
        coopPanel = coopBusy = true;
        expectedLobby = lobby;
        coopDeadline = Raylib.GetTime()+30;
        joinResult.Set(SteamMatchmaking.JoinLobby(lobby));
    }

    static void OnLobbyEntered(LobbyEnter_t result, bool failed)
    {
        if (failed || result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
        { EndCoop("Lobby unavailable or full. Ask for a new invitation."); return; }
        coopLobby = new CSteamID(result.m_ulSteamIDLobby);
        if (coopLobby != expectedLobby ||
            SteamMatchmaking.GetLobbyData(coopLobby,"minecraftcs_build") != CoopVersion)
        { EndCoop("Build mismatch. Both players need the same build."); return; }
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID(SteamMatchmaking.GetLobbyOwner(coopLobby));
        coopPeer = SteamNetworkingSockets.ConnectP2P(ref identity,CoopPort,0,null);
        if (coopPeer == HSteamNetConnection.Invalid)
        { EndCoop("Could not connect to the host."); return; }
        coopStatus = "Connecting to the host...";
    }

    static void OnCoopConnection(SteamNetConnectionStatusChangedCallback_t e)
    {
        var state = e.m_info.m_eState;
        if (coopHost && coopListen != HSteamListenSocket.Invalid && e.m_info.m_hListenSocket == coopListen &&
            state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
        {
            var id = e.m_info.m_identityRemote.GetSteamID();
            bool member = Enumerable.Range(0,SteamMatchmaking.GetNumLobbyMembers(coopLobby))
                .Any(i => SteamMatchmaking.GetLobbyMemberByIndex(coopLobby,i) == id && id != SteamUser.GetSteamID());
            if (!member || coopPeer != HSteamNetConnection.Invalid || coopMatched ||
                SteamNetworkingSockets.AcceptConnection(e.m_hConn) != EResult.k_EResultOK)
            { SteamNetworkingSockets.CloseConnection(e.m_hConn,0,"Lobby member required",false); return; }
            coopPeer = e.m_hConn;
            coopDeadline = Raylib.GetTime()+30;
            return;
        }
        if (e.m_hConn != coopPeer || coopPeer == HSteamNetConnection.Invalid) return;
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
        {
            coopConnected = true;
            coopDeadline = Raylib.GetTime()+30;
            SendCoop(new Packet { Kind="hello", Version=CoopVersion });
        }
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ||
            state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
            EndCoop(e.m_info.m_eEndReason == 4001 ? "Build mismatch. Install the same build." :
                coopHost ? "Friend disconnected." : "Host disconnected. Returned to menu.");
    }

    static bool SendCoop(Packet packet, bool reliable = true)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (coopPeer == HSteamNetConnection.Invalid || data.Length > MaxCoopPacket) return false;
        fixed (byte* ptr = data)
            return SteamNetworkingSockets.SendMessageToConnection(coopPeer,(IntPtr)ptr,(uint)data.Length,
                reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_UnreliableNoDelay,
                out _) == EResult.k_EResultOK;
    }

    static void PollCoop(float dt)
    {
        if (!steamReady) return;
        SteamAPI.RunCallbacks();
        if (coopPeer != HSteamNetConnection.Invalid)
        {
            int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(coopPeer,coopMessages,coopMessages.Length);
            for (int i=0;i<count;i++)
            {
                IntPtr ptr = coopMessages[i];
                try
                {
                    if (!coopConnected) continue;
                    var message = Marshal.PtrToStructure<SteamNetworkingMessage_t>(ptr);
                    if (message.m_cbSize is <= 0 or > MaxCoopPacket) continue;
                    var packet = JsonSerializer.Deserialize<Packet>(new ReadOnlySpan<byte>((void*)message.m_pData,message.m_cbSize));
                    if (packet != null) ReceiveCoop(packet);
                }
                catch (JsonException) { EndCoop("Invalid multiplayer data."); }
                finally { SteamNetworkingMessage_t.Release(ptr); }
            }
        }
        if (coopSendFailed)
        {
            coopSendFailed = false;
            EndCoop("Connection could not keep up. Start a new match.");
        }
        if ((coopBusy || (coopPeer != HSteamNetConnection.Invalid && !coopReady) || incomingWorld != null)
            && Raylib.GetTime() > coopDeadline)
            EndCoop("Steam connection timed out. Try a new invitation.");
        if (outgoingWorld != null) PumpWorldSnapshot();
        if (!coopMatched || !coopHost || friend == null) return;
        SimulateFriend(dt);
        if (Raylib.GetTime() >= nextCoopSend)
        {
            nextCoopSend = Raylib.GetTime()+0.05;
            SendCoop(new Packet { Kind="state", Player=friend, Host=ReadPlayer(), Time=timeOfDay },false);
        }
    }

    static void ReceiveCoop(Packet p)
    {
        if (p.Kind == "hello" && !coopReady)
        {
            if (p.Version != CoopVersion) { EndCoop("Build mismatch. Install the same build.",4001); return; }
            coopReady = true;
            coopBusy = false;
            coopStatus = coopHost ? "Friend connected. You can start the match." : "Connected. Waiting for the host.";
            return;
        }
        if (!coopReady) return;
        if (coopHost)
        {
            if (!coopMatched || p.Kind != "input" || !ValidCoopInput(p.Keys,p.Yaw,p.Pitch,p.Slot)) return;
            friendInput = p;
            lastCoopInput = Raylib.GetTime();
            if (p.Action is >= 1 and <= 3) HandleFriendAction(p);
            return;
        }
        switch (p.Kind)
        {
            case "world" when !coopMatched && incomingWorld == null && p.World?.Version == 3 &&
                p.World.Chunks?.Count == 0 && ValidSave(p.World):
                incomingWorld = p.World;
                coopStatus = "Receiving host world...";
                coopDeadline = Raylib.GetTime()+30;
                break;
            case "chunk" when incomingWorld != null && p.Chunk != null:
                if (incomingWorld.Chunks.Count >= 16384 || !ValidBlocks(p.Chunk.Blocks,ChunkSize*ChunkSize*H) ||
                    Math.Abs((long)p.Chunk.X)>12500000 || Math.Abs((long)p.Chunk.Z)>12500000)
                { EndCoop("Invalid world snapshot."); break; }
                incomingWorld.Chunks.Add(p.Chunk);
                coopDeadline = Raylib.GetTime()+30;
                break;
            case "start" when incomingWorld != null:
                if (!ValidSave(incomingWorld) || !ValidPlayer(p.Host))
                { EndCoop("Invalid world snapshot."); break; }
                soloWorld = CaptureWorld();
                ReplaceCoopWorld(incomingWorld);
                incomingWorld = null;
                friend = p.Host;
                coopMatched = true;
                ResumeCoop();
                break;
            case "state" when coopMatched && ValidPlayer(p.Player) && ValidPlayer(p.Host) && p.Time is >= 0 and < 1:
                ApplyPlayer(p.Player,false);
                friend = p.Host;
                timeOfDay = p.Time;
                UpdateSun(0);
                break;
            case "block" when coopMatched && p.Y is > 0 and < H && p.Block <= Cobblestone &&
                Math.Abs((long)p.X)<=100000000 && Math.Abs((long)p.Z)<=100000000:
                var key = (FloorChunk(p.X),FloorChunk(p.Z));
                if (chunks.ContainsKey(key))
                {
                    byte before = Get(p.X,p.Y,p.Z);
                    SetBlock(p.X,p.Y,p.Z,p.Block);
                    AnimateBlockEdit(p.X,p.Y,p.Z,before,p.Block);
                    if (friend != null && Vector3.DistanceSquared(friend.Position,
                        new Vector3(p.X,p.Y,p.Z)) < 64) friendSwing = 0.26f;
                }
                else
                {
                    GenerateChunk(key.Item1,key.Item2);
                    SetBlock(p.X,p.Y,p.Z,p.Block);
                    savedChunks[key] = chunks[key].Blocks;
                    chunks.Remove(key);
                }
                break;
        }
    }

    static bool ValidPlayer(PlayerState p) => p != null && ValidCoopInput(0,p.Yaw,p.Pitch,0) &&
        MathF.Abs(p.X)<=100000000 && MathF.Abs(p.Z)<=100000000 && p.Y is >= 0 and <= H+16;

    static PlayerState ReadPlayer() => new()
    {
        X=pos.X,Y=pos.Y,Z=pos.Z,Yaw=yaw,Pitch=pitch,Crouch=crouching,Fly=flying,Velocity=vel,Ground=onGround
    };

    static void ApplyPlayer(PlayerState p, bool view)
    {
        pos=p.Position; vel=p.Velocity; onGround=p.Ground; crouching=p.Crouch; flying=p.Fly;
        if (view) { yaw=p.Yaw; pitch=p.Pitch; }
    }

    static void StartCoop()
    {
        if (!coopHost || !coopReady || coopMatched) return;
        SavedWorld world = CaptureWorld();
        var changed = world.Chunks;
        if (changed.Count > 16384) { coopStatus = "World is too large for this co-op build."; return; }
        world.Chunks = new();
        world.Y = TopY((int)MathF.Floor(world.X),(int)MathF.Floor(world.Z))+1;
        world.Flying = world.Crouching = false;
        friend = new PlayerState { X=world.X,Y=world.Y,Z=world.Z,Yaw=world.Yaw,Pitch=world.Pitch };
        if (!SendCoop(new Packet { Kind="world", World=world }))
        { EndCoop("Could not send the world. Try again."); return; }
        outgoingWorld = changed;
        nextSnapshotChunk = 0;
        coopBusy = true;
        coopDeadline = Raylib.GetTime()+30;
        coopStatus = "Sending world to your friend...";
    }

    static void PumpWorldSnapshot()
    {
        for (int sent=0; sent<8 && nextSnapshotChunk<outgoingWorld.Count; sent++)
        {
            if (!SendCoop(new Packet { Kind="chunk", Chunk=outgoingWorld[nextSnapshotChunk] })) return;
            nextSnapshotChunk++;
            coopDeadline = Raylib.GetTime()+30;
        }
        if (nextSnapshotChunk != outgoingWorld.Count ||
            !SendCoop(new Packet { Kind="start", Host=ReadPlayer() })) return;
        outgoingWorld = null;
        coopBusy = false;
        SteamMatchmaking.SetLobbyJoinable(coopLobby,false);
        coopMatched = true;
        ResumeCoop();
    }

    static void ResumeCoop()
    {
        paused = coopPanel = false;
        frame=0;
        Raylib.DisableCursor();
    }

    static void ReplaceCoopWorld(SavedWorld world)
    {
        foreach (var c in chunks.Values)
            if (c.HasMesh) Raylib.UnloadMesh(c.Mesh);
        RestoreWorld(world);
        vel = Vector3.Zero;
        onGround = hasHit = false;
        BuildOccupancy();
        FlushOccupancy();
    }

    static void EndCoop(string message, int reason = 0, bool closing = false)
    {
        if (coopPeer != HSteamNetConnection.Invalid)
            SteamNetworkingSockets.CloseConnection(coopPeer,reason,message,false);
        if (coopListen != HSteamListenSocket.Invalid)
            SteamNetworkingSockets.CloseListenSocket(coopListen);
        if (coopLobby.IsValid()) SteamMatchmaking.LeaveLobby(coopLobby);
        coopPeer = HSteamNetConnection.Invalid;
        coopListen = HSteamListenSocket.Invalid;
        coopLobby = expectedLobby = default;
        coopHost = coopBusy = coopReady = coopMatched = coopConnected = false;
        friend = null;
        friendInput = null;
        incomingWorld = null;
        outgoingWorld = null;
        coopSendFailed = false;
        createResult?.Cancel();
        joinResult?.Cancel();
        if (soloWorld != null)
        {
            if (!closing) ReplaceCoopWorld(soloWorld);
            soloWorld = null;
        }
        coopStatus = message;
        if (!closing)
        {
            paused = coopPanel = true;
            Raylib.EnableCursor();
        }
    }
}
