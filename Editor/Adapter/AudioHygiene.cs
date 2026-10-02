using System;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// F2 fresh audit L2 — the ready block's ENGINE-WIDE mute (<c>set UnityEngine.AudioListener.volume 0</c>, the write the
    /// box drafts; or <c>…AudioListener.pause true</c>) is put back when the run ends. The ready gate remembers the value
    /// once, before its first mute write of a run (a later ready in the same run would only read the 0 it wrote), and
    /// <see cref="AdDirector"/>'s <c>Finish</c> restores it beside the overlays. A game's own mute (its music source) is the
    /// game's to undo and is not touched. Whether Unity itself keeps <c>AudioListener.volume</c> after leaving play mode
    /// was not measured — this restores it either way.
    /// </summary>
    internal static class AudioHygiene
    {
        internal static Func<float> GetVolume = () => AudioListener.volume;
        internal static Action<float> SetVolume = v => AudioListener.volume = v;
        internal static Func<bool> GetPause = () => AudioListener.pause;
        internal static Action<bool> SetPause = v => AudioListener.pause = v;

        private static float? _volume;
        private static bool? _pause;

        private static bool Writes(string command, string member)
        {
            var t = (command ?? "").TrimStart();
            return t.StartsWith("set UnityEngine.AudioListener." + member + " ", StringComparison.OrdinalIgnoreCase)
                   || t.StartsWith("set AudioListener." + member + " ", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Before the ready gate's mute write: remember what an engine-wide mute is about to change (once a run).</summary>
        public static void BeforeMute(string command)
        {
            try
            {
                if (_volume == null && Writes(command, "volume")) _volume = GetVolume();
                if (_pause == null && Writes(command, "pause")) _pause = GetPause();
            }
            catch
            {
                // never let remembering a value stop a capture
            }
        }

        /// <summary>When the run ends: put back what <see cref="BeforeMute"/> remembered, then forget it. Never throws.</summary>
        public static void Restore()
        {
            try
            {
                if (_volume is { } v) SetVolume(v);
                if (_pause is { } p) SetPause(p);
            }
            catch
            {
                // the run is over either way
            }
            finally
            {
                _volume = null;
                _pause = null;
            }
        }

        /// <summary>For the tests: forget without restoring.</summary>
        internal static void ResetForTests()
        {
            _volume = null;
            _pause = null;
        }
    }
}
