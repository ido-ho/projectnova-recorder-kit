namespace ProjectNova.RecorderKit
{
    /// <summary>Which input world a surface lives in. See the four-tier table in
    /// docs/superpowers/specs/2026-08-06-recorder-kit-input-reach-design.md §2.</summary>
    public enum InputTierKind { Ugui, Backend, Seam, Unreachable }

    /// <summary>
    /// Pure: facts in, tier out, no Unity API calls, so the RULE is arguable without an editor.
    ///
    /// The rule that is easy to get backwards: a surface is `ugui` only when something up the parent
    /// chain of the top raycast hit actually implements IPointerClickHandler. A hit alone means
    /// nothing — gem-match3's board sits under a full-screen fade that is hit every time and handles
    /// nothing, which is exactly how a tap there reported success while doing nothing.
    /// </summary>
    public static class InputTier
    {
        public static InputTierKind Classify(
            bool hasEventSystem, bool hitSomething, bool hasClickHandler,
            bool hasPhysicsRaycaster, bool inputSystemPresent)
        {
            // No EventSystem is a global failure, not a property of this point.
            if (!hasEventSystem) return InputTierKind.Unreachable;
            if (hitSomething && hasClickHandler) return InputTierKind.Ugui;
            return inputSystemPresent ? InputTierKind.Backend : InputTierKind.Unreachable;
        }

        public static string Name(InputTierKind k) => k switch
        {
            InputTierKind.Ugui => "ugui",
            InputTierKind.Backend => "backend",
            InputTierKind.Seam => "seam",
            _ => "unreachable",
        };
    }
}
