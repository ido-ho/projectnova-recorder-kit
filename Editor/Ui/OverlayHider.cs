using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>Disables on-screen debug overlays (by MonoBehaviour type name, per adapter)
    /// during capture; restores them after.</summary>
    public sealed class OverlayHider
    {
        private readonly List<Behaviour> _disabled = new();

        public static OverlayHider Hide(string[] typeNames)
        {
            var overlays = new OverlayHider();
            if (typeNames.Length == 0)
                return overlays;
            // ONE LOOKUP PER BEHAVIOUR (the fourteenth audit, M1): adapter.json may list one name thousands of times, and
            // Array.IndexOf compared every enabled behaviour with every copy — 131.7 ms in one pump at 6,000 behaviours.
            var wanted = new HashSet<string>(typeNames, CountingOrdinal.Instance);
            var behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var b in behaviours)
            {
                if (b != null && b.enabled) BehavioursJudgedForTests++;
                if (b != null && b.enabled &&
                    wanted.Contains(b.GetType().Name))
                {
                    b.enabled = false;
                    overlays._disabled.Add(b);
                }
            }
            return overlays;
        }

        /// <summary>For the tests (the fifteenth audit, M4): enabled behaviours judged, and name comparisons made — both
        /// counts, so the "one lookup a behaviour" pin does not rest on one wall-clock measurement.</summary>
        internal static long BehavioursJudgedForTests;
        internal static long NameComparisonsForTests;

        /// <summary>Ordinal, counting each comparison and each hash.</summary>
        private sealed class CountingOrdinal : IEqualityComparer<string>
        {
            public static readonly CountingOrdinal Instance = new();
            public bool Equals(string? x, string? y) { NameComparisonsForTests++; return string.Equals(x, y, StringComparison.Ordinal); }
            public int GetHashCode(string s) { NameComparisonsForTests++; return StringComparer.Ordinal.GetHashCode(s); }
        }

        public void Restore()
        {
            foreach (var b in _disabled)
            {
                if (b != null)
                    b.enabled = true;
            }
            _disabled.Clear();
        }
    }
}
