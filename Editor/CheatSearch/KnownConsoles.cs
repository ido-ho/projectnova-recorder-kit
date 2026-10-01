using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P3 (§3.3 sources 1–2, §5) — THE KNOWN-CONSOLE TABLE: the debug consoles games ship, found by
    /// their TYPE names, never by a game's name. Each row says where the console keeps what it registered (source 1:
    /// the running game's registry), which attribute declares a command (source 2: the tag scan), and which static
    /// method runs a command by name (so a registered name can be proposed as one fixed command).
    ///
    /// A row whose type is not in the game is skipped and SAID (the search's notes), never a throw.
    /// <see cref="ConsoleRow.CheckedAgainstSource"/> says which rows were checked against the console's own source:
    /// IngameDebugConsole is open source (yasirkula/UnityIngameDebugConsole, <c>DebugLogConsole.cs</c> and
    /// <c>ConsoleMethodAttribute.cs</c>, read 2026-09-26). Quantum Console, SRDebugger and Lunar Console are paid or
    /// closed here: their member names are from their public documentation only and UNVERIFIED against the real asset —
    /// a row that names a member the asset does not have reads nothing and says so.
    /// </summary>
    public static class KnownConsoles
    {
        public sealed class ConsoleRow
        {
            public string Id { get; set; } = "";
            /// <summary>The static class that holds the registry — full type name.</summary>
            public string? RegistryType { get; set; }
            /// <summary>The static FIELD holding the registered commands (a list or a dictionary keyed by name).</summary>
            public string? RegistryField { get; set; }
            /// <summary>For a list of objects: the member of each that holds its name.</summary>
            public string? ItemName { get; set; }
            /// <summary><c>Type.Method</c> of a static method taking one string that runs a command by name.</summary>
            public string? Executor { get; set; }
            /// <summary>The attribute that declares a command on a method — full type name.</summary>
            public string? Attribute { get; set; }
            /// <summary>Which constructor argument of <see cref="Attribute"/> is the command's name.</summary>
            public int AttributeNameArg { get; set; }
            /// <summary>True when an attribute with no name argument names the command after its method.</summary>
            public bool NameDefaultsToMethod { get; set; }
            /// <summary>A class whose every declared public method and property IS a command (SRDebugger's options).</summary>
            public string? OptionsType { get; set; }
            public bool CheckedAgainstSource { get; set; }
        }

        public static readonly IReadOnlyList<ConsoleRow> Rows = new[]
        {
            new ConsoleRow
            {
                Id = "ingame-debug-console",
                RegistryType = "IngameDebugConsole.DebugLogConsole",
                RegistryField = "methods",
                ItemName = "command",
                Executor = "IngameDebugConsole.DebugLogConsole.ExecuteCommand",
                Attribute = "IngameDebugConsole.ConsoleMethodAttribute",
                AttributeNameArg = 0,
                CheckedAgainstSource = true,
            },
            new ConsoleRow
            {
                Id = "quantum-console",
                RegistryType = "QFSW.QC.QuantumConsoleProcessor",
                RegistryField = "_commandTable",
                ItemName = "CommandName",
                Executor = "QFSW.QC.QuantumConsoleProcessor.InvokeCommand",
                Attribute = "QFSW.QC.CommandAttribute",
                AttributeNameArg = 0,
                NameDefaultsToMethod = true,
            },
            new ConsoleRow
            {
                Id = "srdebugger",
                OptionsType = "SROptions",
            },
            new ConsoleRow
            {
                Id = "lunar-console",
                // its actions are registered by `LunarConsole.RegisterAction("name", …)` — the tag scan's call-site read
                // (CheatTagScan) finds those by the method's name, so no row field is needed for them
                RegistryType = null,
            },
        };

        /// <summary>The known console an attribute type belongs to, or null.</summary>
        public static ConsoleRow? ByAttribute(string? attributeFullName) =>
            attributeFullName == null ? null : Rows.FirstOrDefault(r => r.Attribute == attributeFullName);

        /// <summary>The known console a type belongs to (its registry, its executor or its options class), or null.</summary>
        public static ConsoleRow? ByType(Type? type)
        {
            if (type?.FullName == null) return null;
            foreach (var r in Rows)
            {
                if (r.RegistryType == type.FullName || r.OptionsType == type.FullName) return r;
                if (r.Executor != null && r.Executor.Substring(0, r.Executor.LastIndexOf('.')) == type.FullName) return r;
            }
            return null;
        }
    }

    /// <summary>
    /// v3 P3 (§3.3 source 1, §5) — WHERE ONE RUNNING CONSOLE KEEPS ITS NAMES: a static field of a list or dictionary,
    /// and — when the console has one — the static method that runs a name. Built from a known-console row
    /// (<see cref="FromKnown"/>) or from the custom-console rule (<see cref="CustomConsoleRule.Describe"/>); read by
    /// <see cref="CheatRegistry"/>.
    /// </summary>
    public sealed class RegistryRef
    {
        public string Console { get; set; } = "";
        public Type Type { get; set; } = typeof(object);
        public FieldInfo Field { get; set; } = null!;
        /// <summary>For a list of objects: the field or property of each that holds its name; null for strings or keys.</summary>
        public string? ItemName { get; set; }
        /// <summary><c>Type.Method</c> that runs one name, or null.</summary>
        public string? Executor { get; set; }

        public string Where => $"{Type.FullName}.{Field.Name}";

        /// <summary>A known console's registry, when its type is in <paramref name="findType"/>'s reach and has the field.</summary>
        public static RegistryRef? FromKnown(KnownConsoles.ConsoleRow row, Func<string, Type?> findType, out string? why)
        {
            why = null;
            if (row.RegistryType == null || row.RegistryField == null) return null;
            var type = findType(row.RegistryType);
            if (type == null) return null;
            var field = type.GetField(row.RegistryField, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                why = $"{row.Id}: {row.RegistryType} is in the game but has no static field '{row.RegistryField}' " +
                      "(this kit's table may not match the version installed) — its registry was not read";
                return null;
            }
            return new RegistryRef
            {
                Console = row.Id, Type = type, Field = field, ItemName = row.ItemName,
                Executor = row.Executor != null && ExecutorExists(row.Executor, findType) ? row.Executor : null,
            };
        }

        private static bool ExecutorExists(string executor, Func<string, Type?> findType)
        {
            var dot = executor.LastIndexOf('.');
            var t = findType(executor.Substring(0, dot));
            return t != null && CustomConsoleRule.IsNameExecutor(t, executor.Substring(dot + 1));
        }
    }

    /// <summary>
    /// v3 P3 (§5) — THE CUSTOM-CONSOLE RULE, for a game that wrote its own console: a class with a static
    /// <c>Register*(string, …)</c> method (found by the tag scan's call-site read, <see cref="CheatTagScan"/>) AND a
    /// static field that is a list or a dictionary of names. Its names are read from that field by reflection; a static
    /// method taking one string named Invoke/Execute/Run/Fire/Trigger/Perform/Call… is its executor. No game's name is
    /// in here: the class is found by its SHAPE.
    /// </summary>
    public static class CustomConsoleRule
    {
        private static readonly string[] ExecutorVerbs = { "invoke", "execute", "run", "fire", "trigger", "perform", "call" };
        private static readonly string[] NameMembers = { "name", "command", "key", "id", "label", "title", "displayname", "commandname" };

        /// <summary>Does the name look like a registration method (<c>Register</c>, <c>RegisterAction</c>,
        /// <c>AddCommand</c>, <c>AddAction</c>)? The server's source-3 regex names the same verbs.</summary>
        public static bool IsRegistrationName(string method) =>
            method.StartsWith("Register", StringComparison.Ordinal)
            || method == "AddCommand" || method == "AddAction" || method == "AddCheat";

        /// <summary>A static method of <paramref name="type"/> called <paramref name="method"/> with ONE string parameter.</summary>
        public static bool IsNameExecutor(Type type, string method) =>
            type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(m => m.Name == method && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(string));

        /// <summary>This type's registry, when it has the custom-console shape; null otherwise (never throws).</summary>
        public static RegistryRef? Describe(Type type)
        {
            try
            {
                if (type.IsGenericTypeDefinition) return null;
                var statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                var registers = type.GetMethods(statics).Any(m => IsRegistrationName(m.Name)
                    && m.GetParameters() is { Length: >= 1 } ps && ps[0].ParameterType == typeof(string));
                if (!registers) return null;
                foreach (var f in type.GetFields(statics).OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    if (f.IsLiteral) continue;
                    var item = NameMemberFor(f.FieldType, out var shaped);
                    if (!shaped) continue;
                    var exec = type.GetMethods(statics)
                        .Where(m => m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(string)
                                    && !IsRegistrationName(m.Name)
                                    && ExecutorVerbs.Any(v => CheatFinding.WordsOf(m.Name).FirstOrDefault() == v))
                        .OrderBy(m => m.Name.Length).ThenBy(m => m.Name, StringComparer.Ordinal)
                        .FirstOrDefault();
                    return new RegistryRef
                    {
                        Console = "custom", Type = type, Field = f, ItemName = item,
                        Executor = exec == null ? null : $"{type.FullName}.{exec.Name}",
                    };
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>For a collection type: whether it can hold names (<paramref name="shaped"/>), and for a list of
        /// objects which member of each is the name. A dictionary keyed by string: its keys (null member). A list of
        /// strings: itself (null member).</summary>
        internal static string? NameMemberFor(Type collection, out bool shaped)
        {
            shaped = false;
            if (collection == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(collection)) return null;
            var dict = Interfaces(collection).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            if (dict != null)
            {
                shaped = dict.GetGenericArguments()[0] == typeof(string);
                return null;
            }
            var seq = Interfaces(collection).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            var item = seq?.GetGenericArguments()[0] ?? (collection.IsArray ? collection.GetElementType() : null);
            if (item == null) return null;
            if (item == typeof(string)) { shaped = true; return null; }
            var member = NameMemberOf(item);
            shaped = member != null;
            return member;
        }

        private static IEnumerable<Type> Interfaces(Type t) =>
            t.IsInterface ? new[] { t }.Concat(t.GetInterfaces()) : t.GetInterfaces();

        /// <summary>The string member of <paramref name="item"/> that is its name: one of the usual names (fields
        /// first — a field read runs no code), else its only string field; null when it has none.</summary>
        internal static string? NameMemberOf(Type item)
        {
            var inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var fields = item.GetFields(inst).Where(f => f.FieldType == typeof(string) && !f.Name.Contains("<")).ToList();
            foreach (var want in NameMembers)
            {
                var f = fields.FirstOrDefault(x => x.Name.TrimStart('_', 'm').TrimStart('_').ToLowerInvariant() == want
                                                   || x.Name.ToLowerInvariant() == want);
                if (f != null) return f.Name;
            }
            var props = item.GetProperties(inst).Where(p => p.PropertyType == typeof(string) && p.CanRead
                                                            && p.GetIndexParameters().Length == 0).ToList();
            foreach (var want in NameMembers)
            {
                var p = props.FirstOrDefault(x => x.Name.ToLowerInvariant() == want);
                if (p != null) return p.Name;
            }
            return fields.Count == 1 ? fields[0].Name : null;
        }
    }
}
