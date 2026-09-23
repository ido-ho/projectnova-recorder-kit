using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// <c>adapter.json</c> <c>camera</c> block. Missing or a non-object key → the parent
    /// <see cref="AdapterJson.Camera"/> is null (never throws). <c>set*</c> default to
    /// SetPosition / SetRotation / SetFov when omitted; <c>viewType</c> / <c>projection</c>
    /// stay null so pose can refuse rather than guess.
    /// </summary>
    public readonly struct CameraAdapter
    {
        public string? ViewType { get; }
        public string? Projection { get; }
        public string SetPosition { get; }
        public string SetRotation { get; }
        public string SetFov { get; }

        public CameraAdapter(
            string? viewType, string? projection,
            string? setPosition, string? setRotation, string? setFov)
        {
            ViewType = viewType;
            Projection = projection;
            SetPosition = string.IsNullOrWhiteSpace(setPosition) ? "SetPosition" : setPosition!;
            SetRotation = string.IsNullOrWhiteSpace(setRotation) ? "SetRotation" : setRotation!;
            SetFov = string.IsNullOrWhiteSpace(setFov) ? "SetFov" : setFov!;
        }
    }

    /// <summary>
    /// Parse-only view of <c>Library/Nova/adapter.json</c>. Never throws — missing or
    /// malformed file yields a null <see cref="GameId"/> and an empty overlay list
    /// (same contract as shots.json). A missing/malformed <c>camera</c> block yields
    /// a null <see cref="Camera"/> rather than a guessed <c>Camera</c> type (UiVfxCamera
    /// trap).
    /// </summary>
    public readonly struct AdapterJson
    {
        public string? GameId { get; }
        public string[] OverlayTypeNames { get; }
        public CameraAdapter? Camera { get; }

        public AdapterJson(string? gameId, string[] overlayTypeNames, CameraAdapter? camera = null)
        {
            GameId = gameId;
            OverlayTypeNames = overlayTypeNames ?? Array.Empty<string>();
            Camera = camera;
        }

        public static AdapterJson Load(string projectRoot)
        {
            try
            {
                var path = Path.Combine(RelayPaths.NovaDir(projectRoot), "adapter.json");
                if (!File.Exists(path))
                    return Empty();
                return Parse(File.ReadAllText(path));
            }
            catch
            {
                return Empty();
            }
        }

        /// <summary>
        /// JSON names win when the array is non-empty; otherwise the C# adapter field
        /// (custom adapters with no JSON key keep using the field).
        /// </summary>
        public static string[] OverlayNamesFor(string[]? jsonNames, string[]? adapterNames)
        {
            if (jsonNames != null && jsonNames.Length > 0)
                return jsonNames;
            return adapterNames ?? Array.Empty<string>();
        }

        /// <summary>Slice A2′ — the same reader, over text that is not on disk yet (the adapter.json
        /// a `sync-nova` is about to write). Same contract: never throws, and anything it cannot
        /// use reads as "no adapter".</summary>
        public static AdapterJson ParseText(string? json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json) ? Empty() : Parse(json!);
            }
            catch
            {
                return Empty();
            }
        }

        private static AdapterJson Empty() => new(null, Array.Empty<string>(), null);

        private static AdapterJson Parse(string json) => From(NovaJson.ParseObject(json));

        /// <summary>The same reader, over a document already parsed — or none, which reads as "no adapter" (tenth audit,
        /// S1: <c>SyncNova</c> parses a delivered adapter.json once, refusing what it cannot parse, and every check it
        /// makes reads that parse).</summary>
        internal static AdapterJson From(JObject? obj)
        {
            if (obj == null) return Empty();
            // A STRING, or no game id at all. `(string?)token` quietly renders a number or a bool
            // as text, so `"gameId": 7` read as the game "7" — and sync-nova now refuses a
            // delivered adapter.json that names no game, which it could not do if a 7 counted.
            var gameId = Str(obj["gameId"]);

            var names = Array.Empty<string>();
            if (obj["overlayTypeNames"] is JArray arr)
            {
                var list = new List<string>();
                foreach (var token in arr)
                {
                    if (token.Type != JTokenType.String)
                        continue;
                    var s = token.Value<string>();
                    if (s is not { Length: > 0 } || string.IsNullOrWhiteSpace(s))
                        continue;
                    list.Add(s);
                }
                names = list.ToArray();
            }

            CameraAdapter? camera = null;
            if (obj["camera"] is JObject cam)
            {
                camera = new CameraAdapter(
                    Str(cam["viewType"]),
                    Str(cam["projection"]),
                    Str(cam["setPosition"]),
                    Str(cam["setRotation"]),
                    Str(cam["setFov"]));
            }

            return new AdapterJson(gameId, names, camera);
        }

        private static string? Str(JToken? token)
        {
            if (token == null || token.Type != JTokenType.String)
                return null;
            var s = token.Value<string>();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
    }
}
