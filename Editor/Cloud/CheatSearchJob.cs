using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P3 (§3.3) — THE <c>cheat-search</c> JOB: "find every cheat the game has", run by the kit when
    /// the website asks. Two halves:
    /// <list type="number">
    /// <item>the STATIC search (sources 2 and 4 — <see cref="CheatStaticSearch"/>): runs no game code, needs no tick and
    /// no Play Mode;</item>
    /// <item>the REGISTRY read (source 1 — <see cref="CheatRegistry"/>): a ticked read. It is asked through the kit job's
    /// gate (<see cref="KitJobRun.Gate"/>, the cloud flag on) as the fixed command <see cref="CheatRegistry.Lever"/>, once
    /// per read; un-ticked, nothing is read, the gate's own sentence is the answer, and the command becomes a row in the
    /// Nova Capture window. Ticked, it reads in Play Mode, in the scene the game reached BY ITSELF after boot — no scene
    /// is loaded for it (a cheat registered only in a later scene is found once a recipe reaches that scene, P5) — until
    /// two reads agree (<see cref="CheatRegistry.StableRead"/>).</item>
    /// </list>
    /// The kit reports FACTS (<see cref="ToFactsJson"/>); the website labels, merges and never trusts a count it did not
    /// read. Mirror of <c>CheatSearchFacts</c> in <c>apps/api/src/game-capture/cheat-jobs.ts</c>.
    /// </summary>
    public sealed class CheatSearchRun
    {
        /// <summary>The most findings one answer carries (the result door takes 1 MB; one finding is a few hundred bytes).</summary>
        public const int MaxFindings = 1500;

        public CheatStaticSearch? Static { get; private set; }
        public CheatRegistry.StableRead Stable { get; } = new();
        /// <summary>Whether the registry read was ASKED of the gate at all (false: there was nothing to read, or the job
        /// did not reach Play Mode).</summary>
        public bool RegistryAsked { get; private set; }
        /// <summary>The gate's sentence when it refused the read, else null.</summary>
        public string? RegistryRefusal { get; private set; }
        /// <summary>Why the registry was not read although the gate would have let it (none found, not in Play Mode).</summary>
        public string? RegistryNotRead { get; private set; }
        public List<string> GateLog { get; } = new();

        /// <summary>
        /// Would the gate let the registry be read NOW? Asked before Play Mode is entered, so an un-ticked search never
        /// presses Play in the studio's editor. The gate's own function with a kit bridge that runs NOTHING — the answer
        /// is only whether the command would pass. Null = it would; otherwise the gate's sentence.
        /// </summary>
        public static string? RegistryRefusalNow(string projectRoot)
        {
            var log = new List<string>();
            var gate = KitJobRun.Gate(new KitFunctionBridge(_ => true), projectRoot, log.Add);
            return gate.Run(CheatRegistry.Lever) ? null : (log.LastOrDefault() ?? Levers.NotApprovedLog(CheatRegistry.Lever));
        }

        /// <summary>The Edit-Mode half only: the static search, and why the registry was not read.</summary>
        public static CheatSearchRun StaticOnly(IReadOnlyList<Assembly> assemblies, string? refusal, string? notRead)
        {
            var run = new CheatSearchRun { Static = CheatStaticSearch.Run(assemblies) };
            run.RegistryRefusal = refusal;
            run.RegistryNotRead = notRead;
            run.RegistryAsked = refusal != null;
            return run;
        }

        /// <summary>
        /// THE WHOLE SEARCH, as the agent pumps it in Play Mode: the static search, then the registry read through the
        /// gate, one read per <see cref="CheatRegistry.StableRead.IntervalSec"/>, until stable or the cap. Every read is
        /// its own gate call, so a tick taken away mid-search stops the next read. Pure over <paramref name="now"/>: the
        /// tests pump it with a fake clock, the agent with the editor's.
        /// </summary>
        public static IEnumerable Run(CheatSearchRun run, string projectRoot, IReadOnlyList<Assembly> assemblies, Func<double> now,
            Func<RegistryRef, bool>? onlyForTests = null)
        {
            run.Static = CheatStaticSearch.Run(assemblies);
            var registries = run.Static.Registries.Where(r => onlyForTests == null || onlyForTests(r)).ToList();
            if (registries.Count == 0)
            {
                run.RegistryNotRead = "no console registry was found in the code, so there was nothing to read";
                yield break;
            }
            var gate = KitJobRun.Gate(new KitFunctionBridge(_ =>
            {
                run.Stable.Offer(CheatRegistry.ReadOnce(registries));
                return true;
            }), projectRoot, run.GateLog.Add);
            run.RegistryAsked = true;
            while (!run.Stable.Done)
            {
                if (!gate.Run(CheatRegistry.Lever))
                {
                    run.RegistryRefusal = run.GateLog.LastOrDefault() ?? Levers.NotApprovedLog(CheatRegistry.Lever);
                    yield break;
                }
                if (run.Stable.Done) break;
                var until = now() + CheatRegistry.StableRead.IntervalSec;
                while (now() < until) yield return null;
            }
        }

        /// <summary>Every finding: the registry's (the running game's own names) first, then the static ones.</summary>
        public List<CheatFinding> Findings()
        {
            var all = new List<CheatFinding>();
            if (Stable.Last != null) all.AddRange(CheatRegistry.Findings(Stable.Last));
            if (Static != null) all.AddRange(Static.Scan.Findings);
            return CheatFinding.Distinct(all);
        }

        /// <summary>The facts the job posts. Caps are the kit's; the server caps again and never trusts these.</summary>
        public string ToFactsJson(string kitVersion)
        {
            var findings = Findings();
            var kept = findings.Take(MaxFindings).ToList();
            var consoles = new JArray();
            var byWhere = Stable.Last?.ToDictionary(r => r.Registry.Where, r => r) ?? new Dictionary<string, CheatRegistry.ConsoleRead>();
            foreach (var r in Static?.Registries ?? new List<RegistryRef>())
            {
                byWhere.TryGetValue(r.Where, out var read);
                var o = new JObject
                {
                    ["console"] = r.Console,
                    ["where"] = r.Where,
                    ["executor"] = r.Executor == null ? JValue.CreateNull() : r.Executor,
                    ["names"] = read == null ? JValue.CreateNull() : new JArray(read.Names),
                    ["error"] = read?.Error == null ? JValue.CreateNull() : read.Error,
                    ["cut"] = read?.Cut ?? 0,
                };
                consoles.Add(o);
            }
            var facts = new JObject
            {
                ["kitVersion"] = kitVersion,
                ["registry"] = new JObject
                {
                    ["asked"] = RegistryAsked,
                    ["read"] = Stable.Last != null,
                    ["refusal"] = RegistryRefusal == null ? JValue.CreateNull() : RegistryRefusal,
                    ["notRead"] = RegistryNotRead == null ? JValue.CreateNull() : RegistryNotRead,
                    ["stable"] = Stable.Stable,
                    ["counts"] = new JArray(Stable.Counts),
                    ["consoles"] = consoles,
                },
                ["findings"] = new JArray(kept.Select(f => f.ToJson())),
                ["findingsCut"] = findings.Count - kept.Count,
                ["typesRead"] = Static?.Scan.TypesRead ?? 0,
                ["cutByBudget"] = Static?.Scan.CutByBudget ?? false,
                ["notes"] = new JArray(Static?.Notes ?? new List<string>()),
                // kit 0.13.2: where the player's data lives (types only — nothing was read), best first
                ["dataRoots"] = new JArray((Static?.DataRoots.Roots ?? new List<DataRoots.Root>()).Select(r => r.ToJson())),
            };
            return facts.ToString(Formatting.None);
        }
    }
}
