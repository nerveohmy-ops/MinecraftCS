using Raylib_cs;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    static readonly string savesDirectory = Path.Combine(AppContext.BaseDirectory, "saves");
    static readonly string worldsDirectory = Path.Combine(savesDirectory, "worlds");
    static readonly string legacyWorldPath = Path.Combine(savesDirectory, "world.json");
    static bool titleOpen, createWorldOpen, createSurvival = true, editingSeed;
    static string worldNameInput = "", worldSeedInput = "";
    static string[] listedWorlds = [];
    static int worldListOffset;
    const int VisibleWorlds = 5;

    static bool ValidWorldName(string name) => name.Length is >= 1 and <= 24 &&
        name == name.Trim() && name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_');

    static string NamedWorldPath(string name) => Path.Combine(worldsDirectory, $"world-{name}.json");

    static void RefreshWorldList()
    {
        try
        {
            listedWorlds = !Directory.Exists(worldsDirectory) ? [] :
                Directory.EnumerateFiles(worldsDirectory, "world-*.json*")
                    .Select(Path.GetFileName)
                    .Select(file => file.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file)
                    .Where(file => file.StartsWith("world-") && file.EndsWith(".json"))
                    .Select(file => file[6..^5])
                    .Where(ValidWorldName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            worldListOffset = Math.Clamp(worldListOffset, 0, Math.Max(0, listedWorlds.Length - VisibleWorlds));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowStatus("Could not list saved worlds"); }
    }

    static SavedWorld NewWorldState(uint seed, bool survival) => new()
    {
        Version = 3, Height = H, Seed = seed, Survival = survival,
        Items = survival ? new int[ItemCount] : null,
        Chunks = new(), X = W / 2 + 0.5f, Z = D / 2 + 0.5f, Time = 0.15f
    };

    static void FinishWorldOpen(WorldStorage storage)
    {
        worldStorage = storage;
        titleOpen = createWorldOpen = paused = inventoryOpen = false;
        timePaused = false;
        autosaveTimer = 0;
        frame = 0;
        Raylib.DisableCursor();
        UpdateSun(0);
        RebuildDirtyChunks(int.MaxValue);
    }

    static bool OpenNamedWorld(string path, bool allowEmpty = false)
    {
        var storage = new WorldStorage(path);
        var saved = storage.Read();
        if (saved == null && (storage.Blocked || !allowEmpty))
        {
            ShowStatus("Could not open this world. Save files preserved.");
            return false;
        }
        ReplaceCoopWorld(saved ?? NewWorldState(DefaultSeed, false));
        FinishWorldOpen(storage);
        if (storage.Recovered) ShowStatus("World loaded from backup");
        return true;
    }

    static void CreateNamedWorld()
    {
        string name = worldNameInput.Trim();
        if (!ValidWorldName(name))
        { ShowStatus("Enter a world name (letters, numbers, spaces, - or _)"); return; }
        string path = NamedWorldPath(name);
        if (File.Exists(path) || File.Exists(path + ".bak"))
        { ShowStatus("A world with that name already exists"); return; }
        uint seed;
        if (worldSeedInput.Length == 0) seed = (uint)Random.Shared.NextInt64(1L << 32);
        else if (!uint.TryParse(worldSeedInput, out seed))
        { ShowStatus("Seed must be 0 to 4294967295"); return; }

        var storage = new WorldStorage(path);
        ReplaceCoopWorld(NewWorldState(seed, createSurvival));
        if (!storage.TryWrite(CaptureWorld(), out string error, createOnly: true))
        { ShowStatus(error); return; }
        FinishWorldOpen(storage);
    }

    static void ReturnToTitle()
    {
        if (soloWorld == null && !TrySaveActiveWorld(worldStorage, out string error))
        { ShowStatus(error); return; }
        if (steamReady) EndCoop("", closing: true);
        StopAmbientSounds();
        ResetGameplayAudio();
        foreach (var chunk in chunks.Values) UnloadChunkMeshes(chunk);
        chunks.Clear();
        savedChunks.Clear();
        survivalMobs.Clear();
        coopPanel = shaderMenuOpen = inventoryOpen = paused = false;
        titleOpen = true;
        createWorldOpen = false;
        Raylib.EnableCursor();
        RefreshWorldList();
    }

    static void UpdateTitle()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.F11)) ToggleFullscreenMode();
        if (!createWorldOpen)
        {
            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0) worldListOffset = Math.Clamp(worldListOffset - Math.Sign(wheel), 0,
                Math.Max(0, listedWorlds.Length - VisibleWorlds));
            return;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { createWorldOpen = false; return; }
        if (Raylib.IsKeyPressed(KeyboardKey.Tab)) editingSeed = !editingSeed;
        ref string field = ref (editingSeed ? ref worldSeedInput : ref worldNameInput);
        if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && field.Length > 0) field = field[..^1];
        for (int c = Raylib.GetCharPressed(); c != 0; c = Raylib.GetCharPressed())
        {
            if (c > 127 || field.Length >= (editingSeed ? 10 : 24)) continue;
            char key = (char)c;
            if (editingSeed ? char.IsAsciiDigit(key) : char.IsAsciiLetterOrDigit(key) || key is ' ' or '-' or '_')
                field += key;
        }
    }

    static void DrawTitle()
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        int x = w / 2 - 240, y = h / 2 - 245;
        Rectangle Button(int row) => new(x, y + 85 + row * 44, 480, 38);
        Raylib.BeginDrawing();
        Raylib.ClearBackground(Col(20, 32, 41));
        Raylib.DrawRectangle(x - 26, y - 20, 532, 510, Col(27, 41, 52));
        Raylib.DrawText("MinecraftCS", x, y + 5, 42, Col(236, 244, 235));
        Raylib.DrawText(createWorldOpen ? "CREATE WORLD" : "WORLD SELECT", x + 2, y + 55, 17, Col(174, 207, 195));

        if (createWorldOpen)
        {
            if (DrawMenuButton($"{(!editingSeed ? "> " : "")}Name: {worldNameInput}", Button(0))) editingSeed = false;
            if (DrawMenuButton($"{(editingSeed ? "> " : "")}Seed: {(worldSeedInput == "" ? "Random" : worldSeedInput)}", Button(1)))
                editingSeed = true;
            if (DrawMenuButton(createSurvival ? "Mode: Survival" : "Mode: Creative", Button(2))) createSurvival = !createSurvival;
            if (DrawMenuButton("Create world", Button(4)) || Raylib.IsKeyPressed(KeyboardKey.Enter)) CreateNamedWorld();
            if (createWorldOpen && DrawMenuButton("Back", Button(5))) createWorldOpen = false;
        }
        else
        {
            if (DrawMenuButton("Create new world", Button(0)))
            {
                createWorldOpen = true;
                editingSeed = false;
                worldNameInput = worldSeedInput = "";
            }
            if (!createWorldOpen && DrawMenuButton("Classic world", Button(1))) OpenNamedWorld(legacyWorldPath, allowEmpty: true);
            for (int i = 0; titleOpen && !createWorldOpen && i < VisibleWorlds && worldListOffset + i < listedWorlds.Length; i++)
            {
                string name = listedWorlds[worldListOffset + i];
                if (DrawMenuButton(name, Button(i + 2))) OpenNamedWorld(NamedWorldPath(name));
            }
            if (listedWorlds.Length > VisibleWorlds)
                Raylib.DrawText($"Scroll  {worldListOffset + 1}-{Math.Min(worldListOffset + VisibleWorlds, listedWorlds.Length)} / {listedWorlds.Length}",
                    x, y + 437, 16, Col(174, 207, 195));
            if (titleOpen && DrawMenuButton("Quit", new Rectangle(x + 350, y + 421, 130, 36))) quitRequested = true;
        }
        if (Raylib.GetTime() < statusUntil)
            Raylib.DrawText(status, Math.Max(8, (w - Raylib.MeasureText(status, 19)) / 2), y + 500, 19, Col(255, 221, 149));
        Raylib.EndDrawing();
    }
}
