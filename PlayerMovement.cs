using System.Numerics;
using Raylib_cs;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    static bool flying, crouching;

    static void PickBlock(byte block)
    {
        int slot = Array.IndexOf(Hotbar, block);
        if (slot >= 0) selected = slot;
    }

    static void UpdateMovementMode()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.F))
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
}
