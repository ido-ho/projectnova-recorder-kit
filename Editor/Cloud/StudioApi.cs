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

    /// <summary>UnityWebRequest-backed transport. Editor-safe: SendWebRequest is asynchronous and
    /// the caller polls IsDone from EditorApplication.update.</summary>
    public sealed class UnityStudioHttp : IStudioHttp
    {
        public IStudioResponse Get(string url, string studioKey)
        {
            var req = UnityWebRequest.Get(url);
            return Send(req, studioKey);
        }

        public IStudioResponse PostJson(string url, string studioKey, string json)
        {
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
