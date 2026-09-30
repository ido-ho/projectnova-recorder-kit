using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P4 (§3.6a item 3) — WHAT AN ELEMENT IS, in three parts: its GameObject NAME, its visible LABEL
    /// (<see cref="UguiDriver.LabelOf"/>, the label a <c>Name@Label</c> selector matches), and its HANDLER — the persistent
    /// calls wired on its click in the editor (<c>Type.Method</c>, read with <see cref="UnityEventBase.GetPersistentTarget"/>
    /// and <see cref="UnityEventBase.GetPersistentMethodName"/>). A code-wired button has no persistent call; its handler
    /// is null and only two parts can ever match.
    ///
    /// Recorded for every UI press a Teach makes. After a game update renames or relabels a button, the replay can find
    /// the element by two of its three parts (<see cref="RecipeRebind"/>) — as a CANDIDATE only.
    ///
    /// READ-ONLY: reads components and the persistent-call list; invokes nothing, adds no listener.
    /// </summary>
    public sealed class ElementIdentity
    {
        public const int MaxHandlerChars = 200;

        public string Name = "";
        public string? Label;
        public string? Handler;

        public static ElementIdentity Of(GameObject go) => new()
        {
            Name = go.name,
            Label = UguiDriver.LabelOf(go),
            Handler = HandlerOf(go),
        };

        /// <summary>The persistent calls on the element's click (a Button's <c>onClick</c>, a Toggle's
        /// <c>onValueChanged</c>), as <c>Type.Method</c> joined by <c>,</c> — or null when none is wired in the editor.</summary>
        public static string? HandlerOf(GameObject go)
        {
            UnityEventBase? ev = go.TryGetComponent<Button>(out var b) ? b.onClick
                : go.TryGetComponent<Toggle>(out var t) ? t.onValueChanged
                : null;
            if (ev == null) return null;
            var parts = new List<string>();
            for (var i = 0; i < ev.GetPersistentEventCount(); i++)
            {
                var method = ev.GetPersistentMethodName(i);
                if (string.IsNullOrEmpty(method)) continue;
                var target = ev.GetPersistentTarget(i);
                parts.Add((target != null ? target.GetType().Name : "?") + "." + method);
            }
            if (parts.Count == 0) return null;
            var joined = string.Join(",", parts);
            return joined.Length <= MaxHandlerChars ? joined : joined.Substring(0, MaxHandlerChars);
        }

        public JObject ToJson() => new()
        {
            ["name"] = Name,
            ["label"] = Label == null ? JValue.CreateNull() : Label,
            ["handler"] = Handler == null ? JValue.CreateNull() : Handler,
        };

        /// <summary>Never throws: anything that is not an object with a string name reads as null.</summary>
        public static ElementIdentity? FromJson(JToken? token)
        {
            if (token is not JObject o || o["name"]?.Type != JTokenType.String) return null;
            var name = o["name"]!.Value<string>() ?? "";
            if (name.Length == 0) return null;
            return new ElementIdentity
            {
                Name = name,
                Label = o["label"]?.Type == JTokenType.String ? o["label"]!.Value<string>() : null,
                Handler = o["handler"]?.Type == JTokenType.String ? o["handler"]!.Value<string>() : null,
            };
        }

        /// <summary>How many of the three parts match another element: the name exactly (ordinal, as the export writes
        /// it), the label ignoring case, the handler exactly. A part that is null on EITHER side never matches — two
        /// code-wired buttons do not "share a handler".</summary>
        public int PartsMatching(ElementIdentity other)
        {
            var n = 0;
            if (Name.Length > 0 && string.Equals(Name, other.Name, StringComparison.Ordinal)) n++;
            if (Label != null && other.Label != null && string.Equals(Label, other.Label, StringComparison.OrdinalIgnoreCase)) n++;
            if (Handler != null && other.Handler != null && string.Equals(Handler, other.Handler, StringComparison.Ordinal)) n++;
            return n;
        }

        public override string ToString() =>
            Name + (Label != null ? "@" + Label : "") + (Handler != null ? " → " + Handler : "");
    }
}
