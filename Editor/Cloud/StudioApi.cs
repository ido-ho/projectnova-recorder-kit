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

        private static IStudioResponse Send(UnityWebRequest req, string studioKey)
        {
            req.SetRequestHeader("Authorization", "Bearer " + studioKey);
            req.SetRequestHeader("Accept", "application/json");
            // Slice B/C commit 2: the API's version-skew gate decides which job KINDS this editor
            // may be handed from this header. It rides on EVERY call, not just the poll, because
            // the claim enforces the same rule — a filtered list is not a gate.
            req.SetRequestHeader(KitVersion.Header, KitVersion.Current);
            req.timeout = 600; // a clip upload on a slow studio line
            req.SendWebRequest();
            return new Response(req);
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
