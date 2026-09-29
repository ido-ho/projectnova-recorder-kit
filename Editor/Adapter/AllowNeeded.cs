using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Invariant 190 (kit 0.14.2) — "ALLOW THESE N". The owner, 2026-09-29: "the UI and the steps are really overwhelming —
    /// automate as much as possible". The website works out the EXACT levers the studio's current work needs — the shots in
    /// the send, the cheats its current stories name, the go-home lever, the adapter's hygiene — and delivers them with the
    /// tick list (<see cref="TickList.NeededLever"/>). The Nova Capture window shows them at the top by their plain names,
    /// and ONE press ticks them all.
    ///
    /// THE PRESS IS STILL A PERSON'S, AND IT WRITES NOTHING THE PER-ROW TOGGLES COULD NOT (invariant 173): each command goes
    /// through <see cref="Levers.SetApproved"/> — the one writer of levers.json — with its check's read
    /// (<see cref="Levers.CompanionTicks"/>), exactly the loop a row's tick runs. Only a FIXED command is ever written (the
    /// reader refuses a template in <c>needed</c>, and <see cref="Levers.NotTickableReason"/> is asked again here). A risky
    /// command is ticked like any row and still WAITS at the gate until "this editor talks to a non-production server" is
    /// ticked (<see cref="Levers.RiskyNotYet"/>, the gate's own rule) — the window says which ones before and after the press.
    /// A command already ticked (<see cref="Levers.IsTicked"/>, the gate's covering rule) is not counted.
    ///
    /// THE PRESS APPROVES WHAT THE PERSON SAW (audits F4, F5): the window lists EVERY command the press will write — no cap —
    /// each with its exact command under its name (<see cref="RowsOf"/>); a filled set-value command, a recipe's cheat or the
    /// go-home lever may have no row under Details, so this list is the only place it is shown. The press is handed the
    /// list the window DREW; the list is read from disk again at the press and, when it no longer names exactly those
    /// commands, nothing is written — "The list changed — look again." (<see cref="ListChanged"/>).
    /// </summary>
    public static class AllowNeeded
    {
        public sealed class Item
        {
            public string Command { get; set; } = "";
            /// <summary>What a person reads: the delivered label, else the command.</summary>
            public string Name { get; set; } = "";
            /// <summary>The delivered candidate's kind for this exact command, or null.</summary>
            public string? Kind { get; set; }
            /// <summary>Why the gate would hold it until the non-production tick (<see cref="Levers.RiskyNotYet"/>), or null.</summary>
            public string? RiskyWhy { get; set; }
        }

        public sealed class View
        {
            /// <summary>The needed levers not ticked yet — what one press writes.</summary>
            public IReadOnlyList<Item> ToAllow { get; set; } = Array.Empty<Item>();
            /// <summary>Of those, the risky ones (they are ticked by the press and wait at the gate).</summary>
            public IReadOnlyList<Item> HeldAfterPress => ToAllow.Where(i => i.RiskyWhy != null).ToList();
            /// <summary>Needed levers ALREADY ticked that the gate still holds until the non-production tick.</summary>
            public IReadOnlyList<Item> TickedButHeld { get; set; } = Array.Empty<Item>();
            /// <summary>How many levers the delivered list names in all.</summary>
            public int NeededTotal { get; set; }
            /// <summary>Why tick-list.json could not be read, or null.</summary>
            public string? Unreadable { get; set; }
        }

        /// <summary>The needed set on this project now. Never throws.</summary>
        public static View For(string projectRoot)
        {
            try
            {
                var candidates = TickList.Read(projectRoot, out var unreadable, out var needed);
                var approved = Levers.Approved(projectRoot);
                var nonProduction = Levers.NonProductionTicked(projectRoot);
                var toAllow = new List<Item>();
                var tickedHeld = new List<Item>();
                foreach (var n in needed)
                {
                    var item = new Item
                    {
                        Command = n.Command,
                        Name = string.IsNullOrWhiteSpace(n.Label) ? n.Command : n.Label!,
                        Kind = candidates.FirstOrDefault(c => string.Equals(c.Command, n.Command, StringComparison.Ordinal))?.Kind,
                        RiskyWhy = Levers.RiskyNotYet(n.Command, nonProduction, candidates),
                    };
                    if (Levers.IsTicked(approved, n.Command))
                    {
                        if (item.RiskyWhy != null) tickedHeld.Add(item);
                        continue;
                    }
                    // one the window could not tick is left to its own row under Details (it says why there)
                    if (Levers.NotTickableReason(n.Command) != null) continue;
                    toAllow.Add(item);
                }
                return new View { ToAllow = toAllow, TickedButHeld = tickedHeld, NeededTotal = needed.Count, Unreadable = unreadable };
            }
            catch (Exception e)
            {
                return new View { Unreadable = e.Message };
            }
        }

        /// <summary>Audit F5 — what the press says when the list on disk is no longer the one the window drew.</summary>
        public const string ListChanged = "The list changed — look again.";

        /// <summary>Audit F4 — what the window lists for the press, one row per command it writes (ALL of them): the plain
        /// name (with its kind, and "risky" when the gate will hold it) and, on the line under it, the exact command.</summary>
        public static IReadOnlyList<(string Title, string Command)> RowsOf(View v) =>
            v.ToAllow.Select(i => ("• " + i.Name + (i.Kind != null && i.Kind != "other" ? $" ({i.Kind})" : "")
                                   + (i.RiskyWhy != null ? " — risky" : ""), i.Command)).ToList();

        /// <summary>
        /// THE PRESS: tick every needed lever the window drew (<paramref name="drawn"/>) — only when the list read from disk
        /// NOW still names exactly those commands, else nothing is written and <see cref="ListChanged"/> is returned — each
        /// with its check's read, through the one writer. Stops at the first write that fails and returns its reason (nothing
        /// after it is written); null when all were written. <paramref name="written"/> counts the needed levers ticked.
        /// </summary>
        public static string? AllowAll(string projectRoot, IReadOnlyList<Item> drawn, out int written)
        {
            written = 0;
            var now = For(projectRoot);
            if (now.Unreadable != null) return "The website's cheat list could not be read: " + now.Unreadable;
            if (!now.ToAllow.Select(i => i.Command).SequenceEqual(drawn.Select(i => i.Command), StringComparer.Ordinal))
                return ListChanged;
            foreach (var item in now.ToAllow)
            {
                if (Levers.SetApproved(projectRoot, item.Command, true) is { } why) return why;
                foreach (var companion in Levers.CompanionTicks(projectRoot, item.Command))
                    if (Levers.SetApproved(projectRoot, companion, true) is { } companionWhy)
                        return companionWhy;
                written++;
            }
            return null;
        }

        /// <summary>The line under the heading: "N cheats your ads need", or null when nothing is left to allow.</summary>
        public static string? Heading(View v) =>
            v.ToAllow.Count == 0 ? null : $"{v.ToAllow.Count} cheat{(v.ToAllow.Count == 1 ? "" : "s")} your ads need";

        /// <summary>The button's words.</summary>
        public static string ButtonLabel(View v) =>
            v.ToAllow.Count == 1 ? "Allow this one" : $"Allow these {v.ToAllow.Count}";

        /// <summary>ONE sentence naming what the non-production tick still holds (before or after the press), or null.</summary>
        public static string? HeldSentence(View v)
        {
            var held = v.HeldAfterPress.Concat(v.TickedButHeld).ToList();
            if (held.Count == 0) return null;
            var names = string.Join(", ", held.Take(4).Select(i => $"“{i.Name}”")) + (held.Count > 4 ? $" and {held.Count - 4} more" : "");
            return $"{names} {(held.Count == 1 ? "is" : "are")} risky and will wait until you tick “{CheatRisk.NonProductionLabel}” under Details › Safety.";
        }
    }
}
