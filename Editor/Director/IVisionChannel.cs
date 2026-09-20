using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Async vision check: "does the screen match this prompt?" answered by a reasoning agent
    /// over the relay — deliberately NOT a WaitCondition (those run synchronously every tick;
    /// an LLM round-trip cannot live there). Runs that use it are semi-interactive.
    /// </summary>
    public interface IVisionChannel
    {
        /// <summary>Capture the screen, post a request; returns the request id (null on failure).</summary>
        string? Request(string prompt);
        /// <summary>True once an answer exists; results are cached per id.</summary>
        bool TryGetResult(string id, out bool match);
    }

    /// <summary>File transport: request = screenshot + json under AdRelay/vision/requests/,
    /// answer expected at AdRelay/vision/responses/&lt;id&gt;.json as {"id":…,"match":bool}.</summary>
    public sealed class FileVisionChannel : IVisionChannel
    {
        private readonly string _projectRoot;
        private readonly Dictionary<string, bool> _cache = new();

        public FileVisionChannel(string projectRoot)
        {
            _projectRoot = projectRoot;
        }

        public string? Request(string prompt)
        {
            var id = Guid.NewGuid().ToString("N").Substring(0, 12);
            var dir = RelayPaths.VisionRequests(_projectRoot);
            Directory.CreateDirectory(dir);
            var screenshot = Path.Combine(dir, id + ".png");
            ScreenCapture.CaptureScreenshot(screenshot);
            AtomicFile.Write(Path.Combine(dir, id + ".json"), new JObject
            {
                ["id"] = id,
                ["prompt"] = prompt,
                ["screenshot"] = screenshot,
            }.ToString());
            return id;
        }

        public bool TryGetResult(string id, out bool match)
        {
            if (_cache.TryGetValue(id, out match))
                return true;
            var path = Path.Combine(RelayPaths.VisionResponses(_projectRoot), id + ".json");
            if (!File.Exists(path))
                return false;
            try
            {
                match = JObject.Parse(File.ReadAllText(path))["match"]?.Value<bool>() ?? false;
                _cache[id] = match;
                return true;
            }
            catch
            {
                return false; // mid-write; retry next tick
            }
        }
    }
}
