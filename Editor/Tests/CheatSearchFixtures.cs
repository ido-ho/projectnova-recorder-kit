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

// ---- a THIN FORWARDER (the shape a real studio game shipped, 2026-09-27): a static class in the GLOBAL namespace whose
// RegisterAction only hands the name on to a namespaced registry that holds the names and runs one by name. The call
// sites register on the forwarder, which has no executor and no list — the executor is one class further in.

/// <summary>The forwarder the game's code calls. No list, no executor: every body only forwards.</summary>
public static class NovaFixtureForwardingConsole
{
    public static void RegisterAction(string name, Action action) =>
        NovaFixtures.ForwardedConsole.FixtureForwardedActions.Register(name, action);

    public static void UnregisterAction(string name) =>
        NovaFixtures.ForwardedConsole.FixtureForwardedActions.Unregister(name);
}

/// <summary>A forwarder that forwards to a forwarder — followed to the registry at the end of the chain.</summary>
public static class NovaFixtureChainedConsole
{
    public static void RegisterAction(string name, Action action) => NovaFixtureForwardingConsole.RegisterAction(name, action);
}

/// <summary>A forwarder whose registry keeps a list but has NO by-name executor anywhere: stays listed.</summary>
public static class NovaFixtureDeadEndConsole
{
    public static void RegisterAction(string name, Action action) =>
        NovaFixtures.ForwardedConsole.FixtureDeadEndStore.Register(name, action);
}

/// <summary>A forwarder that hands the name to TWO registries: which one runs a name is not guessed — stays listed.</summary>
public static class NovaFixtureTwoWayConsole
{
    public static void RegisterAction(string name, Action action)
    {
        NovaFixtures.ForwardedConsole.FixtureForwardedActions.Register(name, action);
        NovaFixtures.CustomConsole.FixtureCommandTable.Register(name, action);
    }
}

namespace NovaFixtures.ForwardedConsole
{
    /// <summary>The registry the forwarder hands names to: a sorted list of names, a dictionary of actions, and
    /// <c>Invoke(string)</c> — the custom-console shape, one class further in than the call sites.</summary>
    public static class FixtureForwardedActions
    {
        private static readonly List<string> _names = new();
        private static readonly Dictionary<string, Action> _actions = new();

        public static int Count => _names.Count;
        public static string NameAt(int index) => _names[index];

        public static void Register(string name, Action action)
        {
            if (string.IsNullOrEmpty(name) || action == null) return;
            if (!_actions.ContainsKey(name))
            {
                var at = _names.BinarySearch(name, StringComparer.Ordinal);
                _names.Insert(at < 0 ? ~at : at, name);
            }
            _actions[name] = action;
        }

        public static void Unregister(string name)
        {
            if (string.IsNullOrEmpty(name) || !_actions.Remove(name)) return;
            _names.Remove(name);
        }

        public static void Clear()
        {
            _names.Clear();
            _actions.Clear();
        }

        public static void Invoke(string name)
        {
            if (_actions.TryGetValue(name, out var action)) action();
        }
    }

    /// <summary>A registry with a list of names and no way to run one by name.</summary>
    public static class FixtureDeadEndStore
    {
        private static readonly List<string> names = new();
        public static void Register(string name, Action action) => names.Add(name);
        public static void ClearForTests() => names.Clear();
    }

    public static class FixtureForwardedBoot
    {
        public static int Level;
        public static int Topaz;

        /// <summary>The game's own debug actions, registered through the global forwarder with literal names.</summary>
        public static void Boot()
        {
            NovaFixtureForwardingConsole.RegisterAction("Skip 1 Level", () => Level += 1);
            NovaFixtureForwardingConsole.RegisterAction("Reset user", () => Level = 1);
            NovaFixtureForwardingConsole.RegisterAction("Give me 10k topaz pls", () => Topaz += 10000);
            NovaFixtureForwardingConsole.RegisterAction("Max Affinity Tree: Warrior", () => Level = 50);
        }

        public static void ChainedBoot() => NovaFixtureChainedConsole.RegisterAction("Chained cheat", () => Level = 7);
    }

    /// <summary>Never booted: their registrations would reach the other fixtures' registries.</summary>
    public static class FixtureListedOnlyBoot
    {
        public static void DeadEnd() => NovaFixtureDeadEndConsole.RegisterAction("Dead end cheat", () => { });
        public static void TwoWay() => NovaFixtureTwoWayConsole.RegisterAction("Two way cheat", () => { });
    }
}
