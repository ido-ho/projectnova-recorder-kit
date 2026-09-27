namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// The kit's built-in cheat bridge: every discovery verb, no game-named cheats.
    /// Lets ping / screenshot / ui-dump / call work the moment the package is added,
    /// with nothing committed under the studio's Assets/.
    /// </summary>
    public sealed class GenericCheatBridge : ReflectionCheatBridge
    {
        public GenericCheatBridge(string projectRoot, IUiDriver ui) : base(projectRoot, ui) { }

        protected override bool RunGameCheat(string verb, string[] args) => false;
    }
}
