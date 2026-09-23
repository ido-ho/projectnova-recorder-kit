using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    public sealed class NullStateProbe : IStateProbe
    {
        public static readonly NullStateProbe Instance = new();
        public string? CurrentStateName => null;
    }

    public sealed class NullCheatBridge : ICheatBridge
    {
        public static readonly NullCheatBridge Instance = new();
        public bool Run(string command)
        {
            Debug.LogWarning($"[RecorderKit] no ICheatBridge registered; cheat ignored: '{command}'");
            return false;
        }
    }

    public sealed class NullReadyGate : IReadyGate
    {
        public static readonly NullReadyGate Instance = new();
        public IEnumerable WaitUntilReady(DirectorContext ctx)
        {
            ctx.LastOpSucceeded = true;
            yield break;
        }
    }

    public sealed class NullRecoveryPolicy : IRecoveryPolicy
    {
        public static readonly NullRecoveryPolicy Instance = new();
        public IEnumerable Recover(DirectorContext ctx)
        {
            ctx.LastOpSucceeded = true;
            yield break;
        }
    }

    /// <summary>
    /// Everything the kit needs to drive one game. A studio that only added the package gets
    /// DefaultAdapterBoot's generic adapter (reflection cheats + Library/Nova/shots.json). A
    /// game-specific [InitializeOnLoad] registration still wins when one exists. Every field
    /// defaults to a null policy so the kit compiles with nothing registered.
    /// </summary>
    public sealed class GameAdapter
    {
        public static readonly GameAdapter Null = new() { GameId = "unregistered" };

        public string GameId = "";
        public IStateProbe StateProbe = NullStateProbe.Instance;
        public ICheatBridge CheatBridge = NullCheatBridge.Instance;
        public IReadyGate ReadyGate = NullReadyGate.Instance;
        public IRecoveryPolicy Recovery = NullRecoveryPolicy.Instance;
        /// <summary>Non-uGUI games override the UI driver here; null = the kit's UguiDriver.</summary>
        public IUiDriver? Ui;
        /// <summary>MonoBehaviour type names hidden during capture (e.g. an FPS overlay).</summary>
        public string[] OverlayTypeNames = Array.Empty<string>();

        /// <summary>
        /// TRUE ONLY FOR THE ADAPTER <see cref="DefaultAdapterBoot"/> REGISTERS — the generic,
        /// JSON-driven one every studio that merely installed the package has. Nothing this adapter
        /// knows about the game is compiled: it all comes out of files under <c>Library/Nova</c>,
        /// which is where a <c>sync-nova</c> writes. So "that name is the studio's own code, it is
        /// never gated" cannot be said of anything it carries (second audit, S1) — the lever gate
        /// reads this flag rather than guessing from the shape of the data.
        /// </summary>
        public bool IsDefaultAdapter;

        /// <summary>
        /// ScriptableObject type names holding this game's enumerable content (heroes, pets, skins).
        /// Declared per game because a project's SO types also include configs and view settings
        /// with no ad meaning. Empty means this game has no scannable content axis, which is a
        /// legitimate outcome, not a gap.
        /// </summary>
        public string[] ContentSources = System.Array.Empty<string>();

        /// <summary>
        /// Which capture path this game needs. Leave on Auto unless a vision-read of an actual CLIP
        /// shows content missing that the live screen clearly has — see RecorderDrivers.For for the
        /// case that made this seam necessary (Unity Recorder dropping world-space rendering while
        /// keeping canvas UI).
        /// </summary>
        public RecorderPreference Recorder = RecorderPreference.Auto;
        /// <summary>
        /// The game's shot list, as a PROVIDER rather than a list.
        ///
        /// Load-bearing for shots-as-data: a JSON-backed game returns a freshly-parsed list on every
        /// access, so editing shots.json takes effect on the next relay command with no domain
        /// reload. A plain field would hold one snapshot for the whole domain's lifetime — assigned
        /// once inside [InitializeOnLoad] — which is exactly the recompile-per-shot-edit loop this
        /// removes. A C#-backed game just returns its static array: `Shots = () => XShotList.V1`.
        /// </summary>
        public Func<IReadOnlyList<AdShot>> Shots = () => Array.Empty<AdShot>();
    }

    public static class AdapterRegistry
    {
        public static GameAdapter Current { get; set; } = GameAdapter.Null;
        public static bool IsRegistered => !ReferenceEquals(Current, GameAdapter.Null);
    }
}
