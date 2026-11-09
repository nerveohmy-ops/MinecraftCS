using Raylib_cs;
using Steamworks;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

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

    // Ввод, симуляция напарника и меню



    static readonly KeyboardKey[] CoopKeys =
        { KeyboardKey.W, KeyboardKey.S, KeyboardKey.D, KeyboardKey.A, KeyboardKey.LeftShift, KeyboardKey.Space, KeyboardKey.LeftControl };
    static int ReadCoopKeys()
    {
        int keys=0;
        for (int i=0;i<CoopKeys.Length;i++)
            if (Raylib.IsKeyDown(CoopKeys[i])) keys |= 1<<i;
        return keys;
    }

    static void SendPlayerInput()
    {
        int action = Raylib.IsMouseButtonPressed(MouseButton.Left) ? 1 :
            Raylib.IsMouseButtonPressed(MouseButton.Right) ? 2 :
            Raylib.IsKeyPressed(KeyboardKey.F) ? 3 : 0;
        double now = Raylib.GetTime();
        if (action == 0 && now < nextCoopSend) return;
        nextCoopSend = now + 0.05;
        SendCoop(new Packet { Kind="input", Keys=ReadCoopKeys(), Yaw=yaw, Pitch=pitch, Slot=selected, Action=action });
    }

    static void SimulateFriend(float dt)
    {
        PlayerState host = ReadPlayer();
        ApplyPlayer(friend,true);
        try
        {
            foreach (var key in NearbyChunkIndices(1)) GenerateChunk(key.X,key.Z);
            int keys = Raylib.GetTime()-lastCoopInput < 0.35 ? friendInput?.Keys ?? 0 : 0;
            if (friendInput != null) { yaw = friendInput.Yaw; pitch = friendInput.Pitch; }
            bool wasCrouching = crouching;
            crouching = !flying && (keys & 64) != 0;
            if (wasCrouching && !crouching && Collides()) crouching = true;
            SimulatePlayer(dt,keys);
            friend = ReadPlayer();
        }
        finally { ApplyPlayer(host,true); }
    }

    static void HandleFriendAction(Packet packet)
    {
        if (packet.Action is 1 or 2) friendSwing = 0.26f;
        if (packet.Action == 3)
        {
            friend.Fly = !friend.Fly;
            friend.Velocity = Vector3.Zero;
            return;
        }
        PlayerState host = ReadPlayer();
        ApplyPlayer(friend,true);
        yaw = packet.Yaw;
        pitch = packet.Pitch;
        var rayState = (hitX,hitY,hitZ,prevX,prevY,prevZ);
        try
        {
            if (!Raycast(pos+new Vector3(0,EyeHeight,0),LookDir(),ReachDist)) return;
            if (packet.Action == 1 && hitY > 0)
                CommitCoopBlock(hitX,hitY,hitZ,Air);
            if (packet.Action == 2 && !IntersectsPlayer(prevX,prevY,prevZ) &&
                !PlayerOverlaps(host,prevX,prevY,prevZ) && Get(prevX,prevY,prevZ)==Air)
                CommitCoopBlock(prevX,prevY,prevZ,Hotbar[packet.Slot]);
        }
        finally
        {
            (hitX,hitY,hitZ,prevX,prevY,prevZ) = rayState;
            ApplyPlayer(host,true);
        }
    }

    static bool PlayerOverlaps(PlayerState player,int x,int y,int z) =>
        player != null && x+1 > player.X-HalfW && x < player.X+HalfW &&
        z+1 > player.Z-HalfW && z < player.Z+HalfW &&
        y+1 > player.Y && y < player.Y+(player.Crouch?1.5f:1.8f);

    static void CommitCoopBlock(int x,int y,int z,byte block)
    {
        if (y<=0 || y>=H || !chunks.ContainsKey((FloorChunk(x),FloorChunk(z)))) return;
        if (block != Air && (PlayerOverlaps(friend,x,y,z) || IntersectsPlayer(x,y,z))) return;
        byte before = Get(x,y,z);
        SetBlock(x,y,z,block);
        AnimateBlockEdit(x,y,z,before,block);
        if (coopMatched && coopHost && !SendCoop(new Packet { Kind="block", X=x,Y=y,Z=z,Block=block }))
            coopSendFailed = true;
    }

    static bool coopSendFailed;

    static void DrawCoopFriend()
    {
        if (!coopMatched || friend == null) return;
        DrawAnimatedFriend();
    }

    static Rectangle CoopButton(int row) =>
        new(Raylib.GetScreenWidth()/2f-150,Raylib.GetScreenHeight()/2f-64+row*48,300,40);

    static string[] CoopLabels() => new[]
    {
        "Create game", "Invite a friend", "Start match", "Leave lobby", "Back"
    };

    static bool CoopButtonEnabled(int row) => row switch
    {
        0 => steamReady && !coopBusy && !coopLobby.IsValid(),
        1 => coopHost && coopLobby.IsValid() && !coopMatched,
        2 => coopHost && coopReady && !coopMatched && !coopBusy,
        3 => coopLobby.IsValid() && !coopBusy,
        _ => !coopBusy
    };

    static void UpdateCoopMenu()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Q)) quitRequested = true;
        if (Raylib.IsKeyPressed(KeyboardKey.Escape) && !coopBusy) { coopPanel=false; return; }
        if (!Raylib.IsMouseButtonPressed(MouseButton.Left)) return;
        for (int row=0;row<5;row++)
        {
            if (!CoopButtonEnabled(row) || !Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(),CoopButton(row))) continue;
            switch (row)
            {
                case 0: CreateCoop(); break;
                case 1:
                    if (SteamUtils.IsOverlayEnabled()) SteamFriends.ActivateGameOverlayInviteDialog(coopLobby);
                    else coopStatus = "Steam overlay unavailable. Enable it and restart the game.";
                    break;
                case 2: StartCoop(); break;
                case 3: EndCoop("Lobby left."); break;
                case 4: coopPanel=false; break;
            }
            break;
        }
    }

    static void DrawCoopMenu()
    {
        int width=Raylib.GetScreenWidth(), height=Raylib.GetScreenHeight();
        Raylib.DrawRectangle(0,0,width,height,Col(16,21,26,248));
        void Center(string label,int y,int size,Color color) =>
            Raylib.DrawText(label,(width-Raylib.MeasureText(label,size))/2,y,size,color);
        Center("STEAM CO-OP",height/2-165,34,Col(255,255,255));
        Center(coopStatus,height/2-111,18,Col(255,215,126));
        string[] labels = CoopLabels();
        for (int row=0;row<labels.Length;row++)
        {
            var rect=CoopButton(row);
            bool enabled=CoopButtonEnabled(row);
            Raylib.DrawRectangleRec(rect,enabled?Col(51,92,114):Col(41,47,53));
            Center(labels[row],(int)rect.Y+10,20,enabled?Col(255,255,255):Col(140,148,155));
        }
    }

    // Модель и поза напарника



    // Геометрия в пропорциях классического блочного персонажа: 8x8 голова,
    // 8x12 корпус и 4x12 конечности. Масштаб сохраняет игровую высоту 1,8 блока.
    const float SkinPixel = 1.8f / 32;
    static Vector3 modelPosition;
    static float modelYaw, modelPitch, modelCrouch, modelStride, modelPhase, modelAir;
    static bool modelReady;

    static void ResetFriendModel()
    {
        modelReady = false;
        modelCrouch = modelStride = modelPhase = modelAir = 0;
        friendSwing = 0;
    }

    static float SmoothModelAngle(float current, float target, float blend) =>
        current + MathF.Atan2(MathF.Sin(target - current), MathF.Cos(target - current)) * blend;

    static void UpdateFriendModel(float dt)
    {
        if (!coopMatched || friend == null) { ResetFriendModel(); return; }
        if (!modelReady || Vector3.DistanceSquared(modelPosition, friend.Position) > 64)
        {
            // Телепорт и новая сессия не должны растягивать интерполяцию через весь мир.
            modelPosition = friend.Position;
            modelYaw = friend.Yaw;
            modelPitch = friend.Pitch;
            modelReady = true;
        }
        float blend = 1 - MathF.Exp(-dt * 14);
        Vector3 previous = modelPosition;
        modelPosition = Vector3.Lerp(modelPosition, friend.Position, blend);
        modelYaw = SmoothModelAngle(modelYaw, friend.Yaw, blend);
        modelPitch += (friend.Pitch - modelPitch) * blend;
        modelCrouch += ((friend.Crouch ? 1 : 0) - modelCrouch) * blend;
        float distance = Vector2.Distance(new(previous.X, previous.Z), new(modelPosition.X, modelPosition.Z));
        float speed = distance / MathF.Max(dt, 0.001f);
        // У клиента Ground не передаётся: опору проверяем по блокам под ногами.
        bool grounded = !friend.Fly && Get((int)MathF.Floor(friend.X),
            (int)MathF.Floor(friend.Y - 0.08f), (int)MathF.Floor(friend.Z)) != Air;
        float stride = grounded ? Math.Clamp(speed / WalkSpeed, 0, 1.5f) : 0;
        modelStride += (stride - modelStride) * blend;
        modelAir += ((!grounded ? 1 : 0) - modelAir) * blend;
        modelPhase = (modelPhase + distance * 2.8f) % MathF.Tau;
    }

    // Все детали рисуются в локальных координатах; матрицы вращают их вокруг суставов.
    static void ModelBox(float x, float y, float z, float width, float height, float depth, Color color) =>
        Raylib.DrawCube(new Vector3(x, y, z) * SkinPixel,
            width * SkinPixel, height * SkinPixel, depth * SkinPixel, color);

    static void DrawModelLimb(float x, float y, float angle, float roll, bool arm)
    {
        Rlgl.PushMatrix();
        Rlgl.Translatef(x * SkinPixel, y * SkinPixel, 0);
        Rlgl.Rotatef(angle, 1, 0, 0);
        Rlgl.Rotatef(roll, 0, 0, 1);
        if (arm)
        {
            ModelBox(0, -2, 0, 4, 4, 4, Col(35,151,164)); // Короткий рукав.
            ModelBox(0, -8, 0, 4, 8, 4, Col(181,133,100));
            ModelBox(0, -7, 2.02f, 2, 5, 0.06f, Col(193,144,108)); // Пиксельный блик кожи.
            ModelBox(0, -11, 0, 4.02f, 2, 4.02f, Col(169,119,88));
        }
        else
        {
            ModelBox(0, -5, 0, 4, 10, 4, Col(65,63,153));
            ModelBox(0, -5, 2.02f, 2, 6, 0.06f, Col(74,73,167));
            ModelBox(0, -11, 0, 4, 2, 4, Col(65,61,67)); // Отдельная полоса ботинка.
        }
        Rlgl.PopMatrix();
    }

    static void DrawAnimatedFriend()
    {
        if (!modelReady) return;
        float gait = MathF.Sin(modelPhase) * modelStride * 34;
        float idle = MathF.Sin(animationTime * 1.8f) * 2;
        float attack = friendSwing > 0 ? MathF.Sin(friendSwing / 0.26f * MathF.PI) : 0;
        Rlgl.PushMatrix();
        Rlgl.Translatef(modelPosition.X, modelPosition.Y, modelPosition.Z);
        // Локальная передняя сторона +Z совмещается с направлением взгляда в игровом мире.
        Rlgl.Rotatef(90 - modelYaw * 180 / MathF.PI, 0, 1, 0);
        float hip = 12 - modelCrouch * 1.5f;
        DrawModelLimb(-2, hip, gait - modelAir * 15 - modelCrouch * 8, 0, false);
        DrawModelLimb(2, hip, -gait + modelAir * 15 - modelCrouch * 8, 0, false);

        Rlgl.PushMatrix();
        Rlgl.Translatef(0, hip * SkinPixel, 0);
        Rlgl.Rotatef(modelCrouch * 25, 1, 0, 0);
        ModelBox(0, 6, 0, 8, 12, 4, Col(31,153,166));
        ModelBox(0, 5.5f, 2.03f, 4, 7, 0.06f, Col(28,139,153));
        ModelBox(0, 10.5f, 2.05f, 2, 3, 0.08f, Col(181,133,100)); // Вырез воротника.
        ModelBox(0, 1, 0, 8.04f, 2, 4.04f, Col(30,130,146));
        DrawModelLimb(-6, 12, -gait + idle - attack * 95, -3 - attack * 12, true);
        DrawModelLimb(6, 12, gait - idle, 3, true);

        Rlgl.PushMatrix();
        Rlgl.Translatef(0, 12 * SkinPixel, 0);
        // Компенсируем наклон корпуса: при приседании голова продолжает смотреть на цель.
        Rlgl.Rotatef(-modelPitch * 180 / MathF.PI - modelCrouch * 25, 1, 0, 0);
        ModelBox(0, 4, 0, 8, 8, 8, Col(181,133,100));
        ModelBox(0, 7, 0, 8.12f, 2, 8.12f, Col(57,39,28)); // Волосы сверху и на затылке.
        ModelBox(0, 4.5f, -3.8f, 8.12f, 5, 0.6f, Col(57,39,28));
        ModelBox(-3.7f, 5.5f, 0, 0.7f, 3, 8.05f, Col(65,44,30));
        ModelBox(3.7f, 5.5f, 0, 0.7f, 3, 8.05f, Col(65,44,30));
        ModelBox(-2, 4.5f, 4.04f, 2, 1, 0.1f, Col(230,229,222));
        ModelBox(2, 4.5f, 4.04f, 2, 1, 0.1f, Col(230,229,222));
        ModelBox(-1.5f, 4.5f, 4.11f, 1, 1, 0.08f, Col(75,65,142));
        ModelBox(1.5f, 4.5f, 4.11f, 1, 1, 0.08f, Col(75,65,142));
        ModelBox(0, 3, 4.08f, 2, 1, 0.25f, Col(151,102,76)); // Нос и борода крупными пикселями.
        ModelBox(0, 1.5f, 4.05f, 4, 2, 0.12f, Col(82,52,38));
        ModelBox(0, 2, 4.13f, 2, 1, 0.08f, Col(126,78,58));
        Rlgl.PopMatrix();
        Rlgl.PopMatrix();
        Rlgl.PopMatrix();
    }
}
