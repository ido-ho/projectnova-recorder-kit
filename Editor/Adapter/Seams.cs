using System.Collections;

namespace ProjectNova.RecorderKit
{
    /// <summary>Read-only view of live UI state. Abstracted so WaitCondition is unit-testable.</summary>
    public interface IUiProbe
    {
        bool Exists(string name);
        string? GetText(string name);

        /// <summary>
        /// Present AND actually actionable by the player (a uGUI Selectable with interactable=true).
        ///
        /// This is the signal to prefer for "the game is ready for input", and it is distinct from
        /// Exists in the way that matters: a game's primary button typically stays in the hierarchy
        /// through animations, encounters and popups while being disabled, so Exists is true for the
        /// whole period the player CANNOT act. Live-found on a board game whose roll button read
        /// present-but-locked during moves and encounters, and whose state machine could not be used
        /// instead because it did not always return to its idle value after a battle.
        /// </summary>
        bool IsInteractable(string name);
    }

    /// <summary>Full UI driving surface. The kit ships a uGUI implementation (UguiDriver);
    /// non-uGUI games supply their own via GameAdapter.Ui (future work — the seam exists now).</summary>
    public interface IUiDriver : IUiProbe
    {
        bool Click(string name, int index = 0);
        void PointerDown(string name, int index = 0);
        void PointerUp(string name, int index = 0);
    }

    /// <summary>Read the game's current logical state (e.g. a state-machine name). Per-game.</summary>
    public interface IStateProbe
    {
        string? CurrentStateName { get; }
    }

    /// <summary>Perform a named state change (cheat). Per-game; reflection-based bridges are a
    /// first-class supported mode. Returns false on failure — the director logs, never throws.</summary>
    public interface ICheatBridge
    {
        bool Run(string command);
    }

    /// <summary>Bring the game to a capturable baseline before the first shot (e.g. SNL: wait for
    /// the board, max the bet). Iterator is pumped by the director; set ctx.LastOpSucceeded.</summary>
    public interface IReadyGate
    {
        IEnumerable WaitUntilReady(DirectorContext ctx);
    }

    /// <summary>Return to a known screen between shots (e.g. SNL's dismiss table). Iterator is
    /// pumped by the director; set ctx.LastOpSucceeded.</summary>
    public interface IRecoveryPolicy
    {
        IEnumerable Recover(DirectorContext ctx);
    }
}
