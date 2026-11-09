using System.Numerics;
using Raylib_cs;
using Steamworks;

namespace MinecraftCS;

internal static unsafe partial class Program
{
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
}
