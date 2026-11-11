using System.Numerics;

namespace MinecraftCS;

internal static unsafe partial class Program
{
    static void TestConsoleCommands(Action<bool, string> check)
    {
        check(ExecuteConsoleCommand("/HELP").Contains("/time set"), "Console help accepts a slash and ignores case");
        check(ExecuteConsoleCommand("/unknown").StartsWith("Unknown command"), "Console reports unknown commands");
        check(ExecuteConsoleCommand("/pos").StartsWith("XYZ:"), "Console shows player coordinates");
        check(ExecuteConsoleCommand("/seed") == $"World seed: {Seed}", "Console shows the current seed");
        AddConsoleLine("previous message");
        ExecuteConsoleCommand("/clear");
        check(consoleLines.Count == 0, "Console clear removes previous output");
        ExecuteConsoleCommand(" /time   set midnight ");
        check(timeOfDay == 0.75f, "Console sets midnight with extra whitespace");
        ExecuteConsoleCommand("/time set nonsense");
        check(timeOfDay == 0.75f, "Invalid time leaves the world unchanged");

        coopMatched = true;
        coopHost = false;
        ExecuteConsoleCommand("/time set noon");
        check(timeOfDay == 0.75f, "Co-op guests cannot change the shared time");
        check(ExecuteConsoleCommand("/save").Contains("Only the host"), "Co-op guests cannot save the shared world");
        coopMatched = false;

        survivalMode = true;
        flying = false;
        ExecuteConsoleCommand("/fly on");
        check(!flying, "Flight command respects survival mode");
        survivalMode = false;
        vel = Vector3.One;
        ExecuteConsoleCommand("/fly on");
        check(flying && vel == Vector3.Zero, "Creative flight command uses the normal flight transition");
        ExecuteConsoleCommand("/fly off");
        check(!flying, "Console can disable flight");
    }
}
