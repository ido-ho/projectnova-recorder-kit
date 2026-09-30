using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    // Levers — levers.json IO: the path, reading and writing the ticks, the non-production tick. The class summary is in Levers.cs.
    public static partial class Levers
    {
        public static string FilePath(string projectRoot) =>
            Path.Combine(RelayPaths.NovaDir(projectRoot), FileName);

        /// <summary>
        /// The commands ticked on THIS machine, in the order the file lists them (duplicates and
        /// blanks dropped). Missing file, unreadable file, not JSON, wrong shape → EMPTY. Never
        /// throws, and never guesses: an unreadable approval list approves nothing.
        ///
        /// ANYTHING IN <c>approved</c> THAT IS NOT A COMMAND makes the whole file unreadable
        /// (second audit, K31). A number, an object or a null where a command should be is a file
        /// this kit does not understand, and half-understanding an approval list is how a list gets
        /// read as shorter — or longer — than the person who ticked it believes. It leaves here as
        /// the exception the one <c>catch</c> below turns into "nothing is approved", which is the
        /// only exit that cannot publish a half-read list.
        /// </summary>
        public static IReadOnlyList<string> Approved(string projectRoot) => ReadApproved(projectRoot, out _);

        /// <summary>
        /// Learn-and-drive v3 P2 (§3.1, decided §9.1 = A) — THE PER-PROJECT TICK "this editor talks to a
        /// non-production server". Stored in the same <c>levers.json</c> as every other tick
        /// (<see cref="NonProductionField"/>), so it has the same one writer (<see cref="SetNonProduction"/>, called only by
        /// the Nova Capture window), the same fail-closed read (an unreadable file answers FALSE) and the same version.
        /// Until it is true, a RISKY cheat (<see cref="CheatRisk"/>) is refused at the gate even when it is ticked.
        /// Detected hints (<see cref="NonProductionHints"/>) are shown beside it and never read here.
        /// </summary>
        public static bool NonProductionTicked(string projectRoot)
        {
            ReadFile(projectRoot, out _, out var nonProduction);
            return nonProduction;
        }

        /// <summary>The levers.json key that holds <see cref="NonProductionTicked"/>. A boolean; absent = false; anything
        /// else makes the whole file unreadable (it approves nothing), as a non-command in <c>approved</c> does.</summary>
        public const string NonProductionField = "nonProductionServer";

        /// <summary>
        /// Tick or untick "this editor talks to a non-production server". The ONLY writer of <see cref="NonProductionField"/>,
        /// called from the Nova Capture window, and it keeps every approval in the file exactly as it was — as
        /// <see cref="SetApproved"/> keeps this field (a writer that dropped the other one would untick it silently).
        /// Returns null on success, or why it could not be written; a file this kit cannot read is not written over.
        /// </summary>
        public static string? SetNonProduction(string projectRoot, bool ticked)
        {
            try
            {
                var list = ReadFile(projectRoot, out var unreadable, out _, out var legacy);
                if (unreadable != null)
                    return $"{FilePath(projectRoot)} cannot be read ({unreadable}); fix or delete it first. Nothing was " +
                           "written: writing it now would replace every approval in it.";
                WriteFile(projectRoot, list, ticked, legacy);
                return null;
            }
            catch (Exception e)
            {
                return $"could not write {FilePath(projectRoot)}: {e.Message}";
            }
        }

        /// <summary>
        /// The Fix 4 audit, M3 (invariant 182) — the levers.json key that holds the SET-VALUE TEMPLATES ticked by this kit
        /// (<see cref="IsSetValueTemplate"/>). A template of that shape found in <c>approved</c> was ticked by a kit before
        /// 0.14.0, when a template approved nothing; it is kept in the file exactly as it was (written back, and taken out
        /// by an untick of its text) but it approves nothing now either — only a tick made on this kit, which writes it
        /// here, does. A kit before 0.14.0 does not read this key (it approves no template anyway).
        /// </summary>
        public const string SetValueApprovedField = "setValueApproved";

        private static void WriteFile(string projectRoot, List<string> approved, bool nonProduction, List<string>? legacy = null)
        {
            var plain = approved.Where(c => !IsSetValueTemplate(c)).ToList();
            foreach (var old in legacy ?? new List<string>())
                if (!plain.Contains(old)) plain.Add(old);
            var root = new JObject
            {
                ["$schemaVersion"] = SchemaVersion,
                ["approved"] = new JArray(plain),
            };
            var setValue = approved.Where(IsSetValueTemplate).ToList();
            if (setValue.Count > 0) root[SetValueApprovedField] = new JArray(setValue);
            // Written only when true: a file with no tick of it reads exactly as every levers.json before P2 did.
            if (nonProduction) root[NonProductionField] = true;
            AtomicFile.Write(FilePath(projectRoot), root.ToString(Formatting.Indented));
        }

        /// <summary><see cref="Approved"/>, also saying why an EXISTING levers.json could not be read — null when it was
        /// read or is not there. An <c>approved</c> that is absent or not a list is a file this kit does not understand
        /// too. <see cref="SetApproved"/> will not write over such a file (eighth audit, S2).</summary>
        private static List<string> ReadApproved(string projectRoot, out string? unreadable) =>
            ReadFile(projectRoot, out unreadable, out _);

        /// <summary>The approvals and the non-production tick from ONE read of levers.json — what the gate asks per command.</summary>
        internal static IReadOnlyList<string> ApprovedAndNonProduction(string projectRoot, out bool nonProduction) =>
            ReadFile(projectRoot, out _, out nonProduction);

        /// <summary>The whole of levers.json: the approvals, and <see cref="NonProductionField"/>. Unreadable → nothing
        /// approved AND not ticked, together — one catch, so no half of a half-read file is ever published.</summary>
        private static List<string> ReadFile(string projectRoot, out string? unreadable, out bool nonProduction) =>
            ReadFile(projectRoot, out unreadable, out nonProduction, out _);

        /// <summary><see cref="ReadFile(string, out string?, out bool)"/>, also handing back the set-value templates found in
        /// <c>approved</c> (ticked before 0.14.0 — kept in the file, approving nothing: <see cref="SetValueApprovedField"/>).</summary>
        private static List<string> ReadFile(string projectRoot, out string? unreadable, out bool nonProduction, out List<string> legacy)
        {
            unreadable = null;
            nonProduction = false;
            legacy = new List<string>();
            var list = new List<string>();
            try
            {
                var path = FilePath(projectRoot);
                if (!System.IO.File.Exists(path)) return list;
                var root = NovaJson.ParseObject(System.IO.File.ReadAllText(path));
                // Ninth audit, M3: a version this kit did not write is a file it does not understand. A newer kit's
                // levers.json may say things this one would drop on the next tick (a list of denials), so it approves
                // nothing and is not written over, as an unreadable file (ATickOverALeversFileOfAnotherVersionLeavesIt).
                var version = root["$schemaVersion"];
                if (version == null || version.Type != JTokenType.Integer || !NovaJson.TryNumber(version, out var number)
                    || number != SchemaVersion)
                    throw new FormatException($"{FileName}: $schemaVersion is " +
                                              (version == null ? "missing" : version.ToString(Formatting.None)) +
                                              $", not {SchemaVersion}, so it was written by a newer kit or by hand");
                if (root["approved"] is not JArray arr)
                    throw new FormatException($"{FileName}: 'approved' is not a list of commands");
                foreach (var t in arr)
                {
                    if (t.Type != JTokenType.String)
                        throw new FormatException(
                            $"{FileName}: 'approved' holds a {t.Type} where a command should be");
                    var s = t.Value<string>();
                    if (string.IsNullOrEmpty(s) || list.Contains(s!)) continue;
                    // M3: a set-value template in `approved` was ticked by an older kit — it approves nothing
                    if (IsSetValueTemplate(s)) { if (!legacy.Contains(s!)) legacy.Add(s!); continue; }
                    list.Add(s!);
                }
                if (root[SetValueApprovedField] is { } sv)
                {
                    if (sv is not JArray svArr)
                        throw new FormatException($"{FileName}: '{SetValueApprovedField}' is not a list of commands");
                    foreach (var t in svArr)
                    {
                        // only the one template shape is honoured here; anything else makes the file unreadable
                        if (t.Type != JTokenType.String || !IsSetValueTemplate(t.Value<string>()))
                            throw new FormatException($"{FileName}: '{SetValueApprovedField}' holds something that is not a `set Type.Member {{v}}` template");
                        var s = t.Value<string>()!;
                        if (!list.Contains(s)) list.Add(s);
                    }
                }
                var np = root[NonProductionField];
                if (np != null && np.Type != JTokenType.Boolean)
                    throw new FormatException($"{FileName}: '{NonProductionField}' is a {np.Type}, not true or false");
                var ticked = np != null && np.Value<bool>();
                nonProduction = ticked;
            }
            catch (Exception e)
            {
                // Fail CLOSED: an approval file that cannot be read approves nothing. Returning
                // what had been collected so far would be a half-read list read as a whole one.
                unreadable = e.Message;
                legacy = new List<string>();
                nonProduction = false;
                return new List<string>();
            }
            return list;
        }

        /// <summary>
        /// Tick or untick one command. The ONLY writer of levers.json, called from the Nova Capture
        /// window. Returns null on success, or the reason it could not be written.
        ///
        /// AN EXISTING levers.json THIS KIT CANNOT READ IS NOT WRITTEN OVER (eighth audit, S2). It approves
        /// nothing (<see cref="Approved"/>), and a tick used to rewrite it from that empty list, so every other
        /// approval in it was gone without a word. The reason comes back instead, which the window shows as it shows
        /// a failed write (<c>ATickOverALeversFileTheKitCannotReadLeavesItByteForByteAndSaysWhy</c>).
        /// </summary>
        public static string? SetApproved(string projectRoot, string command, bool approved)
        {
            if (string.IsNullOrWhiteSpace(command)) return "a blank command cannot be approved";
            // Tickable one way only (second audit, M3): an entry whose verb is a {placeholder}
            // cannot be ticked, but one already in the file can always be UNticked — a person must
            // be able to take back anything that is in there, however it got there.
            if (approved && NotTickableHere(command) is { } why) return why;
            try
            {
                var list = ReadFile(projectRoot, out var unreadable, out var nonProduction, out var legacy);
                if (unreadable != null)
                    return $"{FilePath(projectRoot)} cannot be read ({unreadable}); fix or delete it first. Nothing was " +
                           "written: writing it now would replace every approval in it with this one.";
                if (approved)
                {
                    if (!list.Contains(command)) list.Add(command);
                }
                else
                {
                    list.RemoveAll(c => string.Equals(c, command, StringComparison.Ordinal));
                    // an unticked command leaves the old-kit entries of its text too
                    legacy.RemoveAll(c => string.Equals(c, command, StringComparison.Ordinal));
                }
                // v3 P2: the non-production tick rides the same file and is kept as it was.
                WriteFile(projectRoot, list, nonProduction, legacy);
                return null;
            }
            catch (Exception e)
            {
                return $"could not write {FilePath(projectRoot)}: {e.Message}";
            }
        }
    }
}
