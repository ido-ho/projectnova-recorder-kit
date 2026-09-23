using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Slice D part 1 (owner decision D10) — which Unity project is which game, and the pure halves
    /// of the export job that run inside the agent's pump: the workspace guard, the upload ticket,
    /// the persisted export stages, and the "is this build whole" resume rule. These are the exact
    /// comparisons production runs; the only Unity API any of them touches is
    /// `Application.dataPath`, which is the POINT of the project-root test below.
    ///
    /// Every refusal is paired with the same call, one thing changed, shown to PASS (invariant 50):
    /// a guard that always says no is indistinguishable from one that works.
    /// </summary>
    public class WorkspaceBindingTests
    {
        private const string WsA = "workspace-aaaa";
        private const string WsB = "workspace-bbbb";

        [Test]
        public void Refusal_ABoundProjectRefusesAnotherWorkspacesJob_AndTakesItsOwn()
        {
            StringAssert.Contains("different workspace", WorkspaceGuard.Refusal(WsB, WsA));
            Assert.IsNull(WorkspaceGuard.Refusal(WsA, WsA), "positive control: its own workspace");
            Assert.IsNull(WorkspaceGuard.Refusal(" " + WsA + " ", WsA), "whitespace is not a different workspace");
        }

        [Test]
        public void Refusal_AnUnboundProjectCannotCheck_AndProceeds_TheRolloutSafeFloor()
        {
            // Every kit installed before this release is unbound. If this refused, every existing
            // studio would stop recording the moment it updated.
            Assert.IsNull(WorkspaceGuard.Refusal(WsA, null));
            Assert.IsNull(WorkspaceGuard.Refusal(WsA, ""));
            Assert.IsNull(WorkspaceGuard.Refusal(null, WsA), "an older API that sends no workspaceId");
        }

        [Test]
        public void ExportRefusal_AnUnboundProjectNeverAnswersAnExport_AndIsToldWhatToDo()
        {
            var why = WorkspaceGuard.ExportRefusal(WsA, null, "rogue-legend", null);
            Assert.IsNotNull(why);
            StringAssert.Contains("not bound to a workspace", why);
            StringAssert.Contains("Nova Capture", why, "it must name the window, not just refuse");
            Assert.IsNotNull(WorkspaceGuard.ExportRefusal(WsA, "", "rogue-legend", null));
        }

        [Test]
        public void ExportRefusal_BoundToTheJobsWorkspace_BeforeOnboarding_IsAdmitted()
        {
            // THE case the export exists for: a new game, no Library/Nova/adapter.json yet. The
            // capture rule would refuse this ("cannot verify"); the binding is what admits it.
            Assert.IsNull(WorkspaceGuard.ExportRefusal(WsA, WsA, "rogue-legend", null));
            Assert.IsNull(WorkspaceGuard.ExportRefusal(WsA, WsA, null, null));
        }

        [Test]
        public void ExportRefusal_WrongWorkspace_OrAnOnboardedProjectThatIsAnotherGame_IsRefused()
        {
            StringAssert.Contains("different workspace",
                WorkspaceGuard.ExportRefusal(WsB, WsA, "rogue-legend", null));
            StringAssert.Contains("wrong game",
                WorkspaceGuard.ExportRefusal(WsA, WsA, "rogue-legend", "snl-scratch"));
            // positive control: onboarded AND the same game
            Assert.IsNull(WorkspaceGuard.ExportRefusal(WsA, WsA, "rogue-legend", "rogue-legend"));
            Assert.IsNotNull(WorkspaceGuard.ExportRefusal(null, WsA, "rogue-legend", null),
                "a job that names no workspace cannot be verified");
        }

        [Test]
        public void JobGuard_DoesNotApplyTheCaptureIdentityRuleToAnExport()
        {
            // An export runs where there is no adapter.json and no registered adapter. If the
            // capture rule spoke for it, the one job that belongs on a fresh project is refused.
            Assert.IsNull(JobGuard.RefusalReason("export", "rogue-legend", null));
            Assert.IsNull(JobGuard.RefusalReason("export", "rogue-legend", "unregistered"));
            // …while a CAPTURE on the same values is still refused (the rule did not loosen).
            Assert.IsNotNull(JobGuard.RefusalReason("capture", "rogue-legend", "unregistered"));
        }

        [Test]
        public void StudioWorkspace_ParseList_ReadsRows_AndNeverThrows()
        {
            var list = StudioWorkspace.ParseList(
                "{\"workspaces\":[{\"id\":\"w1\",\"name\":\"Rogue Legend\",\"gameId\":\"rogue-legend\"}," +
                "{\"id\":\"w2\",\"name\":\"New game\",\"gameId\":null},{\"name\":\"no id\"},7]}", out var error);
            Assert.IsNull(error);
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("Rogue Legend  (rogue-legend)", list[0].Label);
            Assert.AreEqual("New game", list[1].Label);
            Assert.IsNull(list[1].GameId);

            Assert.AreEqual(0, StudioWorkspace.ParseList("not json", out var e1).Count);
            Assert.IsNotNull(e1);
            Assert.AreEqual(0, StudioWorkspace.ParseList("{\"jobs\":[]}", out var e2).Count);
            Assert.IsNotNull(e2);
        }

        [Test]
        public void UploadTicket_AnAbsoluteUrlIsUsedAsGiven_APathIsJoinedOntoTheApiBase()
        {
            var bucket = UploadTicket.Parse(
                "{\"url\":\"https://proj.storage.supabase.co/storage/v1/s3/nova/game-export/w/r/part-0000.zip?X-Amz-Signature=abc\"," +
                "\"method\":\"PUT\",\"headers\":{\"Content-Type\":\"application/zip\"}}",
                "https://admiral.test/api", out var e1);
            Assert.IsNull(e1);
            StringAssert.StartsWith("https://proj.storage.supabase.co/", bucket!.Url);
            Assert.AreEqual("application/zip", bucket.Headers["Content-Type"]);

            var api = UploadTicket.Parse(
                "{\"url\":\"/storage-upload/game-export/w/r/part-0000.zip?exp=1&max=2&sig=x\",\"method\":\"PUT\",\"headers\":{}}",
                "https://admiral.test/api/", out var e2);
            Assert.IsNull(e2);
            Assert.AreEqual("https://admiral.test/api/storage-upload/game-export/w/r/part-0000.zip?exp=1&max=2&sig=x", api!.Url);
        }

        [Test]
        public void UploadTicket_RefusesWhatItCannotFollow_ByName()
        {
            Assert.IsNull(UploadTicket.Parse("{\"method\":\"PUT\"}", "https://x/api", out var noUrl));
            StringAssert.Contains("no url", noUrl);
            Assert.IsNull(UploadTicket.Parse("{\"url\":\"https://x/y\",\"method\":\"POST\"}", "https://x/api", out var post));
            StringAssert.Contains("only knows PUT", post);
            Assert.IsNull(UploadTicket.Parse("nope", "https://x/api", out var notJson));
            Assert.IsNotNull(notJson);
        }

        [Test]
        public void UploadTicket_RefusesAnHttpTicketWhenTheApiIsHttps()
        {
            // The part is up to 32 MiB of the studio's project and the signed URL IS the credential.
            // A server that means to move the upload can say so over TLS (fresh-context audit).
            Assert.IsNull(UploadTicket.Parse(
                "{\"url\":\"http://evil.test/put\",\"method\":\"PUT\"}", "https://admiral.test/api", out var down));
            StringAssert.Contains("downgrade", down);

            // POSITIVE CONTROLS, one thing changed each time:
            Assert.IsNotNull(UploadTicket.Parse(
                "{\"url\":\"https://ok.test/put\",\"method\":\"PUT\"}", "https://admiral.test/api", out var https));
            Assert.IsNull(https);
            // a studio on a plain-http box (a LAN install) is not downgrading anything
            var lan = UploadTicket.Parse(
                "{\"url\":\"http://box.lan/put\",\"method\":\"PUT\"}", "http://box.lan/api", out var lanErr);
            Assert.IsNull(lanErr);
            Assert.AreEqual("http://box.lan/put", lan!.Url);
        }

        [Test]
        public void UploadTicket_AnythingElse_ResolvesOntoTheApiBase_AndIsDocumentedAsSuch()
        {
            // Neither shape is GUESSED at — both land on the API base as literal text and 404
            // there. Untested until now, and it is the behaviour the doc comment promises.
            var schemeRelative = UploadTicket.Parse(
                "{\"url\":\"//other.host/put\",\"method\":\"PUT\"}", "https://admiral.test/api", out var e1);
            Assert.IsNull(e1);
            Assert.AreEqual("https://admiral.test/api/other.host/put", schemeRelative!.Url,
                "it must NOT become https://other.host/put");

            var file = UploadTicket.Parse(
                "{\"url\":\"file:///etc/passwd\",\"method\":\"PUT\"}", "https://admiral.test/api", out var e2);
            Assert.IsNull(e2);
            Assert.AreEqual("https://admiral.test/api/file:///etc/passwd", file!.Url);
        }

        // ---- does a refused claim end the job? (fresh-context audit K3) ------------------

        [Test]
        public void ClaimRefusalEndsJob_ANYAnswerIn400To499EndsIt_ExceptTheTwoThatMeanTryAgain()
        {
            // SAID AS THE CODE IS: the rule is the RANGE 400-499, not the three statuses the old
            // name and TRUST.md implied (audit round 3, K3 follow-up).
            Assert.IsTrue(JobGuard.ClaimRefusalEndsJob(409, null), "409 — another editor holds it");
            Assert.IsTrue(JobGuard.ClaimRefusalEndsJob(403, null));
            Assert.IsTrue(JobGuard.ClaimRefusalEndsJob(404, null));
            Assert.IsTrue(JobGuard.ClaimRefusalEndsJob(400, null), "…and every other 4xx, which is what the code does");
            Assert.IsTrue(JobGuard.ClaimRefusalEndsJob(410, null));
            Assert.IsTrue(JobGuard.ClaimRefusalEndsJob(499, null));

            // …WITH TWO EXCEPTIONS, because they are the server saying "not now", not "no".
            // A studio behind a proxy that 408s, or a rate limiter that 429s on a busy day, must
            // not throw away an export build that cost minutes and a gigabyte of disk.
            Assert.IsFalse(JobGuard.ClaimRefusalEndsJob(408, null), "408 request timeout — nobody decided anything");
            Assert.IsFalse(JobGuard.ClaimRefusalEndsJob(429, null), "429 rate limit — try again, not give up");

            // NOBODY ANSWERED. Dropping the job here is how one network blip abandoned a run
            // mid-flight and threw away an export build that had cost minutes.
            Assert.IsFalse(JobGuard.ClaimRefusalEndsJob(0, "Cannot connect to destination host"));
            Assert.IsFalse(JobGuard.ClaimRefusalEndsJob(500, null));
            Assert.IsFalse(JobGuard.ClaimRefusalEndsJob(502, null));
            Assert.IsFalse(JobGuard.ClaimRefusalEndsJob(503, null));
            // a transport error WINS even if a status is somehow also set
            Assert.IsFalse(JobGuard.ClaimRefusalEndsJob(409, "connection reset"));

            // POSITIVE CONTROL: a 2xx is not a refusal at all.
            Assert.IsFalse(JobGuard.ClaimRefusalEndsJob(200, null));
            Assert.IsFalse(JobGuard.ClaimRefusalEndsJob(204, null));
        }

        // ---- where the delete is pointed (audit round 3, M4 / M5) ------------------------

        [Test]
        public void TheProjectRoot_ComesFromTheOpenProject_NotFromTheWorkingDirectory()
        {
            // A RECURSIVE DELETE hangs off this value (`ExportOnDisk.SweepOrphans` removes the whole
            // `export/` folder). `Directory.GetCurrentDirectory()` is process-wide state that any
            // script, package or test in the editor can change; `Application.dataPath` is the
            // editor's own answer to "which project is open" and nothing else can repoint it.
            var expected = Path.GetDirectoryName(UnityEngine.Application.dataPath)!;
            UnityEngine.Debug.Log("[novakit-m4] cwd=" + Directory.GetCurrentDirectory() +
                                  " dataPathParent=" + expected);
            Assert.AreEqual(expected, NovaCaptureAgent.ResolveProjectRoot(),
                "with the working directory untouched, the two already agree");

            var previous = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(Path.GetTempPath());
                Assert.AreEqual(expected, NovaCaptureAgent.ResolveProjectRoot(),
                    "a changed working directory must not be able to repoint the export's recursive delete");
            }
            finally
            {
                Directory.SetCurrentDirectory(previous);
            }
        }

        [Test]
        public void TheExportAlwaysBuildsUnderLibrary_EvenInAPre03ProjectWithALegacyAdRelayFolder()
        {
            // `RelayPaths.ResolveRoot` keeps a pre-0.3 project's relay traffic in the PROJECT-ROOT
            // `AdRelay/` folder so an in-flight session is not split across two trees. The export
            // build — up to a gigabyte, swept by a recursive delete — must not follow it there:
            // TRUST.md promises "everything lands in Library/, gitignored", and the project root is
            // the studio's repo.
            var root = Path.Combine(Path.GetTempPath(), "novakit-legacy-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "AdRelay"));
                File.WriteAllText(Path.Combine(root, "AdRelay", "status.json"), "{}");

                // CONTROL: the legacy relay folder really is in force for the relay itself.
                Assert.AreEqual(Path.Combine(root, "AdRelay"), RelayPaths.Root(root));

                Assert.AreEqual(Path.Combine(root, "Library", "AdRelay", "export"),
                    CapturePaths.ExportRoot(root), "the export build is never written to the repo");
                Assert.AreEqual(Path.Combine(root, "Library", "AdRelay", "export", "run1"),
                    CapturePaths.ExportDir(root, "run1"));

                // …and a project with no legacy folder is unchanged.
                var fresh = Path.Combine(root, "fresh");
                Directory.CreateDirectory(fresh);
                Assert.AreEqual(Path.Combine(fresh, "Library", "AdRelay", "export"), CapturePaths.ExportRoot(fresh));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // ---- the orphan sweep (fresh-context audit K2) -----------------------------------

        [Test]
        public void SweepOrphans_RemovesABuildNoJobOwns_AndNeverOneAJobDoes()
        {
            var root = Path.Combine(Path.GetTempPath(), "nova-sweep-" + System.Guid.NewGuid().ToString("N"));
            var exportRoot = Path.Combine(root, "export");
            try
            {
                Directory.CreateDirectory(Path.Combine(exportRoot, "run-1"));
                File.WriteAllText(Path.Combine(exportRoot, "run-1", "part-0000.zip"), "a gigabyte, in spirit");

                // A JOB IS IN FLIGHT (there is a progress file): its build is not debris.
                Assert.IsFalse(ExportOnDisk.SweepOrphans(exportRoot, progressFileExists: true));
                Assert.IsTrue(Directory.Exists(exportRoot), "the running job's build must survive");

                // No job, so nothing under export/ belongs to anyone: the owner cancelled, the
                // `done` answer was lost, or the studio unticked and never came back.
                Assert.IsTrue(ExportOnDisk.SweepOrphans(exportRoot, progressFileExists: false));
                Assert.IsFalse(Directory.Exists(exportRoot), "the orphaned build is gone");

                // Nothing to sweep is not a sweep.
                Assert.IsFalse(ExportOnDisk.SweepOrphans(exportRoot, progressFileExists: false));
                Assert.IsFalse(ExportOnDisk.SweepOrphans("", progressFileExists: false));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void StudioEndpoints_TheThreeNewRoutes()
        {
            Assert.AreEqual("https://x.test/api/studio/workspaces", StudioEndpoints.Workspaces("https://x.test/api/"));
            Assert.AreEqual("https://x.test/api/studio/capture-jobs/r1/export/header", StudioEndpoints.ExportHeader("https://x.test/api", "r1"));
            Assert.AreEqual("https://x.test/api/studio/capture-jobs/r1/export/upload-url", StudioEndpoints.ExportUploadUrl("https://x.test/api", "r1"));
        }

        [Test]
        public void Headers_AreLowercase_AndMatchTheApi()
        {
            // Node lowercases incoming header keys; the API reads these exact strings
            // (apps/api/src/studio/studio-capture-job.ts).
            Assert.AreEqual("x-nova-workspace", KitVersion.WorkspaceHeader);
            Assert.AreEqual("x-nova-kit-version", KitVersion.Header);
        }

        // ---- the persisted export stages ----

        private static CaptureJob ExportJob() =>
            new CaptureJob("run-1", WsA, new List<CaptureJobItem>(), 1800, CaptureJob.ExportKind, "rogue-legend");

        [Test]
        public void Progress_AnExportIsNotDoneUntilItsResultFlagIsSet_AndItsStagesSurviveTheReload()
        {
            // HONEST NAME (fresh-context audit minor). This drives CaptureProgress and nothing
            // else: the last two lines SET `ResultPosted` themselves, so what they prove is that
            // `Done` follows that flag. They do NOT prove that uploading the last part sets it —
            // that wiring is in `NovaCaptureAgent.RunExport`, which needs a live editor and a fake
            // server and is not exercised here.
            var dir = Path.Combine(Path.GetTempPath(), "nova-export-progress-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, "capture-status.json");
                var p = CaptureProgress.Start(ExportJob());
                // It carries no items, so without the result flag it would read done the instant it
                // started and the post-reload agent would report complete having exported nothing.
                Assert.IsFalse(p.Done);
                Assert.AreEqual(WsA, p.WorkspaceId);
                p.ExportBuildStarts = 2;
                p.ExportBuilt = true;
                p.ExportHeaderPosted = true;
                p.ExportPartsUploaded = 3;
                p.Save(file);

                var back = CaptureProgress.Load(file)!;
                Assert.AreEqual(CaptureJob.ExportKind, back.Kind);
                Assert.AreEqual(WsA, back.WorkspaceId, "the workspace check runs again after the reload");
                Assert.AreEqual("rogue-legend", back.GameId);
                Assert.AreEqual(2, back.ExportBuildStarts);
                Assert.IsTrue(back.ExportBuilt);
                Assert.IsTrue(back.ExportHeaderPosted);
                Assert.AreEqual(3, back.ExportPartsUploaded);
                Assert.IsFalse(back.Done);

                back.ResultPosted = true;
                Assert.IsTrue(back.Done, "positive control: with the flag the agent sets, it reads done");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void Progress_ARefusedExportIsDone_AndReportsAFailure_NeverZeroZero()
        {
            var p = CaptureProgress.Start(ExportJob());
            p.RecordJobFailure("this Unity project is not bound to a workspace");
            Assert.IsTrue(p.Done);
            Assert.AreEqual(1, p.Failed, "a 0-item job that reported 0 failed would read completed on the API");
            StringAssert.Contains("not bound", p.DoneReportJson());
        }

        [Test]
        public void CapturePaths_ARunIdBecomesADirectoryName_SoItCannotWalkAnywhere()
        {
            var root = Path.Combine(Path.GetTempPath(), "proj");
            var dir = CapturePaths.ExportDir(root, "../../etc/passwd");
            StringAssert.StartsWith(CapturePaths.ExportRoot(root), dir);
            Assert.IsFalse(dir.Contains(".."));
            Assert.AreEqual("cmu8u9tma000b", Path.GetFileName(CapturePaths.ExportDir(root, "cmu8u9tma000b")));
        }

        // ---- ExportOnDisk: a build is resumed only if it is provably whole ----

        private static string NewBuildDir(out ExportHeader header, params int[] partSizes)
        {
            var dir = Path.Combine(Path.GetTempPath(), "nova-export-build-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            header = new ExportHeader { WorkspaceId = WsA, KitVersion = "0.6.0", UnityVersion = "6000.0.63f1" };
            for (var i = 0; i < partSizes.Length; i++)
            {
                var name = ExportFormat.PartName(i);
                File.WriteAllBytes(Path.Combine(dir, name), new byte[partSizes[i]]);
                header.Parts.Add(new ExportPartInfo { Index = i, Name = name, Bytes = partSizes[i], Sha256 = new string('a', 64), Entries = 1 });
            }
            File.WriteAllText(Path.Combine(dir, ExportOnDisk.HeaderFileName), header.ToJsonString());
            return dir;
        }

        [Test]
        public void ExportOnDisk_AWholeBuildIsResumed_PositiveControl()
        {
            var dir = NewBuildDir(out _, 64, 128);
            try
            {
                var header = ExportOnDisk.LoadVerified(dir, out var why);
                Assert.IsNull(why);
                Assert.AreEqual(2, header!.Parts.Count);
            }
            finally { ExportOnDisk.TryDeleteDir(dir); }
        }

        [Test]
        public void ExportOnDisk_DebrisIsNeverResumed_MissingPart_ShortPart_NoHeader()
        {
            var missing = NewBuildDir(out _, 64, 128);
            var shorter = NewBuildDir(out _, 64, 128);
            var headless = NewBuildDir(out _, 64);
            try
            {
                File.Delete(Path.Combine(missing, ExportFormat.PartName(1)));
                Assert.IsNull(ExportOnDisk.LoadVerified(missing, out var w1));
                StringAssert.Contains("is missing", w1);

                File.WriteAllBytes(Path.Combine(shorter, ExportFormat.PartName(1)), new byte[100]);
                Assert.IsNull(ExportOnDisk.LoadVerified(shorter, out var w2));
                StringAssert.Contains("100 bytes on disk; the header says 128", w2);

                File.Delete(Path.Combine(headless, ExportOnDisk.HeaderFileName));
                Assert.IsNull(ExportOnDisk.LoadVerified(headless, out var w3));
                StringAssert.Contains("never finished", w3);

                Assert.IsNull(ExportOnDisk.LoadVerified(Path.Combine(headless, "nope"), out var w4));
                Assert.IsNotNull(w4);
            }
            finally
            {
                ExportOnDisk.TryDeleteDir(missing);
                ExportOnDisk.TryDeleteDir(shorter);
                ExportOnDisk.TryDeleteDir(headless);
            }
        }

        [Test]
        public void ExportOnDisk_TryDeleteDir_NeverThrows()
        {
            Assert.DoesNotThrow(() => ExportOnDisk.TryDeleteDir(Path.Combine(Path.GetTempPath(), "nova-does-not-exist-" + System.Guid.NewGuid().ToString("N"))));
        }
    }
}
