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
            var behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var b in behaviours)
            {
                if (b != null && b.enabled &&
                    Array.IndexOf(typeNames, b.GetType().Name) >= 0)
                {
                    b.enabled = false;
                    overlays._disabled.Add(b);
                }
            }
            return overlays;
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
