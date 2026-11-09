using Raylib_cs;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    static readonly List<Sound> ambientSounds = new();
    static readonly Random ambientRandom = new();
    static bool audioReady, ambientEnabled = true;
    static float ambientDelay = 15, ambientProbe;
    static bool ambientSheltered;
    static int lastAmbient = -1;

    static void InitAmbientAudio()
    {
        Raylib.InitAudioDevice();
        audioReady = Raylib.IsAudioDeviceReady();
        if (!audioReady) return;
        InitGameplayAudio();
        for (int i = 1; i <= 3; i++)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "Ambient", $"ice_echo_{i:00}.ogg");
            if (!File.Exists(path)) continue;
            Sound sound = Raylib.LoadSound(path);
            if (sound.FrameCount > 0) ambientSounds.Add(sound);
            else Raylib.UnloadSound(sound);
        }
    }

    static void StopAmbientSounds()
    {
        foreach (Sound sound in ambientSounds) Raylib.StopSound(sound);
    }

    static void UpdateAmbientAudio(float dt)
    {
        if (!audioReady) return;
        if (Raylib.IsKeyPressed(KeyboardKey.N))
        {
            ambientEnabled = !ambientEnabled;
            ShowStatus(ambientEnabled ? "Ambient sound ON" : "Ambient sound OFF");
        }
        if (!ambientEnabled || paused || coopPanel || !Raylib.IsWindowFocused())
        {
            StopAmbientSounds();
            return;
        }
        ambientProbe -= dt;
        if (ambientProbe <= 0)
        {
            ambientProbe = 0.5f;
            int x = (int)MathF.Floor(pos.X), z = (int)MathF.Floor(pos.Z);
            int roof = 0;
            for (int y = (int)MathF.Ceiling(pos.Y + Height); y < H; y++)
            {
                byte block = Get(x, y, z);
                if (block != Air && block != Leaves && block != Wood) roof++;
            }
            ambientSheltered = roof >= 3;
        }
        if (!ambientSheltered) { StopAmbientSounds(); return; }
        ambientDelay = MathF.Max(0, ambientDelay - dt);
        if (ambientDelay > 0 || ambientSounds.Count == 0 || ambientSounds.Any(sound => Raylib.IsSoundPlaying(sound))) return;
        int index = ambientRandom.Next(ambientSounds.Count);
        if (index == lastAmbient && ambientSounds.Count > 1) index = (index + 1) % ambientSounds.Count;
        lastAmbient = index;
        Sound next = ambientSounds[index];
        Raylib.SetSoundVolume(next, 0.16f);
        Raylib.SetSoundPitch(next, 0.92f + ambientRandom.NextSingle() * 0.12f);
        Raylib.SetSoundPan(next, 0.35f + ambientRandom.NextSingle() * 0.3f);
        Raylib.PlaySound(next);
        ambientDelay = 25 + ambientRandom.NextSingle() * 30;
    }

    static void CloseAmbientAudio()
    {
        if (!audioReady) return;
        CloseGameplayAudio();
        foreach (Sound sound in ambientSounds) Raylib.UnloadSound(sound);
        ambientSounds.Clear();
        Raylib.CloseAudioDevice();
        audioReady = false;
    }
}
