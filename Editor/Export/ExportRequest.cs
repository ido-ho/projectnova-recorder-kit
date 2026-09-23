namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// What to export and where to put it. Built by the agent loop from the run it leased.
    /// </summary>
    public sealed class ExportRequest
    {
        /// <summary>Absolute. Used ONLY to find `docs/`, `Docs/`, `Documentation/`, `README*` and
        /// `remote_config*.json` (spec §6). Asset paths resolve against the Unity project folder
        /// itself, which the collector reads from `Application.dataPath` — so a test can point this
        /// at a temp tree without breaking every asset's byte count.
        /// `Directory.GetCurrentDirectory()` in production.</summary>
        public string ProjectRoot = "";

        /// <summary>Absolute; e.g. `&lt;ProjectRoot&gt;/Library/AdRelay/export/&lt;runId&gt;`. The parts and
        /// `header.json` land here. The caller deletes it once the run's `done` is accepted.</summary>
        public string OutDir = "";

        /// <summary>The folder the inventory, dependency, identity and art passes cover. `Assets`
        /// in production; a test scopes it to e.g. `Assets/__NovaExportTest`.</summary>
        public string AssetsRoot = "Assets";

        /// <summary>Where the `.cs` pattern scan runs. Null = <see cref="AssetsRoot"/>. A test
        /// points it at a temp dir OUTSIDE `Assets`, because creating a `.cs` under `Assets`
        /// triggers a recompile and a domain reload in the middle of the run.</summary>
        public string? PatternScanRoot;

        /// <summary>This kit window's OWN per-project binding — NOT an echo of the job. The API
        /// compares it against the RUN's workspace, so the check compares two independent values
        /// (invariant 100).</summary>
        public string WorkspaceId = "";

        /// <summary>`Library/Nova/adapter.json` gameId, or null before onboarding.</summary>
        public string? EditorGameId;

        public bool AdapterJsonPresent;

        public string KitVersion = "";

        public ExportCaps Caps = new ExportCaps();

        /// <summary>Yield after roughly this much work, so an `EditorApplication.update` pump never
        /// freezes the editor. 0 yields after every unit.</summary>
        public int SliceBudgetMs = 12;
    }

    /// <summary>The collector's live state. The caller reads it between pumps to draw a progress
    /// bar, and at <see cref="Done"/> to find out what happened.</summary>
    public sealed class ExportResult
    {
        /// <summary>True when the run is over — successfully OR fatally.</summary>
        public bool Done;

        /// <summary>Set only for a "cannot write to OutDir"-class failure. A per-asset failure is
        /// NEVER fatal: it lands in `header.errors` and the export ships.</summary>
        public string? FatalError;

        public string Phase = "";

        public float Progress01;

        /// <summary>Spec §8.9 — parts written so far (the open one included) and their bytes, for
        /// the progress the kit posts while it builds.</summary>
        public int PartsBuilt;
        public long BytesBuilt;

        /// <summary>
        /// How many times the run asked Unity to drop unreferenced assets. NOT on the wire and not
        /// a progress figure — it is how a test asserts that a phase which LOADS engine assets
        /// (textures, audio clips) also bounds the memory it costs. Before 2026-09-21 the identity
        /// phase loaded every AudioClip in the project and swept none of them, and there was
        /// nothing an EditMode test could look at to say so.
        /// </summary>
        public int Sweeps;

        /// <summary>Set on success. `header.json` with the same content is also on disk in OutDir,
        /// so an upload can resume after a domain reload.</summary>
        public ExportHeader? Header;
    }
}
