using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// In-process UGUI find / click / pointer-hold / text query. Case-insensitive name search
    /// across all active canvases. Runs on the main thread during Play Mode. uGUI only — other
    /// UI stacks supply their own IUiDriver via GameAdapter.Ui.
    /// </summary>
    public sealed class UguiDriver : IUiDriver
    {
        public bool Exists(string name) => Find(name, 0) != null;

        /// <summary>
        /// Uses Selectable.IsInteractable(), which accounts for the whole CanvasGroup chain — so a
        /// button under a full-screen overlay that blocks interaction reads false, exactly as the
        /// player experiences it. An object with no Selectable is reported interactable when present,
        /// since there is no interactability to check and Exists is then the honest answer.
        /// </summary>
        public bool IsInteractable(string name)
        {
            var go = Find(name, 0);
            if (go == null)
                return false;
            return !go.TryGetComponent<Selectable>(out var sel) || sel.IsInteractable();
        }

        public string? GetText(string name)
        {
            var go = Find(name, 0);
            if (go == null)
                return null;
            if (TryGetTmpText(go, out var tmpText))
                return tmpText;
            if (go.TryGetComponent<Text>(out var legacy))
                return legacy.text;
            TryGetTmpText(go, out var childTmpText, includeChildren: true);
            return childTmpText;
        }

        /// <summary>TMP is reached reflectively (see LabelOf) so this works without the package —
        /// live-confirmed 2026-08-03: a compile-time `using TMPro` here was the only thing stopping
        /// the kit from building in a fresh project with no game (and so no TextMeshPro) already
        /// installed, despite LabelOf right below already doing exactly this for the same type.</summary>
        private static bool TryGetTmpText(GameObject go, out string? text, bool includeChildren = false)
        {
            var tmpType = GameReflection.FindType("TMP_Text") ?? GameReflection.FindType("TextMeshProUGUI");
            if (tmpType == null) { text = null; return false; }
            var comp = includeChildren ? go.GetComponentInChildren(tmpType) : go.GetComponent(tmpType);
            if (comp == null) { text = null; return false; }
            text = tmpType.GetProperty("text")?.GetValue(comp) as string;
            return true;
        }

        public bool Click(string name, int index = 0)
        {
            var target = Find(name, index);
            if (target == null || !target.activeInHierarchy)
                return false;
            if (target.TryGetComponent<Selectable>(out var sel) && !sel.IsInteractable())
                return false;
            var data = MakeEvent(target);
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target) ?? target;
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler);
            return true;
        }

        public void PointerDown(string name, int index = 0)
        {
            var target = Find(name, index);
            if (target == null)
                return;
            var data = MakeEvent(target);
            var handler = ExecuteEvents.GetEventHandler<IPointerDownHandler>(target) ?? target;
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerDownHandler);
        }

        public void PointerUp(string name, int index = 0)
        {
            var target = Find(name, index);
            if (target == null)
                return;
            var data = MakeEvent(target);
            var handler = ExecuteEvents.GetEventHandler<IPointerUpHandler>(target) ?? target;
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerUpHandler);
        }

        private static PointerEventData MakeEvent(GameObject target) => new(EventSystem.current)
        {
            position = ScreenPos(target),
            button = PointerEventData.InputButton.Left,
        };

        /// <summary>THE object <c>click name index</c> presses (and, at index 0, the object every
        /// condition checks), or null. Internal so the on-screen dump can ask it whether the name and
        /// index a row prints reach THAT row's object (E.2's fifth audit, the dump marker) — the one
        /// finder, never a second copy of its matching.</summary>
        internal static GameObject? Find(string name, int index)
        {
            var matches = MatchesInClickOrder(name);
            return index >= 0 && index < matches.Count ? matches[index] : null;
        }

        /// <summary>
        /// Every active object matching <paramref name="name"/>, in the SAME order the index argument of
        /// Click/Hold selects from.
        ///
        /// Public so `ui-dump` can report indices that actually correspond to what `click name index`
        /// will hit. Before this, the dump enumerated via FindObjectsByType while clicking walked the
        /// canvas tree, so a printed index would have pointed somewhere else entirely — and a game whose
        /// clickable is generically named (three separate objects called "Button") could not be targeted
        /// at all except by guessing.
        /// </summary>
        public static IReadOnlyList<GameObject> MatchesInClickOrder(string selector)
        {
            var (name, label) = ParseSelector(selector);
            var matches = new List<GameObject>();
            var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            foreach (var canvas in canvases)
            {
                if (canvas.gameObject.activeInHierarchy)
                    SearchRecursive(canvas.transform, name, matches);
            }
            if (label == null)
                return matches;
            return matches.FindAll(go => LabelOf(go) is { } l &&
                l.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Split "Name@Label" into its parts; label is null for a plain name.
        ///
        /// Exists because INDEX-BASED TARGETING OF GENERIC NAMES IS UNRELIABLE, which cost a live
        /// debugging round. One game names its level button, main-menu button and spin button all
        /// "Button", and the index that distinguishes them comes from Unity's FindObjectsByType
        /// enumeration order — which is explicitly unordered and DID change across a scene reload:
        /// "Level 1" was #2 in one session and #1 in the next, so a shot encoding index 2 silently
        /// clicked "Spin".
        ///
        /// A label is the stable identifier: it is authored content, it is what a human or agent reads
        /// off the screen, and `ui-dump` already prints it. Accepting it inside the name string rather
        /// than as a new parameter means EVERY name-taking API gains it at once — Click, Hold, and every
        /// WaitCondition (`Present("Button@Level 1")`).
        /// </summary>
        internal static (string Name, string? Label) ParseSelector(string selector)
        {
            var at = selector.IndexOf('@');
            if (at < 0)
                return (selector, null);
            var label = selector.Substring(at + 1).Trim();
            return (selector.Substring(0, at).Trim(), label.Length == 0 ? null : label);
        }

        /// <summary>Visible label from uGUI Text or TextMeshPro, searched into children (the label is
        /// usually a child of the button). TMP is reached reflectively so this works without the package.</summary>
        internal static string? LabelOf(GameObject go)
        {
            var text = go.GetComponentInChildren<Text>();
            if (text != null && !string.IsNullOrWhiteSpace(text.text))
                return text.text.Trim();

            var tmpType = GameReflection.FindType("TMP_Text") ?? GameReflection.FindType("TextMeshProUGUI");
            if (tmpType == null)
                return null;
            var comp = go.GetComponentInChildren(tmpType);
            return comp != null && tmpType.GetProperty("text")?.GetValue(comp) is string s
                   && !string.IsNullOrWhiteSpace(s)
                ? s.Trim()
                : null;
        }

        /// <summary>
        /// EVERY ACTIVE NAME ON EVERY ACTIVE CANVAS, IN CLICK ORDER — one walk, then any number of lookups (the fourteenth
        /// audit, S2). `ui-dump` numbered each candidate by calling <see cref="MatchesInClickOrder"/> for it, and each call
        /// walked every active canvas again: 0.5 ms a Selectable at 3,000 nodes, 150 ms in one pump at 300. This walks the
        /// same canvases, in the same order, with the same recursion — so a canvas nested in another is listed from both,
        /// as the per-call walk listed it — and keeps every name's matches in that order. <see cref="Matches"/> answers a
        /// selector exactly as <see cref="MatchesInClickOrder"/> does (<c>TheOneWalkIndexListsEverySelectorsMatchesInTheClickOrder</c>
        /// holds the two to one answer); a `@label` narrows the name's list without a second walk. A snapshot: build it again
        /// after the UI changes.
        /// </summary>
        internal sealed class ClickOrderIndex
        {
            private static readonly IReadOnlyList<GameObject> None = Array.Empty<GameObject>();
            private readonly Dictionary<string, List<GameObject>> _byName = new(StringComparer.OrdinalIgnoreCase);

            public static ClickOrderIndex Build()
            {
                var index = new ClickOrderIndex();
                foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                {
                    if (canvas.gameObject.activeInHierarchy)
                        index.Walk(canvas.transform);
                }
                return index;
            }

            private void Walk(Transform parent)
            {
                for (var i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    if (!child.gameObject.activeInHierarchy)
                        continue;
                    if (!_byName.TryGetValue(child.name, out var list))
                        _byName[child.name] = list = new List<GameObject>(1);
                    list.Add(child.gameObject);
                    Walk(child);
                }
            }

            /// <summary>What <see cref="UguiDriver.Find"/> returns for <paramref name="selector"/> and <paramref name="index"/>,
            /// from the snapshot — the dump marker's question asked without a walk per row (stack pass 4).</summary>
            public GameObject? Find(string selector, int index)
            {
                var matches = Matches(selector);
                return index >= 0 && index < matches.Count ? matches[index] : null;
            }

            /// <summary>What <see cref="MatchesInClickOrder"/> returns for <paramref name="selector"/>, from the snapshot.</summary>
            public IReadOnlyList<GameObject> Matches(string selector)
            {
                var (name, label) = ParseSelector(selector);
                if (!_byName.TryGetValue(name, out var matches))
                    return None;
                if (label == null)
                    return matches;
                return matches.FindAll(go => LabelOf(go) is { } l &&
                    l.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        private static void SearchRecursive(Transform parent, string name, List<GameObject> results)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (!child.gameObject.activeInHierarchy)
                    continue;
                if (child.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    results.Add(child.gameObject);
                SearchRecursive(child, name, results);
            }
        }

        private static Vector2 ScreenPos(GameObject go)
        {
            var rect = go.GetComponent<RectTransform>();
            if (rect == null)
                return Vector2.zero;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var canvas = go.GetComponentInParent<Canvas>();
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;
            if (cam != null)
                return cam.WorldToScreenPoint((corners[0] + corners[2]) / 2f);
            return ((Vector2)(corners[0] + corners[2])) / 2f;
        }
    }
}
