using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P4 (§3.6a item 2) — WHICH SCREEN AM I ON, as the active PANEL ROOTS.
    ///
    /// The spike's identity was every active name on every active canvas (<c>TeachRecorder.ScreenNames()</c>), popups
    /// included, so a popup on the lobby and the lobby alone were two different "screens" and nothing said which part was
    /// the base screen. Here a screen is its ROOTS: the active direct children of every ROOT canvas
    /// (<see cref="Canvas.isRootCanvas"/> — a canvas nested in another is part of that screen, not a screen of its own).
    /// A popup is an extra root on top of the base screen's, so "the lobby with a popup" = the lobby's roots + one.
    /// A root is named <c>Canvas/Child</c> (the root canvas's name, then the root's), so two canvases that each have a
    /// <c>Panel</c> differ.
    ///
    /// THE LIMIT, said: a game that wraps every panel in ONE child of its canvas (a safe-area object) has one root per
    /// canvas, so its screens all share a signature. The rules that read roots then see no change — the dismiss/goal
    /// split keeps every press as a goal step (the safe direction: nothing needed is dropped) and arrival still reads
    /// NAMES. Looking through such a wrapper was tried and refused: a lone panel with no sibling looks exactly like a
    /// wrapper, so the signature would change shape the moment a popup is added next to it. Whether roots are stable on
    /// real games is §10's open question (the spike-B replays, at Unity).
    ///
    /// READ-ONLY: it walks transforms and reads names and component types; it changes nothing.
    /// </summary>
    public static class ScreenSignature
    {
        /// <summary>The most names one observation keeps (a screen of a real game has hundreds; the cap keeps a
        /// recipe's facts well under the result door's 1 MB).</summary>
        public const int MaxNames = 4000;
        /// <summary>The most clickable names one "after" keeps (its ui-dump).</summary>
        public const int MaxButtons = 120;
        /// <summary>The most NEW names one "after" keeps.</summary>
        public const int MaxAdded = 300;

        /// <summary>What was on screen at one moment: the roots (the signature), every active name under the root
        /// canvases, and the clickable ones.</summary>
        public sealed class Observation
        {
            public List<string> Roots = new();
            public HashSet<string> Names = new(StringComparer.Ordinal);
            public List<string> Buttons = new();
            /// <summary>The signature as one string — the same screen gives the same key.</summary>
            public string Key => string.Join("|", Roots);
        }

        /// <summary>Every active root canvas in the loaded scenes, in a stable order (by name, then instance id).</summary>
        public static List<Canvas> RootCanvases() =>
            UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
                .Where(c => c != null && c.isRootCanvas && c.gameObject.activeInHierarchy)
                .OrderBy(c => c.name, StringComparer.Ordinal)
                .ThenBy(c => c.GetInstanceID())
                .ToList();

        /// <summary>The panel roots of one root canvas, named <c>Canvas/Child</c>, in sibling order.</summary>
        public static List<string> RootsOf(Canvas root)
        {
            var list = new List<string>();
            for (var i = 0; i < root.transform.childCount; i++)
            {
                var c = root.transform.GetChild(i);
                if (c.gameObject.activeInHierarchy) list.Add(root.name + "/" + c.name);
            }
            return list;
        }

        /// <summary>The panel root an object lives under (<c>Canvas/Child</c>), or null when it is on no canvas or IS the
        /// root canvas.</summary>
        public static string? RootOf(GameObject go)
        {
            if (go == null) return null;
            var canvas = go.GetComponentInParent<Canvas>(true);
            if (canvas == null) return null;
            var root = canvas.rootCanvas;
            var t = go.transform;
            if (t == root.transform) return null;
            while (t != null && t.parent != root.transform) t = t.parent;
            return t == null ? null : root.name + "/" + t.name;
        }

        /// <summary>What is on screen NOW, over the loaded scenes' root canvases.</summary>
        public static Observation Observe() => Observe(RootCanvases());

        /// <summary>What is on screen over THESE root canvases (the tests hand their own).</summary>
        public static Observation Observe(IEnumerable<Canvas> roots)
        {
            var o = new Observation();
            var buttons = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var canvas in roots)
            {
                if (canvas == null || !canvas.gameObject.activeInHierarchy) continue;
                o.Roots.AddRange(RootsOf(canvas));
                Walk(canvas.transform, o.Names, buttons);
            }
            o.Roots.Sort(StringComparer.Ordinal);
            o.Buttons = buttons.Take(MaxButtons).ToList();
            if (o.Names.Count > MaxNames)
                o.Names = new HashSet<string>(o.Names.OrderBy(n => n, StringComparer.Ordinal).Take(MaxNames), StringComparer.Ordinal);
            return o;
        }

        private static void Walk(Transform t, HashSet<string> names, SortedSet<string> buttons)
        {
            for (var i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (!c.gameObject.activeInHierarchy) continue;
                names.Add(c.name);
                if (c.GetComponent<IPointerClickHandler>() != null) buttons.Add(c.name);
                Walk(c, names, buttons);
            }
        }

        /// <summary>The names in <paramref name="after"/> that were not in <paramref name="before"/>, sorted, capped.</summary>
        public static List<string> Added(Observation before, Observation after) =>
            after.Names.Where(n => !before.Names.Contains(n))
                .OrderBy(n => n, StringComparer.Ordinal).Take(MaxAdded).ToList();

        /// <summary>The "before" of a press as the recipe carries it: the roots only (the split reads roots; arrival
        /// reads the "after"'s added names, which are measured against the full before).</summary>
        public static JObject BeforeJson(Observation before) => new() { ["roots"] = new JArray(before.Roots) };

        /// <summary>The "after" of a press as the recipe carries it: roots, the names new since the press, the clickable
        /// names on screen (its ui-dump).</summary>
        public static JObject AfterJson(Observation before, Observation after) => new()
        {
            ["roots"] = new JArray(after.Roots),
            ["added"] = new JArray(Added(before, after)),
            ["buttons"] = new JArray(after.Buttons),
        };
    }
}
