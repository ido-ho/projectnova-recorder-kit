using System.Collections.Generic;
using UnityEngine;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Fixture for <see cref="ExportCollectorTests"/> — a ScriptableObject with serialized Colors
    /// and a Font reference, so an export run has a real `colorObjects` row and a real font
    /// `refCount` to be right or wrong about.
    ///
    /// It also STANDS IN FOR THE STUDIO'S OWN CODE (<see cref="ExportLiveProjectTests"/>): the
    /// message counters below are how the suite proves the export never executes a line of it.
    /// `AssetDatabase.LoadMainAssetAtPath` on a ScriptableObject runs `Awake`, `OnEnable` and
    /// `OnValidate`, and the memory sweep afterwards runs `OnDisable` — measured 2026-09-21. An
    /// export is a read, so all four must stay at zero.
    ///
    /// The file name matches the class name on purpose: Unity binds a MonoScript by file name, and
    /// an asset created from a type it cannot bind loads back as a broken script.
    /// </summary>
    public class ExportTestColorAsset : ScriptableObject
    {
        public Color primary = Color.red;
        public Color secondary = new Color(0.1f, 0.2f, 0.3f, 1f);

        /// <summary>
        /// THE SHAPE THE COLOUR READER SILENTLY DROPPED (audit S3, 2026-09-21). Unity serialises a
        /// `Color[]` / `List&lt;Color&gt;` as BARE flow mappings under a list marker —
        /// `swatches:\n  - {r: 1, g: 0, b: 0, a: 1}` — with no key of their own, and the reader
        /// landed on the key `{r` and gave up without an `errors` row. A palette asset is the single
        /// most likely place a studio keeps its brand colours, so this is the shape that mattered
        /// most.
        /// </summary>
        public Color[] swatches = { new Color(1f, 0f, 0f, 1f), new Color(0f, 0f, 1f, 1f) };

        public List<Color> palette = new List<Color> { new Color(0f, 1f, 0f, 1f) };

        public Font? font;

        public static int Awakes;
        public static int Enables;
        public static int Validates;
        public static int Disables;

        public static void ResetCounters()
        {
            Awakes = 0;
            Enables = 0;
            Validates = 0;
            Disables = 0;
        }

        private void Awake() => Awakes++;
        private void OnEnable() => Enables++;
        private void OnValidate() => Validates++;
        private void OnDisable() => Disables++;
    }
}
