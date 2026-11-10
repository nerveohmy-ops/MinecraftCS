using Raylib_cs;
using System.Numerics;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    // Физика игрока
    const float Gravity = 30f, JumpSpeed = 8.6f;
    const float WalkSpeed = 4.3f, RunSpeed = 6.5f;
    const float HalfW = 0.3f;
    static float Height => crouching ? 1.5f : 1.8f;
    static float EyeHeight => crouching ? 1.32f : 1.62f;
    const float Eps = 0.0001f;
    const float ReachDist = 7f;                 // дальность рейкаста
    const float MouseSens = 0.0022f;

    static Vector3 pos;                         // pos.Y — уровень ног
    static Vector3 vel;
    static bool onGround;
    static float yaw, pitch;

    // Результат рейкаста
    static bool hasHit;
    static int hitX, hitY, hitZ, prevX, prevY, prevZ;

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

    static bool flying, crouching;

    static void PickBlock(byte block)
    {
        int slot = Array.IndexOf(Hotbar, block);
        if (slot >= 0) selected = slot;
    }

    static void UpdateMovementMode()
    {
        if (!survivalMode && Raylib.IsKeyPressed(KeyboardKey.F))
        {
            flying = !flying;
            vel = Vector3.Zero;
            ShowStatus(flying ? "Flight enabled" : "Flight disabled");
        }
        bool wasCrouching = crouching;
        crouching = !flying && Raylib.IsKeyDown(KeyboardKey.LeftControl);
        // Keep the shorter body until there is room to stand up.
        if (wasCrouching && !crouching && Collides()) crouching = true;
    }

    static float SafeSneakStep(float distance, bool xAxis)
    {
        Vector3 original = pos;
        float candidate = distance;
        // Reduce the horizontal step until the feet retain support.
        while (MathF.Abs(candidate) > 0.0001f)
        {
            pos = original + (xAxis ? new Vector3(candidate, -0.06f, 0) : new Vector3(0, -0.06f, candidate));
            bool supported = Collides();
            pos = original;
            if (supported) return candidate;
            candidate = MathF.Abs(candidate) <= 0.01f ? 0 : candidate - MathF.CopySign(0.01f, candidate);
        }
        return 0;
    }

    sealed class BlockParticle
    {
        public Vector3 Position, Velocity;
        public float Life, Size;
        public Color Color;
    }

    static readonly List<BlockParticle> blockParticles = new();
    static readonly Random animationRandom = new(723);
    static float animationTime, walkPhase, walkBlend, swingTime, equipDip, landingDip;
    static float friendSwing;
    static bool animationGrounded;
    static int animationSlot = -1;

    static void ResetAnimations()
    {
        blockParticles.Clear();
        swingTime = friendSwing = equipDip = landingDip = walkBlend = 0;
        walkPhase = animationTime = 0;
        ResetFriendModel(); // В новом мире поза и интерполяция начинаются заново.
        animationSlot = -1;
        animationGrounded = false;
    }

    static Color BlockColor(byte block) => block switch
    {
        Grass => Col(95, 156, 62), Dirt => Col(132, 91, 57),
        Sand => Col(221, 207, 145), Wood => Col(109, 78, 45),
        Leaves => Col(67, 128, 54), Planks => Col(181, 139, 83),
        Bricks => Col(171, 79, 61), _ => Col(140, 143, 147)
    };

    // Called only for accepted edits, never for terrain generation or save loading.
    static void AnimateBlockEdit(int x, int y, int z, byte before, byte after)
    {
        PlayBlockEditSound(x, y, z, before, after);
        if (before == after) return;
        Vector3 center = new(x + 0.5f, y + 0.5f, z + 0.5f);
        if (Vector3.DistanceSquared(center, pos) > 24 * 24) return;
        int count = after == Air ? 14 : 6;
        for (int i = 0; i < count; i++)
        {
            if (blockParticles.Count >= 192) blockParticles.RemoveAt(0);
            Vector3 direction = new(animationRandom.NextSingle() - 0.5f,
                animationRandom.NextSingle() * 0.7f, animationRandom.NextSingle() - 0.5f);
            blockParticles.Add(new BlockParticle {
                Position = center + direction * 0.8f,
                Velocity = direction * (after == Air ? 4 : 2),
                Life = after == Air ? 0.55f : 0.28f,
                Size = after == Air ? 0.09f : 0.055f,
                Color = BlockColor(after == Air ? before : after)
            });
        }
    }

    static void UpdateAnimations(float dt)
    {
        animationTime += dt;
        swingTime = MathF.Max(0, swingTime - dt);
        friendSwing = MathF.Max(0, friendSwing - dt);
        equipDip = MathF.Max(0, equipDip - dt * 4);
        landingDip *= MathF.Exp(-dt * 12);
        if (animationSlot != selected) { animationSlot = selected; equipDip = 1; }
        if (!animationGrounded && onGround && !flying) landingDip = 0.055f;
        animationGrounded = onGround;
        float speed = new Vector2(vel.X, vel.Z).Length();
        // Client snapshots intentionally omit velocity and grounded state.
        bool grounded = onGround;
        if (coopMatched && !coopHost)
        {
            int keys = ReadCoopKeys();
            speed = (keys & 15) != 0 ? (crouching ? 1.4f : (keys & 16) != 0 ? RunSpeed : WalkSpeed) : 0;
            grounded = Get((int)MathF.Floor(pos.X), (int)MathF.Floor(pos.Y - 0.06f),
                (int)MathF.Floor(pos.Z)) != Air;
        }
        float target = grounded && !flying ? Math.Clamp(speed / WalkSpeed, 0, 1.5f) : 0;
        walkBlend += (target - walkBlend) * (1 - MathF.Exp(-dt * 12));
        walkPhase = (walkPhase + dt * speed * 2.6f) % MathF.Tau;
        UpdateFriendModel(dt); // Поза напарника сглаживается отдельно от сетевой физики.
        for (int i = blockParticles.Count - 1; i >= 0; i--)
        {
            var p = blockParticles[i];
            p.Life -= dt;
            if (p.Life <= 0) { blockParticles.RemoveAt(i); continue; }
            p.Velocity.Y -= dt * 8;
            p.Position += p.Velocity * dt;
        }
    }

    static void DrawBlockParticles()
    {
        foreach (var p in blockParticles)
        {
            float size = p.Size * MathF.Min(1, p.Life * 8);
            Raylib.DrawCube(p.Position, size, size, size, p.Color);
        }
    }

    static Vector3 AnimationCameraOffset() => new(0,
        MathF.Sin(walkPhase * 2) * 0.012f * walkBlend - landingDip, 0);

    static void DrawFirstPersonHand()
    {
        // A separate near camera keeps the hand from intersecting the terrain.
        Camera3D camera = new() { Position = Vector3.Zero, Target = Vector3.UnitZ,
            Up = Vector3.UnitY, FovY = 70, Projection = CameraProjection.Perspective };
        Raylib.BeginMode3D(camera);
        Rlgl.DisableDepthTest();
        float swing = MathF.Sin((1 - swingTime / 0.26f) * MathF.PI);
        if (swingTime <= 0) swing = 0;
        Vector3 hand = new(-0.32f + swing * 0.15f,
            -0.27f - equipDip * 0.25f + MathF.Sin(walkPhase) * walkBlend * 0.018f
            + MathF.Sin(animationTime * 1.8f) * 0.004f + swing * 0.07f,
            0.67f + swing * 0.16f);
        Raylib.DrawCube(hand + new Vector3(0, -0.1f, -0.08f), 0.13f, 0.26f, 0.18f, Col(53,137,200));
        Raylib.DrawCube(hand, 0.14f, 0.16f, 0.18f, Col(231,174,120));
        Raylib.DrawCube(hand + new Vector3(0, 0.11f, 0.07f), 0.21f, 0.21f, 0.21f, BlockColor(Hotbar[selected]));
        Rlgl.EnableDepthTest();
        Raylib.EndMode3D();
    }
}
