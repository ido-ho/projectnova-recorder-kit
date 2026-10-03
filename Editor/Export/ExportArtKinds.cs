namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Video-first plan step 2 — WHICH KINDS OF ART THIS KIT SENDS, written into `header.json` as
    /// `artKinds`. A fact about the kit, not the game: the server reads "no Spine file in the export"
    /// as "this kit does not send Spine files" when `spine` is absent here, and NEVER as "this game
    /// has no rigs" (invariant 50: a null result is not an absence until the instrument says it
    /// looked). An older kit writes no `artKinds` at all, and is read the same way.
    /// </summary>
    public static class ExportArtKinds
    {
        public const string Icon = "icon";
        public const string Font = "font";
        public const string Audio = "audio";
        /// <summary>Spine skeleton data (`.json` / `.skel` / `.skel.bytes`), its `.atlas` text and
        /// the atlas page pictures — only for the rigs the game uses.</summary>
        public const string Spine = "spine";
        /// <summary>The prefabs the game uses that are mostly particles (their YAML, with the counts that decided
        /// it on their art index row), bounded; their pictures go with the textures. Planned LAST, after the
        /// textures, so prefabs never take the total cap from pictures.</summary>
        public const string Vfx = "vfx";
        public const string Texture = "texture";

        /// <summary>Every kind the art plan of THIS kit considers, in the order it plans them. The collector copies
        /// this into the header it builds (<see cref="ExportHeader.ArtKinds"/>); a header read back keeps what its
        /// build wrote.</summary>
        public static readonly string[] Sent = { Icon, Font, Audio, Spine, Texture, Vfx };

        public const string ReasonOverVfxCount = "over vfxMaxFiles";
        public const string ReasonOverVfxFile = "file over vfxFileMaxBytes";
    }

    /// <summary>What a prefab's own YAML holds, counted by <see cref="ExportCollector.CountPrefabDocs"/>.</summary>
    public struct PrefabCounts
    {
        public int ParticleSystems;
        public int GameObjects;
        public int PrefabInstances;
        public int RectTransforms;
    }
}
