using System.Numerics;
using Raylib_cs;

namespace MinecraftCS;

internal static unsafe partial class Program
{
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
