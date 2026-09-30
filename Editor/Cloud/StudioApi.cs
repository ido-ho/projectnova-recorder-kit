using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine.Networking;

namespace ProjectNova.RecorderKit
{
    /// <summary>One in-flight HTTP call, polled from the editor update loop (never blocked on).</summary>
    public interface IStudioResponse : IDisposable
    {
        bool IsDone { get; }
        /// <summary>HTTP status, or 0 when the request never reached the server.</summary>
        long Status { get; }
        string Body { get; }
        /// <summary>Non-null when the failure was the transport (DNS, refused, TLS), not the server.</summary>
        string? TransportError { get; }
    }

    /// <summary>The transport seam. The agent talks to this; the editor build wires
    /// <see cref="UnityStudioHttp"/>, tests wire a fake.</summary>
    public interface IStudioHttp
    {
        IStudioResponse Get(string url, string studioKey);
        IStudioResponse PostJson(string url, string studioKey, string json);
        IStudioResponse PostFile(string url, string studioKey, string filePath, string fileField,
            IReadOnlyDictionary<string, string> fields);

        /// <summary>Slice B/C commit 2 — a multipart POST whose file is OPTIONAL and is not a video.
        /// The self-test result is a small `facts` JSON part plus, when the frame was captured, one
        /// PNG. A self-test that could not take a frame still posts its facts: "no frame" is itself
        /// the most important fact such a run can report, and dropping the whole POST would leave
        /// the run reading `failed` with no reason attached.</summary>
        IStudioResponse PostMultipart(string url, string studioKey,
            IReadOnlyDictionary<string, string> fields, string? filePath, string fileField,
            string contentType);

        /// <summary>Slice D part 1 — PUT one file's bytes to a SIGNED upload URL, STREAMED from
        /// disk (an export part is up to 32 MiB and an export is many of them; the clip path's
        /// read-the-whole-file shape is the thing this replaces).
        ///
        /// THIS CALL CARRIES NO STUDIO KEY, BY DESIGN — note there is no <c>studioKey</c>
        /// parameter to pass. The signed URL is the credential, and under the bucket lane its host
        /// is the storage provider, not our API: a bearer key sent there is a key leaked to a third
        /// party. It sends exactly <paramref name="headers"/> and nothing else — no kit-version or
        /// workspace header either, since a presigned URL signs its headers and an unexpected one
        /// is, at best, noise on someone else's server.</summary>
        IStudioResponse PutFile(string url, string filePath, IReadOnlyDictionary<string, string> headers);
    }

    /// <summary>
    /// Kit 0.14.3 (deep review KIT-5, invariant 192) — WHERE THE STUDIO KEY MAY GO: an https URL, or plain http to this
    /// machine only (localhost, 127.0.0.1, [::1] — a developer's API). Every call that carries the key asks it first
    /// (<see cref="UnityStudioHttp"/>), so a Base URL typed as <c>http://…</c> sends nothing, rather than the key and every
    /// upload in clear. Pure — the tests hold it.
    /// </summary>
    public static class StudioUrl
    {
        public static string? KeyRefusal(string? url)
        {
            if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u))
                return $"the API URL '{url}' is not a full URL (https://…) — nothing was sent";
            if (u.Scheme == Uri.UriSchemeHttps) return null;
            if (u.Scheme == Uri.UriSchemeHttp && (u.IsLoopback || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
                return null;
            return $"the API URL must start with https:// (plain http is allowed only to this machine) — '{u.Scheme}://{u.Host}' " +
                   "would carry your studio key unencrypted, so nothing was sent. Fix the API URL under Details › Connection";
        }
    }

    /// <summary>UnityWebRequest-backed transport. Editor-safe: SendWebRequest is asynchronous and
    /// the caller polls IsDone from EditorApplication.update.</summary>
    public sealed class UnityStudioHttp : IStudioHttp
    {
        /// <summary>KIT-5: a call that would carry the key somewhere it must not go — answered at once, as a transport
        /// failure the agent already knows how to show and retry, and never sent.</summary>
        internal sealed class Refused : IStudioResponse
        {
            public Refused(string why) => TransportError = why;
            public bool IsDone => true;
            public long Status => 0;
            public string Body => "";
            public string? TransportError { get; }
            public void Dispose() { }
        }

        public IStudioResponse Get(string url, string studioKey)
        {
            if (StudioUrl.KeyRefusal(url) is { } no) return new Refused(no);
            var req = UnityWebRequest.Get(url);
            return Send(req, studioKey);
        }

        public IStudioResponse PostJson(string url, string studioKey, string json)
        {
            if (StudioUrl.KeyRefusal(url) is { } no) return new Refused(no);
            var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)) { contentType = "application/json" },
                downloadHandler = new DownloadHandlerBuffer(),
            };
            return Send(req, studioKey);
        }

        public IStudioResponse PostFile(string url, string studioKey, string filePath, string fileField,
            IReadOnlyDictionary<string, string> fields)
        {
            if (StudioUrl.KeyRefusal(url) is { } no) return new Refused(no);
            // Whole file in memory: a take is tens of MB, the API's cap is 512 MiB, and multipart
            // sections need the bytes. Streaming would need a hand-rolled multipart body.
            var bytes = File.ReadAllBytes(filePath);
            var form = new List<IMultipartFormSection>();
            foreach (var kv in fields)
                form.Add(new MultipartFormDataSection(kv.Key, kv.Value));
            form.Add(new MultipartFormFileSection(fileField, bytes, Path.GetFileName(filePath), "video/mp4"));
            var req = UnityWebRequest.Post(url, form);
            return Send(req, studioKey);
        }

        public IStudioResponse PostMultipart(string url, string studioKey,
            IReadOnlyDictionary<string, string> fields, string? filePath, string fileField,
            string contentType)
        {
            if (StudioUrl.KeyRefusal(url) is { } no) return new Refused(no);
            var form = new List<IMultipartFormSection>();
            foreach (var kv in fields)
                form.Add(new MultipartFormDataSection(kv.Key, kv.Value));
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                var bytes = File.ReadAllBytes(filePath);
                form.Add(new MultipartFormFileSection(fileField, bytes, Path.GetFileName(filePath), contentType));
            }
            var req = UnityWebRequest.Post(url, form);
            return Send(req, studioKey);
        }

        public IStudioResponse PutFile(string url, string filePath, IReadOnlyDictionary<string, string> headers)
        {
            var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT)
            {
                // UploadHandlerFile streams from disk; nothing here holds the part in memory.
                uploadHandler = new UploadHandlerFile(filePath),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            foreach (var kv in headers)
            {
                // Content-Type belongs to the upload handler; set as a plain header UnityWebRequest
                // can send it twice, and a presigned URL is unforgiving about what it receives.
                if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    req.uploadHandler.contentType = kv.Value;
                else
                    req.SetRequestHeader(kv.Key, kv.Value);
            }
            req.timeout = 900; // 32 MiB on a slow studio line; the ticket itself lives 15 minutes
            req.SendWebRequest();
            return new Response(req);
        }

        private static IStudioResponse Send(UnityWebRequest req, string studioKey)
        {
            ApplyStudioHeaders(req, studioKey, NovaCaptureAgent.BoundWorkspaceId);
            // KIT-5: a redirect is never followed with the key on it — the API answers its own routes, and a 3xx is an error
            req.redirectLimit = 0;
            req.timeout = 600; // a clip upload on a slow studio line
            req.SendWebRequest();
            return new Response(req);
        }

        /// <summary>
        /// The headers EVERY studio call carries — one function, so a route added later (slice
        /// E.3's screen check is one) cannot send a different set. Pulled out of <c>Send</c> so the
        /// kit's tests can read them off a real, never-sent <see cref="UnityWebRequest"/>.
        /// </summary>
        internal static void ApplyStudioHeaders(UnityWebRequest req, string studioKey, string? workspace)
        {
            req.SetRequestHeader("Authorization", "Bearer " + studioKey);
            req.SetRequestHeader("Accept", "application/json");
            // Slice B/C commit 2: the API's version-skew gate decides which job KINDS this editor
            // may be handed from this header. It rides on EVERY call, not just the poll, because
            // the claim enforces the same rule — a filtered list is not a gate.
            req.SetRequestHeader(KitVersion.Header, KitVersion.Current);
            // Slice D part 1 (D10): which workspace THIS Unity project is bound to. On every call
            // for the same reason the version is — the poll filters on it AND the claim refuses on
            // it. Sent only when the studio has picked one; an unbound project sends nothing and
            // the API answers it exactly as it answered every kit before this header existed.
            if (!string.IsNullOrEmpty(workspace))
                req.SetRequestHeader(KitVersion.WorkspaceHeader, workspace);
        }

        private sealed class Response : IStudioResponse
        {
            private readonly UnityWebRequest _req;
            public Response(UnityWebRequest req) => _req = req;
            public bool IsDone => _req.isDone;
            public long Status => _req.responseCode;
            public string Body => _req.downloadHandler?.text ?? "";
            public string? TransportError =>
                _req.result == UnityWebRequest.Result.ConnectionError ||
                _req.result == UnityWebRequest.Result.DataProcessingError
                    ? _req.error
                    : null;
            public void Dispose() => _req.Dispose();
        }
    }
}
