using Microsoft.Win32;
using Raylib_cs;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    const int WindowW = 1280, WindowH = 720;
    static string gpuName = "unknown";

    static int frame;
    static bool diagnosticRender;

    // ════════════════════════ ВИДЕОКАРТА И ЭКРАН ════════════════════════
    [DllImport("opengl32.dll", EntryPoint = "glGetString")]
    static extern IntPtr GlGetString(uint name);

    static int monitorHz;

    // Имя GPU, который реально создал OpenGL-контекст (0x1F01 = GL_RENDERER)
    static string QueryGpuName()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return "unknown";
            IntPtr ptr = GlGetString(0x1F01);
            return ptr == IntPtr.Zero ? "unknown" : Marshal.PtrToStringAnsi(ptr) ?? "unknown";
        }
        catch { return "unknown"; }
    }

    // Windows сам выбирает GPU для каждой программы (то же, что «Параметры → Система → Дисплей → Графика →
    // Высокая производительность»). Записываем это предпочтение для MinecraftCS.exe и один раз
    // перезапускаемся, чтобы окно сразу создавалось на дискретной карте (Nvidia / AMD).
    // Отключить: запуск с аргументом --no-gpu-setup.
    static void EnsureDiscreteGpu(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Contains("--no-gpu-setup") || args.Contains("--gpu-ready")) return;
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "MinecraftCS.exe");
            if (!File.Exists(exe)) return;

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            string cur = key.GetValue(exe) as string ?? "";
            var parts = cur.Split(';', StringSplitOptions.RemoveEmptyEntries)
                           .Where(x => !x.StartsWith("GpuPreference=")).ToList();
            parts.Add("GpuPreference=2");
            string want = string.Join(";", parts) + ";";
            bool changed = cur != want;
            if (changed) key.SetValue(exe, want, RegistryValueKind.String);

            bool isThatExe = string.Equals(Environment.ProcessPath, exe, StringComparison.OrdinalIgnoreCase);
            if (isThatExe && !changed) return;   // уже запущены как MinecraftCS.exe с нужной настройкой

            ProcessStartInfo psi = new(exe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--gpu-ready");
            using Process child = Process.Start(psi);
            if (child == null) return;
            child.WaitForExit();
            Environment.Exit(child.ExitCode);
        }
        catch { /* не вышло — играем на том GPU, который выбрал Windows */ }
    }

    static void ToggleFullscreenMode()
    {
        if (!Raylib.IsWindowFullscreen())
        {
            int m = Raylib.GetCurrentMonitor();
            Raylib.SetWindowSize(Raylib.GetMonitorWidth(m), Raylib.GetMonitorHeight(m));
            Raylib.ToggleFullscreen();
        }
        else
        {
            Raylib.ToggleFullscreen();
            Raylib.SetWindowSize(WindowW, WindowH);
        }
        monitorHz = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
        frame = 0;   // пропустить «скачок» мыши после смены режима
    }

    static void ToggleVSync()
    {
        if (Raylib.IsWindowState(ConfigFlags.VSyncHint)) Raylib.ClearWindowState(ConfigFlags.VSyncHint);
        else Raylib.SetWindowState(ConfigFlags.VSyncHint);
    }

    // ════════════════════════ ГЛАВНЫЙ ЦИКЛ ════════════════════════
    static void Update(float dt)
    {
        PollCoop(dt);
        if (coopPanel) { UpdateCoopMenu(); return; }
        if (shaderMenuOpen) { UpdateShaderMenu(); return; }
        if (Raylib.IsKeyPressed(KeyboardKey.F3)) debugHud = !debugHud;
        if (Raylib.IsKeyPressed(KeyboardKey.F4)) showChunkBorders = !showChunkBorders;
        if (!Raylib.IsWindowFocused())
        {
            inventoryOpen = false;
            SetPaused(true);
            return;
        }

        bool escape = Raylib.IsKeyPressed(KeyboardKey.Escape);
        bool inventoryKey = Raylib.IsKeyPressed(KeyboardKey.E);
        if (Raylib.IsKeyPressed(KeyboardKey.F5)) SaveWorld();
        if (paused)
        {
            if (escape) SetPaused(false);
            else if (Raylib.IsKeyPressed(KeyboardKey.S)) OpenShaderMenu();
            else if (Raylib.IsKeyPressed(KeyboardKey.M)) coopPanel = true;
            else if (Raylib.IsKeyPressed(KeyboardKey.Q)) quitRequested = true;
            return;
        }
        if (inventoryOpen || inventoryKey)
        {
            if (inventoryKey || escape) SetInventoryOpen(!inventoryOpen);
            return;
        }
        if (escape)
        {
            SetPaused(true);
            return;
        }

        autosaveTimer += dt;
        if (autosaveTimer >= 30f) { SaveWorld(); autosaveTimer = 0; }
        bool altEnter = (Raylib.IsKeyDown(KeyboardKey.LeftAlt) || Raylib.IsKeyDown(KeyboardKey.RightAlt))
                        && Raylib.IsKeyPressed(KeyboardKey.Enter);
        if (Raylib.IsKeyPressed(KeyboardKey.F11) || altEnter) ToggleFullscreenMode();
        if (Raylib.IsKeyPressed(KeyboardKey.V)) ToggleVSync();
        if (Raylib.IsKeyPressed(KeyboardKey.T)) timePaused = !timePaused;
        if (Raylib.IsKeyPressed(KeyboardKey.O)) softShadows = !softShadows;
        if (Raylib.IsKeyPressed(KeyboardKey.G)) shadowsOn = !shadowsOn;

        frame++;
        Vector2 md = Raylib.GetMouseDelta();
        if (frame > 3 && !diagnosticRender)   // первые кадры после DisableCursor дают «скачок» мыши
        {
            yaw += md.X * MouseSens;
            pitch = Math.Clamp(pitch - md.Y * MouseSens, -1.55f, 1.55f);
        }

        for (int i = 0; i < Hotbar.Length; i++)
            if (Raylib.IsKeyPressed((KeyboardKey)((int)KeyboardKey.One + i))) selected = i;
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0) selected = ((selected - Math.Sign(wheel)) % Hotbar.Length + Hotbar.Length) % Hotbar.Length;

        StreamChunks();
        if (Raylib.IsMouseButtonPressed(MouseButton.Left) || Raylib.IsMouseButtonPressed(MouseButton.Right))
            swingTime = 0.26f;
        if (!coopMatched || coopHost) { UpdatePlayer(dt); UpdateSun(dt); }
        else SendPlayerInput();
        UpdateAnimations(dt);

        Vector3 eye = new(pos.X, pos.Y + EyeHeight, pos.Z);
        hasHit = Raycast(eye, LookDir(), ReachDist);
        if (hasHit && (!coopMatched || coopHost))
        {
            if (Raylib.IsMouseButtonPressed(MouseButton.Middle))
                PickBlock(Get(hitX, hitY, hitZ));
            if (Raylib.IsMouseButtonPressed(MouseButton.Left) && hitY > 0)
                CommitCoopBlock(hitX, hitY, hitZ, Air);
            else if (Raylib.IsMouseButtonPressed(MouseButton.Right) &&
                     Get(prevX, prevY, prevZ) == Air && !IntersectsPlayer(prevX, prevY, prevZ))
                CommitCoopBlock(prevX, prevY, prevZ, Hotbar[selected]);
        }

        RebuildDirtyChunks();
        FlushOccupancy();
    }

    static void Main(string[] args)
    {
        if (args.Contains("--self-test")) { RunSelfTests(); return; }
        if (args.Contains("--steam-check"))
        {
            InitCoop();
            Console.WriteLine(coopStatus);
            if (steamReady) Steamworks.SteamAPI.Shutdown();
            else Environment.ExitCode = 2;
            return;
        }
        diagnosticRender = args.Contains("--benchmark") || args.Contains("--render-test");
        bool benchmark = args.Contains("--benchmark");
        bool renderTest = args.Contains("--render-test") || benchmark;
        if (!renderTest) EnsureDiscreteGpu(args);   // обязательно до создания окна и контекста OpenGL

        if (!renderTest) InitCoop();
        Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint);
        Raylib.InitWindow(WindowW, WindowH, "MinecraftCS");
        if (!renderTest) InitAmbientAudio();
        Raylib.SetExitKey(KeyboardKey.Null);
        Raylib.SetTargetFPS(0); // без ограничения FPS
        gpuName = QueryGpuName();
        monitorHz = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
        if (args.Contains("--fullscreen") || args.Contains("-f")) ToggleFullscreenMode();

        atlas = MakeAtlas();
        material = Raylib.LoadMaterialDefault();
        material.Maps[(int)MaterialMapIndex.Albedo].Texture = atlas;

        GenerateWorld();
        SpawnPlayer();
        if (!renderTest) LoadWorld();
        foreach (var key in NearbyChunkIndices(2)) GenerateChunk(key.X, key.Z);
        UpdateOccupancyOrigin();
        UpdateSun(0);
        BuildOccupancy();
        CreateOccupancyTexture();
        InitLighting();
        InitShaderManager();
        RebuildDirtyChunks(int.MaxValue);
        if (renderTest)
        {
            pitch = -0.65f;
            shadowsOn = benchmark;
            timePaused = true;
        }
        if (benchmark)
        {
            foreach (var key in NearbyChunkIndices(renderRadius)) GenerateChunk(key.X,key.Z);
            RebuildDirtyChunks(int.MaxValue);
        }
        double benchSeconds = 0;
        int benchFrames = 0;
        Raylib.DisableCursor();

        ulong launchLobby = ParseLobbyArgument(args);
        if (!renderTest && launchLobby != 0)
        {
            if (steamReady) JoinCoop(new Steamworks.CSteamID(launchLobby));
            else { paused = coopPanel = true; Raylib.EnableCursor(); }
        }
#if STEAM_TEST
        else if (!renderTest && args.Contains("--host-steam"))
        {
            paused = coopPanel = true;
            Raylib.EnableCursor();
            CreateCoop();
        }
#endif

        while (!Raylib.WindowShouldClose() && !quitRequested)
        {
            Update(MathF.Min(Raylib.GetFrameTime(), 0.05f));
            UpdateAmbientAudio(MathF.Min(Raylib.GetFrameTime(), 0.05f));
            UpdateGameplayAudio();
            Draw();
            if (benchmark && frame > 60) { benchSeconds += Raylib.GetFrameTime(); benchFrames++; }
            if (renderTest && frame >= (benchmark ? 300 : 40))
            {
                Raylib.TakeScreenshot(Path.Combine(Environment.CurrentDirectory,"lighting-preview.png"));
                if (benchmark) Console.WriteLine($"BENCHMARK: {benchSeconds * 1000 / benchFrames:F2} ms/frame, {benchFrames / benchSeconds:F1} FPS");
                Console.WriteLine($"RENDER CHECK: shader={shaderOk}, pack={currentShaderPack}, GPU={gpuName}, fullscreen={Raylib.IsWindowFullscreen()}, resolution={Raylib.GetRenderWidth()}x{Raylib.GetRenderHeight()}");
                if (!shaderOk) Environment.ExitCode = 1;
                break;
            }
        }

        if (!renderTest) SaveWorld();
        if (steamReady)
        {
            EndCoop("", closing:true);
            inviteCallback?.Dispose();
            connectionCallback?.Dispose();
            createResult?.Dispose();
            joinResult?.Dispose();
            Steamworks.SteamAPI.Shutdown();
        }
        foreach (var chunk in chunks.Values)
            UnloadChunkMeshes(chunk);
        Raylib.UnloadTexture(atlas);
        Raylib.UnloadTexture(occTex);
        if (dirtUiLoaded) Raylib.UnloadTexture(dirtUiTex);
        if (shaderOk) Raylib.UnloadShader(worldShader);
        Raylib.CloseWindow();
        CloseAmbientAudio();
    }
}
