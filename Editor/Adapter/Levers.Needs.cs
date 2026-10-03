using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    // Levers — what the files on disk ask for: the needed-lever lists from shots.json and adapter.json. The class summary is in Levers.cs.
    public static partial class Levers
    {
        /// <summary>
        /// The distinct levers the files currently on disk can pull, sorted ordinal
        /// — every shot's `setup` entry, every `cheat` / `cheatUntil` command, the adapter's
        /// `ready` `mute` / `resistOn` / `resistOff` and `get &lt;muteGet&gt;` (a `get` runs property getters, which is
        /// code — the fourteenth audit), plus the two
        /// writes that are not ICheatBridge commands at all: `timeScale` once if any shot changes
        /// it, and `hide-overlay &lt;TypeName&gt;` per adapter.json `overlayTypeNames` entry.
        /// Commands are
        /// listed AS AUTHORED: a `{param}` template stays a template, because that is what a person
        /// is being asked to approve.
        ///
        /// The hosted lint computes the same list from the same two files
        /// (<c>apps/renderer/src/game-ads/shots/shots-lint.ts</c>, <c>levers</c>) — this mirrors it.
        /// </summary>
        public static IReadOnlyList<string> Needed(string projectRoot) =>
            NeededFrom(ReadOrNull(RelayPaths.NovaShotsFile(projectRoot)),
                ReadOrNull(SyncNova.AdapterFile(projectRoot)));

        private static string? ReadOrNull(string path)
        {
            try { return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// THE SHOTS HALF, FROM ONE DOCUMENT — the shared definition the hosted lint mirrors
        /// (<c>apps/renderer/src/game-ads/shots/shots-lint.ts</c>), pinned case by case in
        /// <c>Editor/Tests/Fixtures/shots-grammar.cases.json</c> (`levers`).
        ///
        /// A RAW TRAVERSAL, deliberately: it does not care whether a shot LOADS, or what
        /// <c>$schemaVersion</c> says, because a shot the loader refuses still has to have its
        /// commands shown to the person deciding. Not JSON, not an object, or no <c>shots</c>
        /// array → nothing, which is the window's and the gate's reading of a file on disk; a DELIVERY
        /// of such a file is refused instead (<see cref="SyncNova.DeliveryRefusal"/>, tenth audit). For each shot object: every non-empty string in <c>setup</c>; the
        /// <c>command</c> of every <c>cheat</c> / <c>cheatUntil</c> step; and
        /// <see cref="TimeScaleLever"/> once for any <c>timeScale</c> step. Nothing else — a
        /// vision prompt, a condition and an element name are not levers. Sorted ordinal,
        /// distinct.
        /// </summary>
        public static IReadOnlyList<string> NeededFromShotsJson(string? shotsText)
        {
            JObject? root = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(shotsText)) root = NovaJson.ParseObject(shotsText!);
            }
            catch (Exception)
            {
                // A shots file that is not JSON needs no levers HERE — the window and the gate read the file on disk, and
                // the kit cannot load a shot out of it either (the loader says why). A DELIVERY is never read this way:
                // SyncNova's delivery check runs the loader on it, which refuses what it cannot parse (tenth audit, S1; since
                // the eleventh, the check IS the loader).
            }
            return root == null ? new List<string>() : NeededFromShots(root);
        }

        /// <summary>The same list, from a document already parsed — what <see cref="SyncNova"/>'s delivery check reads
        /// (tenth audit, S1), so it never meets a parse failure to answer "nothing" for.</summary>
        internal static List<string> NeededFromShots(JObject root)
        {
            var found = new List<string>();
            // A set beside the list (ninth audit): SyncNova counts this list for every delivery before it writes, and a
            // 512 KB file of short distinct commands made List.Contains quadratic. The answer is the same list.
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            void Add(JToken? t)
            {
                if (t == null || t.Type != JTokenType.String) return;
                var s = t.Value<string>();
                if (string.IsNullOrEmpty(s) || !distinct.Add(s!)) return;
                found.Add(s!);
            }

            if (root["shots"] is JArray shots)
            {
                foreach (var shotToken in shots)
                {
                    if (shotToken is not JObject shot) continue;
                    if (shot["setup"] is JArray setup)
                        foreach (var cmd in setup) Add(cmd);
                    if (shot["steps"] is not JArray steps) continue;
                    foreach (var stepToken in steps)
                    {
                        if (stepToken is not JObject step) continue;
                        var kind = step["kind"]?.Type == JTokenType.String
                            ? step["kind"]!.Value<string>()
                            : null;
                        if (kind == "cheat" || kind == "cheatUntil") Add(step["command"]);
                        // A `timeScale` step writes `Time.timeScale` directly — not through the
                        // cheat bridge, so not through the bridge's decorator. It is ONE lever
                        // whatever the factor: a number is not a command, and "may a delivered
                        // shot slow this game down" is the question a person can answer.
                        else if (kind == "timeScale") Add(new JValue(TimeScaleLever));
                    }
                }
            }

            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>The same list, from texts rather than from disk (the sync handler has them in
        /// hand before they are written).</summary>
        public static IReadOnlyList<string> NeededFrom(string? shotsText, string? adapterText) =>
            Union(NeededFromShotsJson(shotsText), NeededFromAdapterJson(adapterText));

        /// <summary>The same list, from two documents already parsed (tenth audit, S1: <see cref="SyncNova"/>'s delivery
        /// check).</summary>
        internal static IReadOnlyList<string> NeededFromParsed(JObject shots, JObject? adapter) =>
            Union(NeededFromShots(shots), NeededFromAdapter(adapter));

        private static IReadOnlyList<string> Union(IReadOnlyList<string> shotsLevers, IReadOnlyList<string> adapterLevers)
        {
            var found = new List<string>(shotsLevers);
            var distinct = new HashSet<string>(found, StringComparer.Ordinal);
            foreach (var lever in adapterLevers)
                if (distinct.Add(lever))
                    found.Add(lever);
            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>
        /// THE ADAPTER HALF, FROM ONE DOCUMENT — everything an <c>adapter.json</c> asks to write
        /// into the studio's running game, sorted ordinal and distinct. The hosted lint computes
        /// the same list from the same document, and
        /// <c>Editor/Tests/Fixtures/adapter-levers.cases.json</c> is the file BOTH sides are run
        /// against, case by case: a lever this side names differently is a send the website can
        /// never complete ("DIFFERENT lever lists"), so the two definitions are pinned to shared
        /// data rather than to each other's source.
        ///
        /// Three things are in it:
        /// <list type="bullet">
        /// <item>the <c>ready</c> block's commands — <c>mute</c>, <c>resistOn</c>, <c>resistOff</c>, and, since the
        /// fourteenth audit, <c>muteGet</c> as the command the ready gate sends, <see cref="MuteGetLever"/>: a `get` runs
        /// property getters in the studio's game, which is code, so it is a lever like any other.</item>
        /// <item><c>hide-overlay &lt;TypeName&gt;</c>, one per <c>overlayTypeNames</c> entry — a
        /// MonoBehaviour disabled by type name, which is a write that never touches
        /// <see cref="ICheatBridge"/>.</item>
        /// <item><see cref="CameraSpecLever"/> when the <c>camera</c> block says anything (second
        /// audit, M9). That block is cloud content that decides WHICH METHODS a
        /// <c>camera-pose</c> step invokes and on which Component — `camera-pose 1 2 3 …` is one
        /// tick, and it said nothing about `DeleteSave` being the method it calls.</item>
        /// </list>
        /// </summary>
        public static IReadOnlyList<string> NeededFromAdapterJson(string? adapterText)
        {
            JObject? root = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(adapterText)) root = NovaJson.ParseObject(adapterText!);
            }
            catch (Exception)
            {
                // Not JSON: no levers HERE (the window, the gate). A delivery is refused instead (tenth audit, S1).
            }
            return NeededFromAdapter(root);
        }

        /// <summary>The same list, from a document already parsed, or none (tenth audit, S1). Overlays and the camera
        /// block are read through <see cref="AdapterJson.From"/>, the reader the director and CameraPose use.</summary>
        internal static List<string> NeededFromAdapter(JObject? root)
        {
            var found = new List<string>();
            // A set beside the list, as NeededFromShots has (the eleventh audit, M2): List.Contains made a 64 KB adapter.json
            // of 10,905 short overlay names take 860 ms to count. The answer is the same list.
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            void Add(string? s)
            {
                if (string.IsNullOrEmpty(s) || !distinct.Add(s!)) return;
                found.Add(s!);
            }
            void AddToken(JToken? t)
            {
                if (t == null || t.Type != JTokenType.String) return;
                Add(t.Value<string>());
            }

            if (root?["ready"] is JObject ready)
            {
                AddToken(ready["mute"]);
                // the fourteenth audit: the read too, as the command the ready gate sends — exactly "get " + the text
                // (HygieneReadyGate.WaitUntilReady), because the tick is compared with what runs, ordinal
                if (ready["muteGet"] is { Type: JTokenType.String } muteGet && muteGet.Value<string>() is { Length: > 0 } read)
                    Add(MuteGetLever(read));
                AddToken(ready["resistOn"]);
                AddToken(ready["resistOff"]);
            }

            // Read through the same parser the director and CameraPose use, so the list asked
            // about and the list acted on cannot differ.
            var adapter = AdapterJson.From(root);
            foreach (var type in adapter.OverlayTypeNames)
                Add(HideOverlayLever(type));
            if (adapter.Camera is { } camera && CameraSpecNeeded(camera))
                Add(CameraSpecLever(camera));

            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>The lever a <c>ready.muteGet</c> is — the command <c>HygieneReadyGate</c> sends, exactly: <c>get </c> and
        /// the text as written (the fourteenth audit). The hosted lint builds the same string (<c>lintAdapterJson</c>).</summary>
        public static string MuteGetLever(string muteGet) => "get " + muteGet;

        /// <summary>The one sentence a lever over <see cref="MaxLeverLength"/> is refused with — by <see cref="SyncNova"/>'s
        /// lever check and, prefixed with the field, its ready-block check: the lever's first 40 characters (never half a
        /// surrogate pair), then its length.</summary>
        internal static string TooLongLeverRefusal(string lever)
        {
            var shown = char.IsHighSurrogate(lever[39]) ? 39 : 40; // never half a pair
            return $"the lever {Newtonsoft.Json.JsonConvert.ToString(lever.Substring(0, shown) + "…")} is {lever.Length} characters long, " +
                   $"and this kit takes levers of at most {MaxLeverLength} — nothing was written";
        }

        /// <summary>The verb of <see cref="CameraSpecLever"/>, named once so the kit, the window
        /// and the hosted lint cannot spell it differently.</summary>
        public const string CameraSpecPrefix = "camera-spec";

        /// <summary>
        /// THE LEVER AN adapter.json <c>camera</c> BLOCK NEEDS — exactly
        /// <c>camera-spec &lt;viewType&gt; &lt;setPosition&gt; &lt;setRotation&gt; &lt;setFov&gt;</c>,
        /// single spaces, every value trimmed, a blank or absent <c>viewType</c> written <c>*</c>
        /// and a blank or absent method name written as its own default. The website computes the
        /// same string character for character; a mismatch fails every send, which is why the shape
        /// is spelled out here rather than formatted at each call site.
        ///
        /// WHY IT IS A LEVER AT ALL: those four values decide which three METHODS a <c>camera-pose</c>
        /// step invokes, and — when the step names no Type of its own — which Component it finds. A
        /// step that DOES name a Type is refused on a delivered project unless that Type is this
        /// view type (fourth audit, M2: the step's own Type used to win silently; a spec with no view
        /// type, <c>*</c>, leaves the step's Type standing). A tick on `camera-pose 1 2 3 …` approves
        /// a framing, not `DeleteSave` being the method that framing calls.
        /// </summary>
        public static string CameraSpecLever(CameraAdapter camera) =>
            CameraSpecPrefix + " " + Token(camera.ViewType, "*")
            + " " + Token(camera.SetPosition, "SetPosition")
            + " " + Token(camera.SetRotation, "SetRotation")
            + " " + Token(camera.SetFov, "SetFov");

        /// <summary>
        /// Does this <c>camera</c> block need a tick? Only when it says something a default does
        /// not: a view type, or a method name that is not the one the kit would have called anyway.
        /// An absent or empty block, and a block that spells the three defaults out, ask for
        /// nothing — there is nothing there for a person to decide.
        /// </summary>
        public static bool CameraSpecNeeded(CameraAdapter camera) =>
            !string.IsNullOrWhiteSpace(camera.ViewType)
            || !string.Equals(Token(camera.SetPosition, "SetPosition"), "SetPosition", StringComparison.Ordinal)
            || !string.Equals(Token(camera.SetRotation, "SetRotation"), "SetRotation", StringComparison.Ordinal)
            || !string.Equals(Token(camera.SetFov, "SetFov"), "SetFov", StringComparison.Ordinal);

        private static string Token(string? value, string whenBlank) =>
            string.IsNullOrWhiteSpace(value) ? whenBlank : value!.Trim();
    }
}
