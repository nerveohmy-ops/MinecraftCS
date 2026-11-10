using System.Numerics;
using Raylib_cs;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    const int MaxHealth = 20, MaxStack = 999;
    const int Pickaxe = 13, RawMeat = 14, CookedMeat = 15, GoldenMeal = 16, ItemCount = 17;
    static bool survivalMode;
    static int health = MaxHealth;
    static readonly int[] items = new int[ItemCount];
    static readonly List<SurvivalMob> survivalMobs = new();
    static Random mobRandom = new();
    static float spawnDelay, damageCooldown;
    static bool spawnHostile;

    sealed class SurvivalMob(Vector3 position, bool hostile, float direction)
    {
        public Vector3 Position = position;
        public readonly bool Hostile = hostile;
        public float Direction = direction;
        public int Health = hostile ? 6 : 3;
    }

    sealed record Recipe(string Label, int Output, int Count, (int Item, int Count)[] Ingredients);

    static readonly Recipe[] recipes =
    [
        new("4 Planks  /  1 Log", Planks, 4, [(Wood, 1)]),
        new("Iron pickaxe  /  3 Iron + 2 Planks + 1 Coal", Pickaxe, 1, [(IronOre, 3), (Planks, 2), (CoalOre, 1)]),
        new("4 Bricks  /  4 Stone + 1 Coal", Bricks, 4, [(Stone, 4), (CoalOre, 1)]),
        new("Cooked meat  /  1 Meat + 1 Coal", CookedMeat, 1, [(RawMeat, 1), (CoalOre, 1)]),
        new("Golden meal  /  1 Cooked meat + 1 Gold", GoldenMeal, 1, [(CookedMeat, 1), (GoldOre, 1)])
    ];

    static byte OreAt(int x, int y, int z)
    {
        if (y < 2 || y >= 44) return Stone;
        // Hash coarse cells so deposits occur in small veins, including across chunk borders.
        uint vein = Hash(x >> 1, y >> 1, z >> 1) % 100;
        if (y < 15 && vein < 2) return GoldOre;
        if (y < 30 && vein < 6) return IronOre;
        return vein < 12 ? CoalOre : Stone;
    }

    static void RestoreSurvival(SavedWorld world)
    {
        survivalMode = world.Survival;
        health = survivalMode ? world.Health : MaxHealth;
        Array.Clear(items);
        if (survivalMode && world.Items != null) world.Items.CopyTo(items, 0);
        survivalMobs.Clear();
        mobRandom = new Random(unchecked((int)world.Seed));
        spawnDelay = 0.5f;
        damageCooldown = 0;
        spawnHostile = false;
    }

    static bool CanCraft(Recipe recipe) =>
        items[recipe.Output] <= MaxStack - recipe.Count &&
        recipe.Ingredients.All(ingredient => items[ingredient.Item] >= ingredient.Count);

    static bool TryCraft(Recipe recipe)
    {
        if (!CanCraft(recipe)) return false;
        foreach (var ingredient in recipe.Ingredients) items[ingredient.Item] -= ingredient.Count;
        items[recipe.Output] += recipe.Count;
        return true;
    }

    static void SurvivalEdit(int x, int y, int z, byte block)
    {
        if (!survivalMode) { CommitCoopBlock(x, y, z, block); return; }
        if (y <= 0 || y >= H || !chunks.ContainsKey((FloorChunk(x), FloorChunk(z)))) return;
        byte previous = Get(x, y, z);
        if (block == Air)
        {
            if (previous == Air || items[previous] == MaxStack) return;
            if (previous == GoldOre && items[Pickaxe] == 0)
            { ShowStatus("Gold requires an iron pickaxe"); return; }
            CommitCoopBlock(x, y, z, Air);
            items[previous]++;
        }
        else if (previous == Air && items[block] > 0 && !IntersectsPlayer(x, y, z) &&
                 !survivalMobs.Any(mob => Raylib.CheckCollisionBoxes(MobBounds(mob), new BoundingBox(new Vector3(x, y, z), new Vector3(x + 1, y + 1, z + 1)))))
        {
            CommitCoopBlock(x, y, z, block);
            items[block]--;
        }
    }

    static BoundingBox MobBounds(SurvivalMob mob)
    {
        Vector3 p = mob.Position;
        float height = mob.Hostile ? 1.65f : 0.85f;
        return new BoundingBox(p - new Vector3(0.35f, 0, 0.35f), p + new Vector3(0.35f, height, 0.35f));
    }

    static bool AttackSurvivalMob(Vector3 eye)
    {
        if (!survivalMode || !Raylib.IsMouseButtonPressed(MouseButton.Left)) return false;
        Ray ray = new(eye, LookDir());
        float limit = 4.5f;
        if (hasHit)
        {
            Vector3 block = new(hitX, hitY, hitZ);
            var hit = Raylib.GetRayCollisionBox(ray, new BoundingBox(block, block + Vector3.One));
            if (hit.Hit) limit = MathF.Min(limit, hit.Distance);
        }
        int target = -1;
        for (int i = 0; i < survivalMobs.Count; i++)
        {
            var hit = Raylib.GetRayCollisionBox(ray, MobBounds(survivalMobs[i]));
            if (hit.Hit && hit.Distance < limit) { target = i; limit = hit.Distance; }
        }
        if (target < 0) return false;

        var mob = survivalMobs[target];
        mob.Health -= items[Pickaxe] > 0 ? 3 : 1;
        if (mob.Health <= 0)
        {
            if (!mob.Hostile) items[RawMeat] = Math.Min(MaxStack, items[RawMeat] + 1);
            survivalMobs.RemoveAt(target);
        }
        return true;
    }

    static void UpdateSurvival(float dt)
    {
        if (!survivalMode) return;
        damageCooldown = MathF.Max(0, damageCooldown - dt);
        spawnDelay -= dt;
        if (spawnDelay <= 0 && survivalMobs.Count < 10)
        {
            spawnDelay = 2.5f;
            float angle = mobRandom.NextSingle() * MathF.Tau;
            int x = (int)MathF.Floor(pos.X + MathF.Cos(angle) * 12f);
            int z = (int)MathF.Floor(pos.Z + MathF.Sin(angle) * 12f);
            int ground = TopY(x, z);
            if (ground >= AddedDepth && Get(x, ground, z) is Grass or Sand or Dirt or Stone)
            {
                survivalMobs.Add(new SurvivalMob(new Vector3(x + 0.5f, ground + 1, z + 0.5f), spawnHostile, angle));
                spawnHostile = !spawnHostile;
            }
        }
        for (int i = survivalMobs.Count - 1; i >= 0; i--)
        {
            var mob = survivalMobs[i];
            Vector3 toPlayer = pos - mob.Position;
            if (toPlayer.LengthSquared() > 48f * 48f) { survivalMobs.RemoveAt(i); continue; }
            if (mob.Hostile && toPlayer.LengthSquared() < 1.3f * 1.3f && damageCooldown == 0)
            {
                Vector3 attack = mob.Position + Vector3.UnitY * 0.8f;
                Vector3 target = pos + Vector3.UnitY * 0.8f - attack;
                float distance = target.Length();
                // Raycast mutates the shared block hit; restore it after the line-of-sight check.
                var previous = (hitX, hitY, hitZ, prevX, prevY, prevZ);
                bool blocked = distance > 0.001f && Raycast(attack, target / distance, distance);
                (hitX, hitY, hitZ, prevX, prevY, prevZ) = previous;
                if (!blocked)
                {
                    health -= 2;
                    damageCooldown = 1f;
                    if (health <= 0)
                    {
                        health = MaxHealth;
                        GenerateWorld();
                        SpawnPlayer();
                        survivalMobs.Clear();
                        ShowStatus("You respawned");
                        return;
                    }
                }
            }

            if (mob.Hostile && toPlayer.X * toPlayer.X + toPlayer.Z * toPlayer.Z > 0.7f)
                mob.Direction = MathF.Atan2(toPlayer.Z, toPlayer.X);
            float speed = mob.Hostile ? 1.8f : 0.75f;
            Vector3 next = mob.Position + new Vector3(MathF.Cos(mob.Direction), 0, MathF.Sin(mob.Direction)) * (speed * dt);
            int x = (int)MathF.Floor(next.X), z = (int)MathF.Floor(next.Z);
            int surface = TopY(x, z);
            if (surface >= 0 && MathF.Abs(surface + 1 - mob.Position.Y) <= 1)
                mob.Position = new Vector3(next.X, surface + 1, next.Z);
            else if (!mob.Hostile) mob.Direction += 1.5f;
        }
    }

    static void DrawSurvivalMobs()
    {
        foreach (var mob in survivalMobs)
        {
            if (DrawTexturedMob(mob)) continue;
            Vector3 p = mob.Position;
            if (mob.Hostile)
            {
                Raylib.DrawCube(p + Vector3.UnitY * 0.85f, 0.55f, 1.25f, 0.42f, Col(48, 104, 89));
                Raylib.DrawCube(p + Vector3.UnitY * 1.46f, 0.46f, 0.42f, 0.46f, Col(106, 146, 82));
            }
            else
            {
                Raylib.DrawCube(p + Vector3.UnitY * 0.44f, 0.67f, 0.6f, 0.72f, Col(222, 143, 135));
                Raylib.DrawCube(p + new Vector3(0.25f, 0.55f, 0), 0.45f, 0.41f, 0.44f, Col(238, 163, 151));
            }
        }
    }

    static void DrawSurvivalHud()
    {
        string label = $"Health {health}/{MaxHealth}";
        int x = (Raylib.GetScreenWidth() - Raylib.MeasureText(label, 20)) / 2;
        Raylib.DrawText(label, x, Raylib.GetScreenHeight() - 130, 20, Col(255, 185, 163));
    }

    static void DrawSurvivalInventory()
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        int x = w / 2 - 310, y = h / 2 - 240;
        Raylib.DrawRectangle(0, 0, w, h, Col(0, 0, 0, 165));
        Raylib.DrawRectangle(x, y, 620, 480, Col(28, 35, 42));
        Raylib.DrawText("Inventory and crafting", x + 20, y + 18, 25, Col(255, 255, 255));
        for (int i = 0; i < Hotbar.Length; i++)
        {
            Rectangle slot = new(x + 20 + i % 3 * 194, y + 58 + i / 3 * 35, 184, 30);
            if (DrawMenuButton($"{BlockNames[i]} ({items[Hotbar[i]]})", slot)) selected = i;
        }
        DrawInventoryResource(CoalOre, "Coal", x + 20, y + 172);
        DrawInventoryResource(IronOre, "Iron", x + 136, y + 172);
        DrawInventoryResource(GoldOre, "Gold", x + 252, y + 172);
        DrawInventoryResource(RawMeat, "Meat", x + 368, y + 172);
        DrawInventoryResource(Pickaxe, "Pick", x + 484, y + 172);
        for (int i = 0; i < recipes.Length; i++)
        {
            var recipe = recipes[i];
            string label = CanCraft(recipe) ? recipe.Label : $"Missing: {recipe.Label}";
            if (DrawMenuButton(label, new Rectangle(x + 20, y + 206 + i * 37, 580, 32))) TryCraft(recipe);
        }
        if (DrawMenuButton($"Eat cooked meat ({items[CookedMeat]})", new Rectangle(x + 20, y + 400, 275, 34)))
            EatSurvivalFood(CookedMeat, 5);
        if (DrawMenuButton($"Eat golden meal ({items[GoldenMeal]})", new Rectangle(x + 305, y + 400, 295, 34)))
            EatSurvivalFood(GoldenMeal, 10);
        DrawItemIcon(CookedMeat, x + 28, y + 406);
        DrawItemIcon(GoldenMeal, x + 313, y + 406);
        Raylib.DrawText("E / Esc to close", x + 20, y + 448, 17, Col(191, 202, 213));
    }

    static void EatSurvivalFood(int item, int amount)
    {
        if (items[item] == 0 || health == MaxHealth) return;
        items[item]--;
        health = Math.Min(MaxHealth, health + amount);
    }
}
