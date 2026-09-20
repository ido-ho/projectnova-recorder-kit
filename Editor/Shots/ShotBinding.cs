using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Binds `{name}` placeholders in a shot's command strings to values supplied per run.
    ///
    /// Every failure is a HARD error rather than a best-effort substitution, because the one
    /// unacceptable outcome for a parameterized shot is silently filming the wrong content while
    /// every automated signal stays green.
    ///
    /// Pure and static so it unit-tests without Unity, a game, or a director.
    /// </summary>
    public static class ShotBinding
    {
        /// <summary>Parameter NAMES: lowerCamelCase. Also defines what counts as a placeholder.</summary>
        private static readonly Regex NamePattern = new(@"^[a-z][A-Za-z0-9]*$", RegexOptions.Compiled);

        /// <summary>
        /// Parameter VALUES. ReflectionCheatBridge.Run splits commands on spaces, so forbidding
        /// whitespace stops a value injecting a second command, and forbidding '@' stops it forging
        /// a label selector ("Button@Label").
        /// </summary>
        private static readonly Regex ValuePattern = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

        /// <summary>A `{name}` whose contents are a VALID parameter name. `{"k":1}` is not one.</summary>
        private static readonly Regex PlaceholderPattern =
            new(@"\{([a-z][A-Za-z0-9]*)\}", RegexOptions.Compiled);

        public static bool ValidateName(string name) => name != null && NamePattern.IsMatch(name);
        public static bool ValidateValue(string value) => value != null && ValuePattern.IsMatch(value);

        /// <summary>True when the text contains at least one placeholder. Drives the zero-param lint.</summary>
        public static bool HasPlaceholder(string text) =>
            !string.IsNullOrEmpty(text) && PlaceholderPattern.IsMatch(text);

        public static bool TryApply(string text, IReadOnlyList<string> declared,
            IReadOnlyDictionary<string, string> bindings, out string result, out string error)
        {
            result = text ?? "";
            error = "";
            if (string.IsNullOrEmpty(text)) return true;

            // A binding the shot never declared is a script typo that must surface even when
            // `validate` was skipped.
            foreach (var key in bindings.Keys)
            {
                if (!declared.Contains(key))
                {
                    error = $"binding '{key}' is not declared by this shot " +
                            $"(declared: {(declared.Count == 0 ? "none" : string.Join(", ", declared))})";
                    return false;
                }
            }

            var failure = "";
            result = PlaceholderPattern.Replace(text, m =>
            {
                var name = m.Groups[1].Value;
                if (!declared.Contains(name))
                {
                    if (failure.Length == 0)
                        failure = $"placeholder '{{{name}}}' is not declared by this shot " +
                                  $"(declared: {(declared.Count == 0 ? "none" : string.Join(", ", declared))})";
                    return m.Value;
                }
                if (!bindings.TryGetValue(name, out var value))
                {
                    if (failure.Length == 0)
                        failure = $"placeholder '{{{name}}}' has no value bound for this run";
                    return m.Value;
                }
                if (!ValidateValue(value))
                {
                    if (failure.Length == 0)
                        failure = $"value '{value}' for '{name}' is not allowed " +
                                  "(letters, digits, dot, underscore, hyphen only — no spaces or '@')";
                    return m.Value;
                }
                return value;
            });

            if (failure.Length > 0)
            {
                error = failure;
                result = text ?? "";
                return false;
            }
            return true;
        }
    }
}
