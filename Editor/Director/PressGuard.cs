using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 §4 (spike A part 2) — WHAT A GATED RUN MAY PRESS. Asked by the director before
    /// every <c>click</c> and <c>hold</c> step of a run whose lever gate is live (cloud content, or a
    /// delivered project), so the answer lives in the kit, at the press, where no sender can skip it.
    ///
    /// Two fences, in this order:
    /// <list type="number">
    /// <item><b>The allowlist</b> — when the run carries one (<see cref="AdDirector.Options.PressAllowlist"/>),
    /// only a target whose NAME (the part before <c>@</c>) is on it is pressed. This is the fence: auto-try
    /// presses the names its need matched, and nothing else.</item>
    /// <item><b>The risky-word list</b> — always, allowlist or not: a target whose name or label has a word
    /// that means money, deleting, resetting or the account is refused. The second fence, because a name
    /// cannot prove what a button does — runtime listeners are not readable
    /// (<c>ReflectionCheatBridge</c> ui-dump notes) — but it can prove what a button SAYS.</item>
    /// </list>
    ///
    /// Words are split the way the server's <c>soundsDestructive</c> splits them
    /// (<c>apps/renderer/src/game-ads/learn/lane-digest.ts</c>): at non-letters and at camelCase humps, and
    /// matched by ROOT at the start of a word — so <c>DeleteAccount</c>, "Buy All Packs!" and <c>RestorePurchases</c>
    /// are refused and <c>Preset</c>, <c>Display</c> are not. The destructive roots are that function's, and the
    /// money/account roots are added here. ONE list: <c>Editor/Tests/Fixtures/press-risk.cases.json</c> holds these roots
    /// and verdicts, and every copy — this one (enforced), the renderer's tick-list risk and DESTRUCTIVE? mark (a reader's
    /// flag), the API's near-name safety (never offered as a one-press fix) — is held equal to it by its own suite.
    ///
    /// SCOPE, said plainly: the guard fences the director's <c>click</c> and <c>hold</c> STEPS. The cheat bridge's own
    /// press verbs (<c>click</c>, <c>invoke-button</c>, <c>tap-at</c>, <c>press-at</c>, <c>tap-through</c>, <c>drag</c>, <c>raw</c>)
    /// run as <c>cheat</c> steps: they are tick-gated as levers, per exact command, and do NOT pass this guard. Auto-try
    /// and replay must therefore use <c>click name@label</c> steps, not those verbs (plan §4).
    /// </summary>
    public static class PressGuard
    {
        /// <summary>The destructive roots — press-risk.cases.json's <c>destructiveRoots</c> (lane-digest.ts reads the same).</summary>
        public static readonly IReadOnlyList<string> DestructiveRoots = new[]
        {
            "reset", "wipe", "delet", "eras", "purg", "clear", "destroy", "remov", "unlink",
        };

        /// <summary>Money, spending and the account: a press that could charge, spend or sign someone out.</summary>
        public static readonly IReadOnlyList<string> MoneyAndAccountRoots = new[]
        {
            "buy", "purchas", "pay", "spend", "iap", "subscri", "redeem", "restor", "checkout",
            "logout", "signout", "account",
        };

        private static readonly Regex Hump = new Regex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant);
        private static readonly Regex NonLetters = new Regex("[^A-Za-z]+", RegexOptions.CultureInvariant);

        /// <summary>The words of a target (<c>Name</c>, <c>Name@Label</c> or a free label), lower case.</summary>
        public static IEnumerable<string> Words(string text) =>
            NonLetters.Split(Hump.Replace(text ?? "", "$1 $2"))
                .Where(w => w.Length > 0)
                .Select(w => w.ToLowerInvariant());

        /// <summary>The risky root a target's words start with, or null when none does.</summary>
        public static string? RiskyRoot(string target)
        {
            // "log out" / "sign out" are one root spelt as two words. Split the camelCase humps FIRST, then join the pair
            // across a space, underscore or hyphen — so LogOutButton, Sign_out, Sign-out and "Log out" all read as one word
            // (spike A review, finding 1: joining before the hump split let every camelCase form through). Letters-only
            // boundaries, not \b: \b treats '_' as a word character, so \b missed Btn_Sign_out.
            var humped = Hump.Replace(target ?? "", "$1 $2");
            var joined = Regex.Replace(humped, @"(?i)(?<![A-Za-z])(log|sign)[\s_\-]+out(?![A-Za-z])", "$1out");
            foreach (var w in Words(joined))
                foreach (var root in DestructiveRoots.Concat(MoneyAndAccountRoots))
                    if (w.StartsWith(root, StringComparison.Ordinal))
                        return root;
            return null;
        }

        /// <summary>The name part of a selector: <c>Buy@Gems</c> → <c>Buy</c>.</summary>
        public static string NameOf(string target)
        {
            // Trimmed like UguiDriver.ParseSelector trims the name, so the allowlist and the driver agree on what the name is.
            var at = (target ?? "").IndexOf('@');
            return (at < 0 ? target ?? "" : target!.Substring(0, at)).Trim();
        }

        /// <summary>
        /// Why this press is refused, or null when it may run. <paramref name="allowlist"/> null = no
        /// allowlist for this run (the risky-word fence still applies).
        /// </summary>
        public static string? Refusal(string target, IReadOnlyCollection<string>? allowlist)
        {
            var name = NameOf(target);
            if (allowlist != null && !allowlist.Contains(name))
                return $"PRESS REFUSED: '{target}' is not on this run's allowed names ({allowlist.Count} allowed)";
            var root = RiskyRoot(target);
            if (root != null)
                return $"PRESS REFUSED: '{target}' has the risky word root '{root}' (money, deleting, resetting or the account) — a gated run never presses it";
            return null;
        }
    }
}
