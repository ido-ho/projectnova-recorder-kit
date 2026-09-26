// Learn-and-drive v3 P3 (§5) — THE GENERALITY FIXTURES for the deep cheat search: two consoles unlike any one game's.
//  1. an IngameDebugConsole-SHAPED console — the free known console of the plan's §5 table, shaped after its real
//     source (yasirkula/UnityIngameDebugConsole: `DebugLogConsole.methods`, `ConsoleMethodInfo.command`,
//     `ConsoleMethodAttribute(string command, string description, params string[])`, `ExecuteCommand(string)`), declared
//     HERE so no third-party package is added to KitTestHost;
//  2. a CUSTOM console: a static class with `RegisterAction(string, Action)`, a static list of entries and an
//     `Invoke(string)` — the custom-console rule's shape — plus a dictionary-keyed one.
// And the source-4 cases: debug-named classes (GM…, Debug…) and two that only LOOK like one (Device…, Latest…).
// Tripwire: nothing the STATIC search does may run code of these classes (an attribute constructor, a static one).
using System;
using System.Collections.Generic;

namespace ProjectNova.RecorderKit.Tests.CheatFixtures
{
    public static class Tripwire
    {
        public static bool AttributeCtorRan;
        public static bool StaticCtorRan;
        public static readonly List<string> Fired = new();
        public static void Reset() { AttributeCtorRan = false; StaticCtorRan = false; Fired.Clear(); }
    }
}

namespace IngameDebugConsole
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public class ConsoleMethodAttribute : Attribute
    {
        public string Command { get; }
        public ConsoleMethodAttribute(string command, string description, params string[] parameterNames)
        {
            ProjectNova.RecorderKit.Tests.CheatFixtures.Tripwire.AttributeCtorRan = true;
            Command = command;
        }
    }

    public class ConsoleMethodInfo
    {
        public readonly string command;
        public readonly Action action;
        public ConsoleMethodInfo(string command, Action action) { this.command = command; this.action = action; }
    }

    public static class DebugLogConsole
    {
        private static readonly List<ConsoleMethodInfo> methods = new();

        public static void AddCommand(string command, string description, Action method)
        {
            methods.RemoveAll(m => m.command == command);
            methods.Add(new ConsoleMethodInfo(command, method));
        }

        public static void ExecuteCommand(string command)
        {
            foreach (var m in methods)
                if (m.command == command) m.action();
        }

        public static void ClearForTests() => methods.Clear();
    }
}

namespace NovaFixtures.KnownConsoleGame
{
    public static class FixtureConsoleCommands
    {
        [IngameDebugConsole.ConsoleMethod("fixture-cube", "spawns a cube")]
        public static void Cube() => ProjectNova.RecorderKit.Tests.CheatFixtures.Tripwire.Fired.Add("cube");

        [IngameDebugConsole.ConsoleMethod("fixture-give", "gives coins", "amount")]
        public static void Give(int amount) => ProjectNova.RecorderKit.Tests.CheatFixtures.Tripwire.Fired.Add("give " + amount);

        /// <summary>Registered at run time, like a game's boot code does — found by the call-site read and the registry.</summary>
        public static void Boot()
        {
            IngameDebugConsole.DebugLogConsole.AddCommand("fixture-runtime-win", "wins the fight",
                () => ProjectNova.RecorderKit.Tests.CheatFixtures.Tripwire.Fired.Add("win"));
        }
    }
}

namespace NovaFixtures.CustomConsole
{
    public static class FixtureDevActions
    {
        public sealed class Entry
        {
            public string Name;
            public Action Run;
            public Entry(string name, Action run) { Name = name; Run = run; }
        }

        private static readonly List<Entry> Entries = new();

        public static void RegisterAction(string name, Action run)
        {
            Entries.RemoveAll(e => e.Name == name);
            Entries.Add(new Entry(name, run));
        }

        public static void Invoke(string name)
        {
            foreach (var e in Entries.ToArray())
                if (e.Name == name) e.Run();
        }

        public static void ClearForTests() => Entries.Clear();
    }

    public static class FixtureDevBoot
    {
        public static int Level;

        public static void Boot()
        {
            FixtureDevActions.RegisterAction("Skip 5 Levels", () => Level += 5);
            FixtureDevActions.RegisterAction("Reset progress", () => Level = 1);
            for (var i = 1; i <= 2; i++)
            {
                var n = i;
                FixtureDevActions.RegisterAction($"Jump to level {n}", () => Level = n);
            }
        }

        /// <summary>Registered LATER than boot — what "read until stable" exists for.</summary>
        public static void LateBoot() => FixtureDevActions.RegisterAction("Late cheat", () => Level = 99);
    }

    public static class FixtureCommandTable
    {
        private static readonly Dictionary<string, Action> table = new();
        public static void Register(string name, Action run) => table[name] = run;
        public static void Execute(string name) { if (table.TryGetValue(name, out var a)) a(); }
        public static void ClearForTests() => table.Clear();
    }

    /// <summary>A custom console NO test ever boots: its static constructor running would mean the static search ran
    /// code of the game.</summary>
    public static class FixtureUntouchedRegistry
    {
        private static readonly List<string> names = new() { "never" };

        static FixtureUntouchedRegistry()
        {
            ProjectNova.RecorderKit.Tests.CheatFixtures.Tripwire.StaticCtorRan = true;
        }

        public static void RegisterCheat(string name) => names.Add(name);
        public static void Run(string name) { }
    }

    public static class FixtureUntouchedBoot
    {
        public static void Boot() => FixtureUntouchedRegistry.RegisterCheat("Untouched cheat");
    }

    public static class FixtureTableBoot
    {
        public static void Boot() => FixtureCommandTable.Register("Unlock all", () => { });
    }
}

namespace NovaFixtures.DebugMethodsGame
{
    public class GMFixtureTools
    {
        public static GMFixtureTools Instance = new();
        public int Coins;
        public void MaxEverything() => Coins = 999;
        public void AddCoins(int amount) => Coins += amount;
        public void SpawnAt(UnityEngine.Vector3 at) { }
        private void Update() { }
        public int CoinsProperty => Coins;
    }

    public static class DebugFixturePanel
    {
        public static void UnlockAllLevels() { }
    }

    public class CheatFixtureNoInstance
    {
        public void GiveGems() { }
    }

    public static class DeviceFixtureInfo
    {
        public static void WipeAll() { }
    }

    public static class LatestFixtureNews
    {
        public static void Refresh() { }
    }
}
