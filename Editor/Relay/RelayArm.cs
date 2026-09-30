using System;
using System.Globalization;
using UnityEditor;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Kit 0.14.3 (SECURITY-FINDINGS F4, deep review KIT-1, invariant 192) — THE FILE RELAY'S ARM SWITCH.
    ///
    /// Until 0.14.3 the relay executed every <c>*.json</c> dropped in <c>Library/AdRelay/commands/</c> whenever the Editor
    /// was open — no switch, no indicator, no idle stop. It is the OPERATOR's local channel (<c>admiral relay</c>, the
    /// <c>/onboard-game</c> session, the <c>unity</c> CLI bridge); nothing in the website-driven flow uses it — the cloud agent
    /// pulls jobs over HTTPS (<see cref="NovaCaptureAgent"/>). So a studio's editor now has it OFF:
    /// <list type="bullet">
    /// <item>armed only by a person, from <c>Tools › Recorder Kit › File Relay › Arm for 60 minutes</c> (or the Nova Capture
    /// window's Details › File relay), per project, in <c>EditorPrefs</c> — never in a file under the repo;</item>
    /// <item>it disarms itself after <see cref="IdleMinutes"/> minutes without a command, and every command it runs pushes
    /// that back (<see cref="Touch"/>) — an operator session that keeps working stays armed, one left overnight does not;</item>
    /// <item>while armed, the Nova Capture window says so at the top of its main view, with a Disarm button;</item>
    /// <item>disarmed, a dropped command is ANSWERED — refused, with the sentence that says how to arm it
    /// (<see cref="DisarmedAnswer"/>) — and never run, so an operator's client gets a reason instead of a timeout.</item>
    /// </list>
    /// The heartbeat (<c>status.json</c>) is written either way: it says the editor is alive, and runs nothing.
    /// </summary>
    public static class RelayArm
    {
        /// <summary>How long the relay stays armed after it was armed, or after its last command.</summary>
        public const int IdleMinutes = 60;

        public const string DisarmedAnswer =
            "the file relay is not armed in this editor — nothing was run. A person at this machine arms it from " +
            "Tools › Recorder Kit › File Relay › Arm for 60 minutes (it disarms itself after 60 minutes without a command), " +
            "then send the command again under a new id";

        private static string Key(string projectRoot) =>
            "ProjectNova.RecorderKit.Relay." + NovaCaptureAgent.ProjectKeyOf(projectRoot) + ".ArmedUntilUtc";

        /// <summary>THE RULE, pure: armed while the stored "until" instant is in the future. Anything unreadable is disarmed.</summary>
        public static bool IsArmed(string? storedUntil, DateTime nowUtc) =>
            TryParse(storedUntil, out var until) && nowUtc < until;

        private static bool TryParse(string? stored, out DateTime until) =>
            DateTime.TryParseExact(stored ?? "", "o", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out until);

        /// <summary>The "until" to store when armed (or touched) at <paramref name="nowUtc"/>.</summary>
        public static string UntilFrom(DateTime nowUtc) =>
            nowUtc.AddMinutes(IdleMinutes).ToString("o", CultureInfo.InvariantCulture);

        public static bool IsArmed(string projectRoot) =>
            IsArmed(EditorPrefs.GetString(Key(projectRoot), ""), DateTime.UtcNow);

        /// <summary>When it disarms itself, or null when it is not armed.</summary>
        public static DateTime? ArmedUntil(string projectRoot)
        {
            var stored = EditorPrefs.GetString(Key(projectRoot), "");
            return IsArmed(stored, DateTime.UtcNow) && TryParse(stored, out var until) ? until : null;
        }

        public static void Arm(string projectRoot) =>
            EditorPrefs.SetString(Key(projectRoot), UntilFrom(DateTime.UtcNow));

        public static void Disarm(string projectRoot) => EditorPrefs.DeleteKey(Key(projectRoot));

        /// <summary>A command ran: push the idle stop back. A relay that is not armed stays not armed.</summary>
        public static void Touch(string projectRoot)
        {
            if (IsArmed(projectRoot)) Arm(projectRoot);
        }

        [MenuItem("Tools/Recorder Kit/File Relay/Arm for 60 minutes")]
        private static void ArmMenu()
        {
            Arm(KitProject.Root());
            UnityEngine.Debug.LogWarning($"[RecorderKit] the file relay is ARMED for this project: anything that can write " +
                                         $"Library/AdRelay/commands/ can drive this editor. It disarms itself after {IdleMinutes} " +
                                         "minutes without a command (Tools › Recorder Kit › File Relay › Disarm to stop it now).");
        }

        [MenuItem("Tools/Recorder Kit/File Relay/Disarm")]
        private static void DisarmMenu()
        {
            Disarm(KitProject.Root());
            UnityEngine.Debug.Log("[RecorderKit] the file relay is disarmed for this project.");
        }

        [MenuItem("Tools/Recorder Kit/File Relay/Disarm", true)]
        private static bool DisarmMenuValid() => IsArmed(KitProject.Root());
    }
}
