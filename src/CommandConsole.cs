using System.Numerics;
using Raylib_cs;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    const int ConsoleLineLimit = 8;
    const string ConsoleHelp = "/help  /pos  /seed  /save  /clear\n" +
        "/time set <day|noon|night|midnight>\n" +
        "/fly <on|off>  (Creative only)";

    static readonly Queue<string> consoleLines = new();
    static bool consoleOpen;
    static string consoleInput = "";

    static void SetConsoleOpen(bool open)
    {
        if (consoleOpen == open) return;
        consoleOpen = open;
        consoleInput = "";
        if (open)
        {
            StopRemoteMovement();
            Raylib.EnableCursor();
        }
        else
        {
            Raylib.DisableCursor();
            frame = 0;
        }
    }

    static void UpdateConsole()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.F2))
        {
            SetConsoleOpen(false);
            return;
        }

        for (int c = Raylib.GetCharPressed(); c != 0; c = Raylib.GetCharPressed())
            if (c is >= 32 and <= 126 && consoleInput.Length < 128)
                consoleInput += (char)c;

        if (consoleInput.Length > 0 && (Raylib.IsKeyPressed(KeyboardKey.Backspace) ||
            Raylib.IsKeyPressedRepeat(KeyboardKey.Backspace)))
            consoleInput = consoleInput[..^1];

        if (!Raylib.IsKeyPressed(KeyboardKey.Enter) || string.IsNullOrWhiteSpace(consoleInput)) return;
        AddConsoleLine("> " + consoleInput);
        string result = ExecuteConsoleCommand(consoleInput);
        if (result.Length > 0)
            foreach (string line in result.Split('\n')) AddConsoleLine(line);
        consoleInput = "";
    }

    static void AddConsoleLine(string line)
    {
        if (consoleLines.Count == ConsoleLineLimit) consoleLines.Dequeue();
        consoleLines.Enqueue(line);
    }

    static string ExecuteConsoleCommand(string input)
    {
        var parts = input.Trim().TrimStart('/').ToLowerInvariant().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "Type /help for commands.";

        bool guest = coopMatched && !coopHost;
        switch (parts[0])
        {
            case "help":
                return parts.Length == 1 ? ConsoleHelp : "Usage: /help";
            case "pos":
                return parts.Length == 1 ? FormattableString.Invariant($"XYZ: {pos.X:F2}, {pos.Y:F2}, {pos.Z:F2}") : "Usage: /pos";
            case "seed":
                return parts.Length == 1 ? $"World seed: {Seed}" : "Usage: /seed";
            case "clear":
                if (parts.Length != 1) return "Usage: /clear";
                consoleLines.Clear();
                return "";
            case "save":
                if (parts.Length != 1) return "Usage: /save";
                if (guest) return "Only the host can save this world.";
                if (worldStorage == null) return "No world to save.";
                return TrySaveActiveWorld(worldStorage, out string error) ? "World saved." : error ?? "World could not be saved.";
            case "time":
                if (parts.Length != 3 || parts[1] != "set") return "Usage: /time set <day|noon|night|midnight>";
                float time = parts[2] switch
                {
                    "day" => 0.1f,
                    "noon" => 0.25f,
                    "night" => 0.65f,
                    "midnight" => 0.75f,
                    _ => -1f
                };
                if (time < 0) return "Usage: /time set <day|noon|night|midnight>";
                if (guest) return "Only the host can change the time.";
                timeOfDay = time;
                UpdateSun(0);
                return $"Time set to {parts[2]}.";
            case "fly":
                if (parts.Length != 2 || parts[1] is not ("on" or "off")) return "Usage: /fly <on|off>";
                if (guest) return "Flight is controlled by the host in co-op.";
                if (survivalMode) return "Flight is available in Creative mode.";
                SetFlight(parts[1] == "on");
                return flying ? "Flight enabled." : "Flight disabled.";
            default:
                return $"Unknown command: {parts[0]}. Type /help.";
        }
    }

    static void DrawConsole()
    {
        const int lineHeight = 22, fontSize = 18;
        int width = Raylib.GetScreenWidth();
        int height = Raylib.GetScreenHeight();
        int top = Math.Max(0, height - (ConsoleLineLimit + 2) * lineHeight - 16);
        Raylib.DrawRectangle(0, top, width, height - top, Col(12, 16, 22, 235));
        Raylib.BeginScissorMode(12, top, Math.Max(0, width - 24), height - top);
        Raylib.DrawText("Console  |  /help  |  Enter: run  |  Esc: close", 12, top + 8, fontSize, Col(176, 204, 229));
        int y = top + lineHeight + 8;
        foreach (string line in consoleLines)
        {
            Raylib.DrawText(line, 12, y, fontSize, Col(234, 238, 242));
            y += lineHeight;
        }

        string prompt = "> " + consoleInput + "_";
        int inputX = Math.Min(12, width - 12 - Raylib.MeasureText(prompt, fontSize));
        Raylib.DrawText(prompt, inputX, height - lineHeight - 4, fontSize, Col(255, 224, 150));
        Raylib.EndScissorMode();
    }
}
