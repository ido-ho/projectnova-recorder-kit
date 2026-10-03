using System;
using System.IO;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// WHICH FOLDER IS "THIS UNITY PROJECT" — one answer, for every part of the kit that asks.
    ///
    /// It used to be <c>Directory.GetCurrentDirectory()</c> in most places and
    /// <c>Application.dataPath/..</c> in the capture agent, which is *usually* the same folder and
    /// is not the same PROMISE: the working directory is process-wide state any script in the
    /// editor can change (audit M4, 2026-09-21). The parts that disagreed were the ones that must
    /// not: <c>sync-nova</c> WRITES the cloud's two files under the agent's root, while the lever
    /// gate, the Nova Capture window and the default adapter READ them from the working directory
    /// — so one <c>SetCurrentDirectory</c> anywhere in the process left the gate looking for a
    /// <c>synced.json</c> that is not there, and an un-ticked cloud cheat ran.
    ///
    /// <c>Application.dataPath</c> is the editor's own answer to "which project is open", so it
    /// cannot be repointed by anything else in the process. The working directory stays as the
    /// fallback for the one case dataPath cannot answer (off the main thread), because this value
    /// also decides where a recursive delete points.
    /// </summary>
    public static class KitProject
    {
        public static string Root()
        {
            try
            {
                var data = Application.dataPath;
                if (!string.IsNullOrEmpty(data))
                {
                    var parent = Path.GetDirectoryName(data);
                    if (!string.IsNullOrEmpty(parent)) return parent!;
                }
            }
            catch (Exception)
            {
                // Not reachable from the main thread of an open Editor, which is the only place the
                // kit runs — but this value decides where files are written and deleted, so it falls
                // back rather than throwing out of a static initialiser.
            }
            return Directory.GetCurrentDirectory();
        }
    }
}
