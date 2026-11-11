using Raylib_cs;
using System.Numerics;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    // Подземный фон
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
        if (!ambientEnabled || paused || inventoryOpen || consoleOpen || coopPanel || !Raylib.IsWindowFocused())
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
    }     // Звуки движения и действий
    static readonly Dictionary<string, List<Sound>> effectSounds = new();
    static readonly Dictionary<string, int> previousEffect = new();
    static bool effectsEnabled = true, footPositionReady, feetWereGrounded;
    static Vector3 footPosition;
    static float footDistance;

    static string AudioMaterial(byte block) => block switch
    {
        Grass => "hay_block", Dirt => "gravel", Sand => "sandstone",
        Wood => "oak_log", Leaves => "birch_leaves", Planks => "spruce_planks",
        Bricks => "stone_bricks", _ => "cobblestone"
    };

    static void InitGameplayAudio()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "Effects");
        if (!audioReady || !Directory.Exists(folder)) return;
        foreach (string path in Directory.GetFiles(folder, "*.ogg"))
        {
            string key = Path.GetFileNameWithoutExtension(path).TrimEnd('0','1','2','3');
            Sound sound = Raylib.LoadSound(path);
            if (sound.FrameCount == 0) { Raylib.UnloadSound(sound); continue; }
            if (!effectSounds.TryGetValue(key, out var sounds)) effectSounds[key] = sounds = new();
            sounds.Add(sound);
        }
    }

    static void PlayEffect(string key, float volume, Vector3? source = null)
    {
        if (!audioReady || !effectsEnabled || paused || inventoryOpen || consoleOpen || coopPanel || !Raylib.IsWindowFocused()) return;
        if (!effectSounds.TryGetValue(key, out var sounds) || sounds.Count == 0) return;
        float pan = 0.5f;
        if (source.HasValue)
        {
            Vector3 delta = source.Value - pos;
            float distance = delta.Length();
            volume *= Math.Clamp(1 - distance / 18, 0, 1);
            Vector3 right = new(-MathF.Sin(yaw), 0, MathF.Cos(yaw));
            pan = Math.Clamp(0.5f - Vector3.Dot(delta, right) / MathF.Max(distance, 1) * 0.4f, 0.1f, 0.9f);
        }
        if (volume < 0.005f) return;
        int index = ambientRandom.Next(sounds.Count);
        if (previousEffect.TryGetValue(key, out int last) && index == last && sounds.Count > 1)
            index = (index + 1) % sounds.Count;
        previousEffect[key] = index;
        Sound sound = sounds[index];
        Raylib.SetSoundVolume(sound, volume);
        Raylib.SetSoundPitch(sound, 0.95f + ambientRandom.NextSingle() * 0.1f);
        Raylib.SetSoundPan(sound, pan);
        Raylib.PlaySound(sound);
    }

    static void PlayBlockEditSound(int x, int y, int z, byte before, byte after)
    {
        if (before == after) return;
        string material = AudioMaterial(after == Air ? before : after);
        string key = material + (after == Air ? "_break" : "_place");
        if (!effectSounds.ContainsKey(key)) key = material + "_step";
        PlayEffect(key, after == Air ? 0.45f : 0.35f, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
    }

    static void StopGameplaySounds()
    {
        foreach (var group in effectSounds.Values)
            foreach (Sound sound in group) Raylib.StopSound(sound);
    }

    static void ResetGameplayAudio()
    {
        StopGameplaySounds();
        footPositionReady = feetWereGrounded = false;
        footDistance = 0;
    }

    static void UpdateGameplayAudio()
    {
        if (!audioReady) return;
        if (Raylib.IsKeyPressed(KeyboardKey.J))
        {
            effectsEnabled = !effectsEnabled;
            ShowStatus(effectsEnabled ? "Sound effects ON" : "Sound effects OFF");
        }
        if (!effectsEnabled || paused || inventoryOpen || consoleOpen || coopPanel || !Raylib.IsWindowFocused())
        {
            ResetGameplayAudio();
            return;
        }
        if (!footPositionReady) { footPosition = pos; footPositionReady = true; }
        float distance = Vector2.Distance(new(pos.X, pos.Z), new(footPosition.X, footPosition.Z));
        footPosition = pos;
        byte floor = Get((int)MathF.Floor(pos.X), (int)MathF.Floor(pos.Y - 0.08f), (int)MathF.Floor(pos.Z));
        bool grounded = !flying && floor != Air && (coopMatched && !coopHost || onGround);
        if (grounded && !feetWereGrounded)
            PlayEffect(AudioMaterial(floor) + "_step", crouching ? 0.12f : 0.32f);
        if (grounded && distance < 1)
        {
            footDistance += distance;
            if (footDistance >= (crouching ? 1.3f : 1.8f))
            {
                footDistance = 0;
                PlayEffect(AudioMaterial(floor) + "_step", crouching ? 0.08f : 0.24f);
            }
        }
        else footDistance = 0;
        feetWereGrounded = grounded;
        if (Raylib.IsMouseButtonPressed(MouseButton.Left)) PlayEffect("sword_swoosh_", 0.12f);
    }

    static void CloseGameplayAudio()
    {
        foreach (var group in effectSounds.Values)
            foreach (Sound sound in group) Raylib.UnloadSound(sound);
        effectSounds.Clear();
        previousEffect.Clear();
    }
}
