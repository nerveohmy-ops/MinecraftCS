using System.Numerics;
using Raylib_cs;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    static Texture2D pigSkin, zombieSkin;
    static readonly Texture2D[] survivalIcons = new Texture2D[ItemCount];

    static Texture2D LoadFaithfulTexture(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "Faithful", file);
        if (!File.Exists(path)) return default;
        Texture2D texture = Raylib.LoadTexture(path);
        if (texture.Id != 0) Raylib.SetTextureFilter(texture, TextureFilter.Point);
        return texture;
    }

    static void LoadSurvivalTextures()
    {
        pigSkin = LoadFaithfulTexture("mobs/pig_temperate.png");
        zombieSkin = LoadFaithfulTexture("mobs/zombie.png");
        survivalIcons[CoalOre] = LoadFaithfulTexture("items/coal.png");
        survivalIcons[IronOre] = LoadFaithfulTexture("items/raw_iron.png");
        survivalIcons[GoldOre] = LoadFaithfulTexture("items/raw_gold.png");
        survivalIcons[Pickaxe] = LoadFaithfulTexture("items/iron_pickaxe.png");
        survivalIcons[RawMeat] = LoadFaithfulTexture("items/porkchop.png");
        survivalIcons[CookedMeat] = LoadFaithfulTexture("items/cooked_porkchop.png");
        survivalIcons[GoldenMeal] = LoadFaithfulTexture("items/golden_apple.png");
    }

    static void UnloadSurvivalTextures()
    {
        if (pigSkin.Id != 0) Raylib.UnloadTexture(pigSkin);
        if (zombieSkin.Id != 0) Raylib.UnloadTexture(zombieSkin);
        foreach (var icon in survivalIcons)
            if (icon.Id != 0) Raylib.UnloadTexture(icon);
    }

    static void DrawItemIcon(int item, int x, int y)
    {
        Texture2D icon = survivalIcons[item];
        if (icon.Id != 0)
            Raylib.DrawTexturePro(icon, new Rectangle(0, 0, icon.Width, icon.Height),
                new Rectangle(x, y, 22, 22), Vector2.Zero, 0, Color.White);
    }

    static void DrawInventoryResource(int item, string name, int x, int y)
    {
        DrawItemIcon(item, x, y);
        Raylib.DrawText($"{name} {items[item]}", x + 25, y + 3, 17, Col(224, 236, 241));
    }

    static bool DrawTexturedMob(SurvivalMob mob)
    {
        Texture2D skin = mob.Hostile ? zombieSkin : pigSkin;
        if (skin.Id == 0) return false;

        // The 32x pack doubles the pixels of the 64x64 entity UV layout.
        Vector3 forward = new(MathF.Cos(mob.Direction), 0, MathF.Sin(mob.Direction));
        Vector3 right = new(-forward.Z, 0, forward.X);
        Rlgl.SetTexture(skin.Id);
        Rlgl.Begin((int)DrawMode.Quads);
        void Part(Vector3 min, Vector3 size, int u, int v, int w, int h, int d, bool rotated = false) =>
            DrawSkinBox(mob.Position, forward, right, min, size, u, v, w, h, d, rotated);

        if (mob.Hostile)
        {
            Part(new(-0.21f, 1.22f, -0.21f), new(0.42f, 0.42f, 0.42f), 0, 0, 8, 8, 8);
            Part(new(-0.21f, 0.60f, -0.11f), new(0.42f, 0.63f, 0.22f), 16, 16, 8, 12, 4);
            Part(new(-0.21f, 0, -0.11f), new(0.21f, 0.61f, 0.22f), 0, 16, 4, 12, 4);
            Part(new(0, 0, -0.11f), new(0.21f, 0.61f, 0.22f), 0, 16, 4, 12, 4);
            Part(new(-0.42f, 0.65f, -0.15f), new(0.21f, 0.59f, 0.22f), 40, 16, 4, 12, 4);
            Part(new(0.21f, 0.65f, -0.15f), new(0.21f, 0.59f, 0.22f), 40, 16, 4, 12, 4);
        }
        else
        {
            Part(new(-0.31f, 0.30f, -0.38f), new(0.62f, 0.47f, 0.90f), 28, 8, 10, 16, 8, rotated: true);
            Part(new(-0.23f, 0.34f, -0.65f), new(0.46f, 0.46f, 0.46f), 0, 0, 8, 8, 8);
            Part(new(-0.13f, 0.42f, -0.71f), new(0.26f, 0.16f, 0.08f), 16, 16, 4, 3, 1);
            for (int side = -1; side <= 1; side += 2)
            for (int end = -1; end <= 1; end += 2)
                Part(new(side * 0.17f - 0.11f, 0, end * 0.26f - 0.08f), new(0.22f, 0.33f, 0.22f),
                    0, 16, 4, 6, 4);
        }

        Rlgl.End();
        Rlgl.SetTexture(0);
        return true;
    }

    static void DrawSkinBox(Vector3 origin, Vector3 forward, Vector3 right, Vector3 min, Vector3 size,
        int u, int v, int width, int height, int depth, bool rotated)
    {
        for (int face = 0; face < 6; face++)
        {
            var (left, top, uw, vh) = face switch
            {
                0 => (u + depth + width, v + depth, depth, height),
                1 => (u, v + depth, depth, height),
                2 => (u + depth, v, width, depth),
                3 => (u + depth + width, v, width, depth),
                4 => (u + depth * 2 + width, v + depth, width, height),
                _ => (u + depth, v + depth, width, height)
            };
            byte light = face == 2 ? (byte)255 : face == 3 ? (byte)157 : (byte)224;
            Rlgl.Color4ub(light, light, light, 255);
            for (int vertex = 0; vertex < 4; vertex++)
            {
                int x = FaceVerts[face, vertex, 0], y = FaceVerts[face, vertex, 1], z = FaceVerts[face, vertex, 2];
                int s = face switch { 0 => 1 - z, 1 => z, 4 => 1 - x, _ => x };
                int t = face is 2 or 3 ? z : 1 - y;
                // Half a source texel keeps adjacent UV islands out of the sample.
                const float inset = 0.25f;
                Rlgl.TexCoord2f((left + inset + s * (uw - 2 * inset)) / 64f,
                    (top + inset + t * (vh - 2 * inset)) / 64f);
                Vector3 local = min + size * (rotated ? new Vector3(x, z, 1 - y) : new Vector3(x, y, z));
                Vector3 world = origin + right * local.X + Vector3.UnitY * local.Y - forward * local.Z;
                Rlgl.Vertex3f(world.X, world.Y, world.Z);
            }
        }
    }
}
