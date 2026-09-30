using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// v3 P5 — a screen of named elements for the start path, the dismiss loop and the director's kit-initiated presses: a
    /// click runs the element's scripted effect (a popup rises, a panel opens). The same shape as
    /// <c>RecipeReplayTests.FakeScreen</c>, shared by the P5 suites.
    /// </summary>
    /// <summary>Kit 0.14.1 (invariant 187) — the start check's ask, faked: what it asked, whether it was ended, and a person
    /// who (on the ask) does something to the game and/or presses Continue.</summary>
    internal sealed class FakeAsk : IStartAsk
    {
        public readonly List<string> Asked = new();
        public bool Ended;
        /// <summary>What the person does when asked (put the game somewhere); null = nobody is at the editor.</summary>
        public Action? Person;
        /// <summary>The person presses Continue after doing it.</summary>
        public bool PressesContinue = true;
        private bool _continued;

        public void Begin(string sentence)
        {
            Asked.Add(sentence);
            Ended = false;
            _continued = false;
            if (Person != null)
            {
                Person();
                _continued = PressesContinue;
            }
        }

        public bool Continued => _continued;
        public void End() => Ended = true;
    }

    internal sealed class FakeReplayScreen : IReplayScreen
    {
        public readonly List<ElementIdentity> Elements = new();
        public readonly HashSet<string> Roots = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Action> OnClick = new(StringComparer.Ordinal);
        public readonly List<string> Clicked = new();
        /// <summary>Called on every <see cref="Observe"/> and <see cref="Exists"/> (a test can raise a popup on a timer).</summary>
        public Action? OnQuery;

        public ElementIdentity Add(string name, string? label = null, string? handler = null)
        {
            var e = new ElementIdentity { Name = name, Label = label, Handler = handler };
            Elements.Add(e);
            return e;
        }

        public void Remove(string name) => Elements.RemoveAll(e => e.Name == name);

        /// <summary>A popup: its root and its close; pressing the close removes both.</summary>
        public void Popup(string root, string close, params string[] inside)
        {
            Roots.Add(root);
            Add(close);
            foreach (var n in inside) Add(n);
            OnClick[close] = () =>
            {
                Roots.Remove(root);
                Remove(close);
                foreach (var n in inside) Remove(n);
            };
        }

        private ElementIdentity? Find(string selector)
        {
            var (name, label) = UguiDriver.ParseSelector(selector);
            return Elements.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
                && (label == null || (e.Label != null && UguiDriver.LabelMatches(e.Label, label))));
        }

        public ScreenSignature.Observation Observe()
        {
            OnQuery?.Invoke();
            var o = new ScreenSignature.Observation { Roots = Roots.OrderBy(r => r, StringComparer.Ordinal).ToList() };
            foreach (var e in Elements) o.Names.Add(e.Name);
            return o;
        }

        public IReadOnlyList<ElementIdentity> Clickables() => Elements.ToList();
        public bool Exists(string selector)
        {
            OnQuery?.Invoke();
            return Find(selector) != null;
        }

        public bool Click(string selector)
        {
            var e = Find(selector);
            if (e == null) return false;
            Clicked.Add(selector);
            if (OnClick.TryGetValue(e.Name, out var effect)) effect();
            return true;
        }

        public void PointerDown(string selector) => Clicked.Add("down:" + selector);
        public void PointerUp(string selector) => Clicked.Add("up:" + selector);

        /// <summary>A recipe as the kit reads it: click steps by name, its closes, its arrival, and the taught roots after
        /// each goal step (screens).</summary>
        public static RecipeFile.Recipe Recipe(string id, string[] clicks, string[]? dismiss = null, string[]? arrival = null,
            string[][]? taughtRoots = null, string? startsFrom = null)
        {
            var r = new RecipeFile.Recipe
            {
                Id = id,
                Ask = "show me " + id,
                StartKind = startsFrom == null ? TeachAnalysis.StartLobby : TeachAnalysis.StartRecipe,
                StartRecipeId = startsFrom,
                KitVersion = "0.13.0",
                Steps = new JArray(clicks.Select(c => (JToken)new JObject { ["kind"] = "click", ["name"] = c })),
                Dismiss = (dismiss ?? Array.Empty<string>()).ToList(),
                Arrival = (arrival ?? Array.Empty<string>()).ToList(),
                Confirmed = true,
            };
            if (taughtRoots != null)
                for (var i = 0; i < taughtRoots.Length; i++)
                    r.Screens.Add(new RecipeFile.Screen { Step = i, Role = "goal", Roots = taughtRoots[i].ToList() });
            return r;
        }
    }
}
