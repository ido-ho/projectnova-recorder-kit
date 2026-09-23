namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Slice B/C commit 2 — what this kit tells the API it is.
    ///
    /// The API's version-skew gate decides which job KINDS this editor may be handed from this one
    /// string. It exists because the kit-side "refuse a kind I do not handle" check (commit 1) only
    /// protects kits that HAVE it: a studio still on v0.4.4 parses an unknown kind as a 0-item
    /// capture and reports <c>done</c> — work never seen, reported complete — and no change here
    /// can ever reach a kit already installed on someone's machine.
    ///
    /// WHY A CONST AND NOT <c>PackageInfo.FindForAssembly</c>: that returns null when the kit is
    /// EMBEDDED rather than installed as a package (the monorepo's own dev project, and any studio
    /// who vendored it), and a null here reads to the gate as "cannot name a version" — which would
    /// silently withhold every new kind from exactly the setup used to develop them. A const is
    /// always readable. The drift risk it introduces (const says one thing, package.json another) is
    /// closed by <c>apps/api/src/studio/kit-version-const.spec.ts</c>, which reads BOTH files and
    /// fails the build when they disagree — the same lock that ties the Setup page's paste line to
    /// package.json.
    ///
    /// KEEP THIS EQUAL TO <c>package.json</c>'s <c>version</c>, in the same commit, every time.
    /// </summary>
    public static class KitVersion
    {
        /// <summary>This kit's package version, e.g. <c>0.8.0</c>. Must match package.json exactly.</summary>
        public const string Current = "0.9.3";

        /// <summary>The header every studio call carries it in. Lowercase on purpose — Node
        /// lowercases incoming header keys, and the API reads the lowercase form.</summary>
        public const string Header = "x-nova-kit-version";

        /// <summary>Slice D part 1 (D10) — the header that carries the workspace THIS Unity project
        /// is bound to. Lowercase for the same reason. Mirror of <c>KIT_WORKSPACE_HEADER</c> in
        /// <c>apps/api/src/studio/studio-capture-job.ts</c>.</summary>
        public const string WorkspaceHeader = "x-nova-workspace";
    }
}
