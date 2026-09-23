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
        /// <summary>Capture the screen, post a request; returns the request id (null on failure).
        /// <paramref name="stepIndex"/> is the 0-based index of this <c>vision</c> step in the
        /// shot's own <c>steps</c> array (slice E.3): the site answers a try's screen check from
        /// the shot it SENT, so the HTTP channel names the step, not the prompt. The file channel
        /// ignores it.</summary>
        string? Request(string prompt, int stepIndex);
        /// <summary>True once an answer exists; results are cached per id.</summary>
        bool TryGetResult(string id, out bool match);
    }

    /// <summary>
    /// Slice E.3 — A QUESTION CAN END WITHOUT A VERDICT. <see cref="IVisionChannel.TryGetResult"/>
    /// can only say "matched" or "did not match", so a channel that can be REFUSED (the site's 4xx),
    /// fail to reach anyone, or fail to take a frame had no way to say so except by never answering
    /// — which the director reported, at the step's timeout, as a failed match. A 0.8.0 kit must not
    /// report a verdict nobody made (the owner's gate). A channel that also implements this is asked
    /// through it instead: <see cref="VisionPoll.Failure"/> is the reason, by name, and the director
    /// fails the step with it at once. <see cref="FileVisionChannel"/> implements it too (the first
    /// audit of E.3, K-S1): a response file with no true/false <c>match</c> is a failure with a reason.
    /// </summary>
    public interface IVisionOutcome
    {
        /// <summary>Where the question <paramref name="id"/> stands now. Called once per director
        /// tick while the step waits; a channel may do its work (poll a request, retry) here.</summary>
        VisionPoll Poll(string id);
    }

    /// <summary>One reading of a vision question: still pending, ANSWERED (a real verdict), or
    /// FAILED with a reason (no verdict was made).</summary>
    public readonly struct VisionPoll
    {
        /// <summary>The verdict — non-null only when someone answered the question.</summary>
        public bool? Match { get; }
        /// <summary>Why the question ended without a verdict — non-null only then.</summary>
        public string? Failure { get; }

        private VisionPoll(bool? match, string? failure)
        {
            Match = match;
            Failure = failure;
        }

        public static VisionPoll Pending => default;
        public static VisionPoll Answered(bool match) => new(match, null);
        public static VisionPoll Failed(string reason) =>
            new(null, string.IsNullOrWhiteSpace(reason) ? "the screen check failed (no reason given)" : reason);

        public bool IsPending => Match == null && Failure == null;
    }

    /// <summary>File transport: request = screenshot + json under AdRelay/vision/requests/,
    /// answer expected at AdRelay/vision/responses/&lt;id&gt;.json as {"id":…,"match":bool}. Only a
    /// JSON <c>true</c> or <c>false</c> is an answer (the first audit of E.3, K-S1): a response whose
    /// <c>match</c> is missing or not a boolean ends the question with
    /// <see cref="NoTrueFalseMatch"/> — never as "did not match".</summary>
    public sealed class FileVisionChannel : IVisionChannel, IVisionOutcome
    {
        /// <summary>Why a response file with no JSON true/false <c>match</c> is not an answer.</summary>
        public const string NoTrueFalseMatch = "the response file has no true/false match";

        private readonly string _projectRoot;
        private readonly Dictionary<string, VisionPoll> _ended = new();

        public FileVisionChannel(string projectRoot)
        {
            _projectRoot = projectRoot;
        }

        public string? Request(string prompt, int stepIndex)
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

        /// <summary>True only once the response file ANSWERED true or false.</summary>
        public bool TryGetResult(string id, out bool match)
        {
            var poll = Poll(id);
            match = poll.Match == true;
            return poll.Match != null;
        }

        public VisionPoll Poll(string id)
        {
            if (_ended.TryGetValue(id, out var ended))
                return ended;
            var path = Path.Combine(RelayPaths.VisionResponses(_projectRoot), id + ".json");
            if (!File.Exists(path))
                return VisionPoll.Pending;
            JToken? match;
            try
            {
                match = JObject.Parse(File.ReadAllText(path))["match"];
            }
            catch
            {
                return VisionPoll.Pending; // mid-write; retry next tick
            }
            var outcome = match != null && match.Type == JTokenType.Boolean
                ? VisionPoll.Answered(match.Value<bool>())
                : VisionPoll.Failed(NoTrueFalseMatch);
            _ended[id] = outcome;
            return outcome;
        }
    }
}
