# What the Recorder Kit can reach on your machine

For a studio deciding whether to install `com.projectnova.recorder-kit`. Every statement below
names the code it comes from (every line reference re-resolved programmatically 2026-09-21, again
for the `probe` section, and again 2026-09-22 against the code shipped as kit 0.8.0 — `package.json`
and `KitVersion.Current` read 0.8.0). Kit 0.6.0 and 0.7.0 were never published: what this page
marks "new in 0.6.0" or "new in 0.7.0" first ships in v0.8.0. If the
code and this page disagree, the code wins — tell us.

## Two channels into your Editor

**1. The cloud agent — opt-in, off by default.** `Editor/Cloud/NovaCaptureAgent.cs` polls
`https://<box>/studio/capture-jobs` with the studio key you paste in the Nova Capture window. It is
armed only when you tick "Run capture jobs" (`EditorPrefs`, `NovaCaptureAgent.Enabled`, default
`false`): no job is fetched or run until both that tick and a key are present (`NovaCaptureAgent.Tick`). Two calls
do happen without the tick — pressing **Connect** sends one `GET /studio/me` to check the key you
just pasted, then one `GET /studio/workspaces` to list your account's games for the picker below
(`NovaCaptureAgent.ConnectRoutine`). That is the button doing what it says, not the agent running.

**Which game is this Unity project? (new in 0.6.0)** A studio key belongs to your ACCOUNT, and one
key is often shared by several Unity projects. So the Nova Capture window asks you to pick the
workspace (the game) THIS project is, once. It is stored in `EditorPrefs` under this project's own
key (`NovaCaptureAgent.StudioKey`) — never in a file in your repo — and sent on every call as an
`x-nova-workspace` header (`StudioApi.cs:144-149`). Nothing is inferred: a project is never bound
for you, even if your account has a single game.

Said exactly, because "only ever runs its own workspace's jobs" was too strong: once you have
picked a workspace, this project refuses any job that names a DIFFERENT one
(`CaptureJob.cs:419-427`, `NovaCaptureAgent.RunJob`). A job that names NO workspace at all — which
only an API older than this release sends — is not refused by that rule, because there is nothing
to compare; for a `capture` the game-id check still applies, and an `export` is refused outright
unless the job names a workspace and it is yours (`CaptureJob.cs:433-448`).

A job is DATA, not a command. It carries a **kind** and, for a recording, a shot NAME from your own
`Library/Nova/shots.json`, a take number and an optional target stage
(`Editor/Cloud/CaptureJob.cs:18-151`). A kind this kit has no handler for is refused whole, before
anything runs (`CaptureJob.cs:92-94`), and a shot name that is not in your file is refused
(`NovaCaptureAgent.RunJob`, "unknown shot in this project's shots.json"). From 0.8.0 a recording
may also carry the TEXT of one shot the website sent — your pack's `tutorial-gate` — which every
take runs first (see **`capture`** below). Apart from that and a try's shot (**`probe`**, below), the
cloud cannot send a script to run, nor a path, and everything it can
write goes under `Library/Nova/`: three files, plus a `.local-<utc>.bak` copy of either of them if
you had edited it, plus a short-lived temp file beside each write (`.tmp-<pid>` for the two sent
files, `.tmp` for `synced.json`). See **`sync-nova`** below,
which is new in 0.7.0 and changed this paragraph: before it, the cloud could write nothing at all.

**What changed, exactly.** A `sync-nova` job carries the TEXT of a `shots.json` and an
`adapter.json`, and a shots file can contain `cheat` commands — so from 0.7.0 the cloud can put a
command on your disk. Every REFLECTION WRITE such a shot can make is refused until you tick it in
the Nova Capture window: its `cheat` / `cheatUntil` / `setup` commands and the `ready` block (its
`muteGet` read included, since the fourteenth audit: a `get` runs your property getters, which is code) go
through one decorator (`LeverGateBridge.Run`, built in `AdDirector`'s constructor), and the two writes that do not go through that
interface — a `timeScale` step and disabling an `overlayTypeNames` MonoBehaviour — are gated at
their own call sites against the same file (`AdDirector.DoStep`, `AdDirector.RunAll`).
What a delivered shot CAN do without a tick is press your uGUI elements by name (`click`, `hold`)
— the same reach a person driving the relay has. The exemptions are named in the levers section
below.

**When a job stops, and when it only pauses.** Only an ANSWER from the server ends a job. A
network failure, a 5xx, and the two 4xx that mean "not now" — 408 (request timeout) and 429 (rate
limit) — are all "try again on the next poll"; ANY OTHER status from 400 to 499 ends it, because
the run has finished, been taken over after the lease lapsed, or is gone (`CaptureJob.cs:297-343`).
Said as the range because that is what the code does. The two exceptions are there because this
decision throws away an export build that cost minutes of your editor's time: a rate limit on a
busy day must not do that (`ClaimRefusalEndsJob` / `IsDeterministicAnswer`, shared by the claim,
the header POST, the upload-ticket request and the RESULT post of a self-test or a `sync-nova` —
**not** `done`, which does not ask).

Where a persistent failure really ends the job, said exactly (audit round 4): the self-test and
`sync-nova` results, the header POST, the upload-ticket request and each part PUT have an attempt
budget (3 attempts,
15 s apart, no `Retry-After`), so those do end. The claim/lease refresh and the `done` POST have
**no** budget — they retry every 15 s for as long as the Editor is open. So "a rate limit must not
drop a studio's gigabyte build" survives a limit shorter than about 30 seconds on the bounded
callers, and indefinitely on the two unbounded ones.

There are exactly four kinds:

- **`capture`** — records the named shot and uploads the clip and its `.steps.json`. From 0.8.0 a
  recording's claim may carry your pack's `tutorial-gate` shot, its text and SHA-256
  (`CaptureGateClaim`); the website decides that once per recording, by the rule a try's gate is
  decided by. Such a recording runs the gate before EVERY take: its hash is checked against the
  bytes that arrived before it is used, by the reader a try's gate goes through
  (`CaptureGatePlan.Prepare` → `ProbePlan.ReadGate`), and it runs with recording OFF, under the lever
  gate a try runs under (`CaptureRun.OptionsFor`). Before each take the kit checks every lever the
  take needs — the gate's, the shot's and the adapter's — against `levers.json`, and one that is not
  ticked refuses that take before anything runs (`CaptureRun.RefusedBeforeTake`). A stop in the gate
  fails that take, and no clip is made. The website hands such a recording only to a kit of 0.8.0 or
  newer in a Unity project bound to its workspace; any other editor that claims it is refused by
  name. Once a recording is under way its gate is decided, and if its editor goes away the website
  does not list it to such an editor: each poll reads on past the runs that editor cannot take,
  through at most the 50 oldest runs waiting, so such a recording stands in front of the recordings
  behind it only when 50 or more runs that editor cannot take are waiting ahead of them; a recording
  not yet started is listed, and the first such claim fails it with the reason.
- **`self-test`** (new in 0.5.0) — an editor check you start from the web. It **records nothing and
  runs no cheat**: it enters Play Mode, waits for the game to draw, takes ONE screenshot, and posts
  that PNG together with a small set of facts about the editor. Two things it does touch, both
  editor state rather than your game: it enters Play Mode, and if your project has no
  `playModeStartScene` it pins one for the run and releases it afterwards (`NovaCaptureAgent.ReleaseStartScenePin`, `NovaCaptureAgent.NoteAbortedAttempt`).
  It leaves the editor in Play Mode when it finishes. The facts: kit and Unity version, whether `Application.runInBackground` is on,
  whether Unity Recorder is present and which recorder driver is in use, `timeScale`, how many
  frames were drawn and for how long, the boot scene path, how many shots loaded and any shot-file
  parse errors, whether an `adapter.json` is present, and the game id your project reports
  (`Editor/Cloud/SelfTestFacts.cs`). **New in 0.9.0:** the scene that was active when the picture
  was taken (**0.9.1:** after waiting up to 45 s for the game to leave its boot scene, and when it did), whether the game looks stuck on its boot scene, how many errors your game logged since
  Play was pressed (errors from your editor scripts — `/Editor/` code, editor update/delayCall — are not counted, **0.9.1**) and **the text of the FIRST one (up to 300 characters)**, and the git branch and
  commit the project is on (read from `.git` as text — git is never run). That first error line is
  your game's own log text: machine paths are replaced, a URL keeps its host and path but loses its
  query string, and any run of 32+ letters/digits (a token, a key, an id) becomes `<redacted>` — but
  anything else your game writes into an error line leaves your machine (`PlayConsoleWatch.Scrub`). It
  records no gameplay and runs no cheat — it only observes. It is the one thing in this kit that
  sends a screenshot rather than a clip, which is why it is called out here.

- **`sync-nova`** (new in 0.7.0) — **"Send to my editor"**, pressed from the web by your account's
  OWNER. It writes the shots you authored on the website onto this machine and tells the website
  what your Unity project made of them. Edit Mode: it never presses Play, records nothing, and runs
  no command in your game (`NovaCaptureAgent.RunSyncNova`). It is spelled out below, because it is
  the only job in this kit that WRITES into your project.

- **`export`** (new in 0.6.0) — **"Learn my game"**, started from the web by your account's OWNER.
  This is the one job that sends something other than a recording or a screenshot, and it sends a
  lot, so it is spelled out in full in its own section below. In short: it reads your project in
  Edit Mode, never presses Play, opens no scene, runs no cheat, changes no import setting and
  writes nothing under `Assets/` (`NovaCaptureAgent.RunJob`, `NovaCaptureAgent.RunExport`). It is only ever handed to
  a Unity project that has PICKED the workspace it is for, and the kit refuses it again itself if
  the job and the pick disagree, or if this project's `Library/Nova/adapter.json` says it is a
  different game (`CaptureJob.cs:433-448`).

**2. The file relay — always armed while the Editor is open.** `Editor/Relay/RelayBoot.cs:13-41`
starts a pump on every Editor session (only batch mode disables it) that executes every `*.json`
dropped in `Library/AdRelay/commands/` (`Editor/Relay/RelayServer.cs:134`). This is the LOCAL
channel an operator's tools use on your machine; nothing remote writes to that directory. Two facts
you should know, both open on our side and both fixable only by a kit release you install on
purpose: it has no arm switch, no indicator and no idle stop; and its `open-scene` command is not
restricted to `Assets/` (`RelayServer.cs:44-52, 350-372`). Anything with write access to
`Library/AdRelay/commands/` on your machine can drive the Editor through it.

## What the commands can do (`Editor/Adapter/ReflectionCheatBridge.cs:53-56`)

All twenty-two, in the order the code lists them — an earlier version of this page named
seventeen and quietly left out the five that change what is on screen:

`singletons` · `dump <Type>` · `get <Type>.<member>` · `set <Type>.<member> <value>` ·
`call <Type>.<Method> [args]` · `ui-dump` · `click <name>` · `invoke-button <name>` ·
`raycast-at` / `tap-at` / `input-probe` / `press-at` / `release-at` / `tap-through` / `drag` ·
`hide-ui <name>` / `show-ui` · `content-scan` · `camera-pose` / `camera-release` · `help` ·
`raw <cmd>`, plus any cheat you list in your own `shots.json`.

`hide-ui` / `show-ui` suppress a uGUI element for the length of a recording (footage hygiene — a
settings gear, a HUD label that contradicts the ad); they hide and never rewrite, and `show-ui`
restores from a ledger. `camera-pose` / `camera-release` pin and release the live camera for a
static framing. `raw` bypasses a built-in when your game defines a verb of the same name. In plain words: over the
relay, reflection can read and write public members of your game's live singletons and invoke their
methods. That is the whole point (it is how shots are driven without touching your code) and the
whole risk. Nothing here reaches outside the Editor process or your project directory.

## What "Learn my game" reads and uploads (the `export` job, new in 0.6.0)

Everything below is built into zip files under `Library/AdRelay/export/<run>/`
(`CaptureJob.cs:894-911`) — ALWAYS there, even in a pre-0.3 project whose relay traffic still lives
in a project-root `AdRelay/` folder. Until this release the export followed the relay into that
folder, which put a build of up to a gigabyte inside the studio's repo (`ExportRoot`, `:910`). You can open those zips while it runs — they are exactly what is sent.

**When the build is deleted from your disk.** When the server accepts the job, with the run's
progress file (`NovaCaptureAgent.DropProgress`, `NovaCaptureAgent.ReportDone`). When the run stops being yours — you
cancelled it from the web, or another editor took it over — at the next poll (`NovaCaptureAgent.PollRoutine`). And if
it ends any other way at all (the `done` answer is lost, the Editor is closed mid-run), the next
poll that finds no job in flight SWEEPS the whole `export/` folder before it asks for work
(`ExportOnDisk.cs:67-75`, `NovaCaptureAgent.PollRoutine`). Unticking "run capture jobs" mid-export
is the one case that waits on you: nothing runs at all while it is off, so the build stays until
the first poll after you re-tick, which either finishes the job or finds the run is no longer yours
and removes it (`NovaCaptureAgent.PollRoutine`). Until this release only the first case removed anything, so a
cancelled export left up to a gigabyte behind for good. **The kit reports facts and makes no judgement**: it never decides which font is "the
display font" or which class is "a cheat menu"; that reading happens on our side, where it cites
the file it came from and can be disputed.

| What | Exactly | Code (`Editor/Export/ExportCollector.cs`) |
|---|---|---|
| Asset list | for every asset under `Assets/`: guid, path, Unity type, file size, whether it is in the build, and (**new in 0.9.0**) whether your Addressables reach it | phase 1 `ScanAssets`, phase 4 `ReadInBuild` |
| Reference graph | which asset directly references which (guids only) | phase 3 `:612` |
| Build + player settings | the build scene list; active build target; **product name, company name, bundle version, application identifier, run-in-background, default orientation, icon guids** — those PlayerSettings fields and no others; `Resources/` folders; a COUNT of StreamingAssets files; Addressables group files and the guids in them (read from the YAML on disk — no Addressables package needed — through the same bounded, symlink-refusing walker as every other tree read, and not read at all if a group file is over 8 MiB, which is then said in `errors`) | phase 2 `:325`, groups `:515` |
| Font, colour, audio facts | font assets with their family name and how many assets reference them; every ScriptableObject's serialized `Color` fields — including arrays and lists of them, named `swatches[0]`, and only from the asset's OWN document, never from the Material or atlas Unity stores in the same file (the YAML key that holds each colour + hex); every audio clip's length, channels and sample rate. **The colour and TMP family-name facts are read from the asset FILE, never by loading it; the audio facts and a `.ttf`/`.otf` family name ARE read by loading** — engine formats with no script of yours attached, released again as the pass goes. See "what it never does" below | phase 5 `:693` |
| **Lines of your C#** | for `.cs` files under `Assets/` up to 1 MiB: every line matching one of EIGHT fixed patterns — `#if UNITY_EDITOR/DEVELOPMENT_BUILD/DEBUG`, a class named like `*Debug*/*Cheat*/*DevMenu*/*DevTool*/*GMTool*/*Console*`, `Random.InitState(`, `RemoteConfig/RemoteSettings/FirebaseRemoteConfig`, `ftue/tutorial/onboarding…=`, `feature_unlock`, `PlayerPrefs.Get/Set…("`, and (**new in 0.9.0**) a debug action registered by name — `RegisterAction("…"`, `AddCommand("…"`, `[Command("…")]`, `DevVar<…>("…")` and the like, so the NAMES of your dev-console actions leave your machine (a name like "Reset user" is collected as a fact; running any of them stays a lever you tick) — sent as file path, line number and **the matching line's text, trimmed to 200 characters**. No other source is read out. The table is `ExportFormat.Patterns` | phase 6 `ExportCollector.cs:971` |
| Docs | `.md`/`.txt` under a project-root `docs/`, `Docs/` or `Documentation/` folder, `README*` at the project root, and (**new in 0.9.0**) `AGENTS.md`, `CLAUDE.md` and `GEMINI.md` at the project root — up to 1 MiB each | phase 7 `ExportCollector.cs:1056` |
| **Remote config** | any file named `remote_config*.json` anywhere under the project root (not `Library/`, `Temp/`, `Logs/`, `obj/`, `Packages/`, `node_modules/` or dot-folders) — up to **16 MiB** each (1 MiB before 0.9.0), 200 docs+config files in total. **If yours holds keys or secrets, they are uploaded.** The kit cannot tell and does not try. Check before you press the button | phase 7 `ExportCollector.cs:1056` |
| **Your game's own words** (**new in 0.9.0**) | the source-language strings file(s) of your localization: a file under `Assets/` whose path says localization/translation/i18n/l10n, in a text format, whose name marks English or the source language (e.g. `translations_en.json`) — up to 8 MiB each, 20 files. Other languages are not sent | phase 7b `ReadStrings` |
| **Button names** (**new in 0.9.0**) | from the YAML text of every `.prefab` and enabled build scene: for each object with a click handler (`m_OnClick`) — its name path (`Canvas/Shop/BuyButton`), its visible label (the first text on it or its children, 200 chars) and the method names its click calls. Read as text; nothing is loaded or run. At most 50,000 rows | phase 6b `ReadUi` |
| Art | the ORIGINAL files of: app icons (every platform's, **0.9.0**), fonts, audio clips and textures, each up to **8 MiB** (audio up to **24 MiB**, **0.9.0**), up to **1 GiB** in total, **the ones your game uses first** (in the build or reached by Addressables, **0.9.0**); plus a **256-px thumbnail of every texture** (rendered through a temporary RenderTexture — your import settings are not touched). Anything over a cap is not sent and is LISTED by path and size instead | phase 8 `ExportCollector.cs:1127`, thumbnails `:1286` |

**While it builds (new in 0.9.0)** the kit posts a progress line to our API every few seconds —
the phase name, a percentage, and how many parts and bytes are built and uploaded. Counts only: no
file names, no content (`NovaCaptureAgent.ProgressBody`).

**Where it goes.** The kit first posts a small header (the counts above, your Unity and kit
version, your product and company name, and the list of zip parts with their sizes and SHA-256).
Each part is then PUT to a short-lived signed URL the server hands out for that one part — either
our API or our storage provider. **That PUT carries no studio key and no other header of ours**
(`StudioApi.cs:101-121`): the signed URL is the only credential, so your key is never sent to a
third-party host. If this API is `https://` the kit refuses an upload URL that is plain `http://`
rather than following it (`CaptureJob.cs:516-540`).

**What is in the free text.** The header's `errors` list names anything the kit could not read.
Those strings are built from exception messages, and .NET writes absolute paths into those — so
every one is scrubbed before it is recorded: your project folder becomes `<project>`, your home
folder `~`, the temp folder `<temp>` (`ExportFormat.cs:297`, `ExportCollector.cs:1624`; a test
holds this against a real unreadable asset). Asset paths are relative to your project by
construction. **Project-relative paths, product name and company name DO leave your machine** —
that is what an inventory is. Those three (product name, company name and the game id from your
`adapter.json`) now go through the same cleaning as everything else listed: a control character in
any of them becomes a space, and they are cut to 200/200/120 characters. Until this release they
were sent raw, and a newline in a product name — which Unity's own field accepts — failed the WHOLE
export at the server's door, after the upload.

**What it costs you.** Editor time: roughly a millisecond per asset plus 2–3 ms per texture
thumbnail. The work is sliced so the Editor stays usable, and **~12 ms is the slice TARGET, not a
guarantee** — a handful of steps cannot be interrupted once started and run longer.

The steps that cannot be interrupted, as far as we know them: the first whole-project asset search,
the recursive dependency read per enabled build scene, each memory sweep, deflating a 16 MiB facts
shard, and hashing each finished zip part. Two more used to be on this list and are not any more:
the docs sweep and the `remote_config` hunt walked the whole project tree between two slices —
measured at 933 ms and 1,858 ms on two real projects — because they only handed work back when
they FOUND something. They now yield on every folder and file they walk, so the walk itself is
interruptible; what is still uninterruptible there is one file read.

**Measured where, and when.** 34.8 ms was the longest single slice on a one-asset project and
89 ms on a probe project holding an 11 MB ScriptableObject (2026-09-21). The 933 ms and 1,858 ms
above were measured by the audit that found them, BEFORE the repair; the repair itself is
structural — a slice per entry walked — and has not been re-timed on a real project. **What one
sweep costs in a large project is UNMEASURED**: nobody has timed
`UnloadUnusedAssetsImmediate` on a project holding tens of thousands of textures, and the thumbnail
pass now fires one every 512 MiB of estimated texture memory or every 256 items. The 34.8 ms and
89 ms above are what has actually been measured, and both came from small probe projects. Expect
hitches; how long they are on your project is not something this file can honestly tell you yet.

Disk, honestly, as a BOUND rather than a number: while it runs, the export under `Library/` holds
the original art it is shipping (capped at 1 GiB in total, `artTotalMaxBytes`), plus one ≤ 256-px
PNG thumbnail per texture up to 20,000 of them, plus the facts files. Thumbnails are written
straight into the zip parts as they are made — until this release each one also sat in a scratch
folder until cleanup, so the peak was twice that. The whole folder is deleted when the run ends (see
above).

A script recompile, or entering or leaving Play Mode, reloads the domain and RESTARTS the build.
The build may START five times in all — so four restarts — after which the job gives up and says so
(`NovaCaptureAgent.MaxExportBuildStarts`, `NovaCaptureAgent.RunExport`). The owner can cancel a waiting or running export from the web at any
time.

**Measured where, said plainly:** these timings come from a small generated project and from a
probe project built to force the worst cases, not from a shipped game. The TextMesh Pro font path
IS now exercised — the kit's own suite creates a real `TMP_FontAsset` and reads its family name
back out of an export with the atlas written ABOVE the face info and again BELOW it
(`Editor/Tests/ExportTmpFontTests.cs`), because Unity orders a file's documents by signed fileID
and a font's atlas usually has a negative one. The earlier version of that fixture padded the file
BELOW the face info only, which is the author's assumption encoded as a test: it passed while the
reader nulled the family name of 22 of 48 real fonts.

The family-name reader HAS now been run against two real studios' font libraries, read-only: 61
`.asset` files holding an `m_FaceInfo:` across the two games on the author's machine, of which 48
carry a non-empty family name (the other 13 are TMP sprite assets and are correctly empty).
**48 of 48 are read** — measured outside the Editor, by a standalone Mono probe calling the kit's
compiled assembly on those files. On the development Mac, warm, an 8.5 MB atlas-first font reads in
about 10–13 ms (the first call in a process is slower); one machine, not a worst case. Allocation
was measured once (`GC.GetAllocatedBytesForCurrentThread`, which is bytes allocated, not peak live
memory: about 313 KB for the worst file, about 34 KB for an 8.5 MB font) and not re-measured. The memory figures
elsewhere in this section have still never been measured on a shipped game.

## What "Send to my editor" writes (the `sync-nova` job, new in 0.7.0)

This is the one job in the kit that WRITES into your project, so here is all of it.

**Three files, all under `Library/Nova/`** (gitignored by Unity's own template, like everything
else this kit writes) — plus the `.bak` and `.tmp-<pid>` files described below, in the same
folder and nowhere else:

| File | What |
|---|---|
| `shots.json` | the shot list you authored on the website, byte-for-byte as it was sent |
| `adapter.json` | this game's id, its `ready` block, its `overlayTypeNames` and its `camera` block — byte-for-byte as it was sent |
| `synced.json` | what the last send wrote (the two files' SHA-256, the run id, a UTC timestamp, and `replacedLocalEdits` — whether that send copied a file of yours out of the way, kept beside the run id so a RETRY of the same send tells you the same thing) **and every pair of SHA-256s the cloud has delivered to this project, newest 20 of each** — the history the lever gate reads (`SyncNova.WriteSynced`, `SyncNova.ReadSynced`) |

**A file you edited is copied first, never overwritten.** Before replacing either file the kit
hashes what is on disk. If that hash is not one the cloud has ever delivered here — you edited it,
or it was never synced — the file is copied to `<name>.local-<utc>.bak` beside it and the answer to
the website says `replacedLocalEdits` (`SyncNova.Backup`). If the copy fails, the write is
refused and your file stays. A file that is ALREADY byte-for-byte what is arriving is left
completely alone — not even its timestamp changes. And a file that is on disk but cannot be READ
(permissions) refuses the whole send by name: it cannot be copied, so it is not replaced
(`SyncNova.TryReadSha`).

**It verifies before it writes.** The website sends each file's SHA-256 with it; both are checked
before either file is touched, so a truncated download cannot become a truncated `shots.json`
(`SyncNova.Run`, step 0 — both shas before either write). Each write is temp-file-then-`File.Replace` — a plain move when the file
did not exist yet — so a failure mid-write leaves the old file in place. `synced.json` is written
the same way; until 2026-09-21 it was deleted and moved, which had a window where it did not exist
at all.

**And it records the delivery BEFORE the delivery.** The order is: verify both hashes → copy
anything of yours → write `synced.json` with the two incoming hashes appended to its history →
write the two files → write `synced.json` again, now naming what is actually on disk
(`SyncNova.Run`, steps 1-5). It is in that order so that a send which fails half-way leaves the file it
DID write GATED. Until 2026-09-21 the record came last, so a handled failure (or a crash) left the
cloud's `shots.json` on your disk with nothing saying it came from the cloud — and its commands
then ran as if you had written them.

**It runs nothing.** No Play Mode, no scene opened, no cheat, no recording. It writes the files only
when the loader your game uses would take every shot in them, re-reads them through that loader, and
reports what loaded: the shot NAMES, the game id it read back, the two files' hashes AS THEY ARE ON
DISK, the levers those files need, the levers you have ticked, and whether a local edit was backed up
(`SyncNovaResult.ToFactsJson`). That is the whole answer; nothing else about your project goes with it.

**It is only ever handed to a project that picked its workspace** — that gate is the SERVER's
(`KINDS_NEEDING_WORKSPACE_BINDING`), and this kit checks the job's workspace against your pick again
before it writes (`NovaCaptureAgent.RunJob`). Unlike every other kind, it is NOT refused when the
job's game id differs from your `Library/Nova/adapter.json` — this job is what WRITES that file, and
on a project that has never been onboarded there is nothing to compare (`CaptureJob.cs:373-382`).
What it does refuse: a job whose own game id disagrees with the `adapter.json` it is delivering,
and an `adapter.json` that names no game id at all — both are a mix-up on our side, and nothing is
written (`SyncNova.Refusal`). It also refuses, before anything is written, a file larger than the
API's send cap (512 KB for `shots.json`, 64 KB for `adapter.json`); a file it cannot read exactly as
its own readers will — a text they would read back differently from the bytes about to be written
(one that begins with U+FEFF, which they drop as a byte-order mark), or one nesting arrays and objects
more than 256 deep; a `shots.json` the kit's own loader would refuse any part of, with that loader's
first error — not JSON, not an object, no `shots` array, a `$schemaVersion` other than 1, or any shot
it cannot load, such as one with a number of seconds that is not finite, is under 0.1 or is over 600,
a name over 80 characters, more than 500 `setup` commands or 500 steps, or a condition of more than
64 parts, counting the parts of every `all` inside it (the delivery check IS the loader,
`JsonShotLoader.Read`); an `adapter.json` whose
`ready` block holds a write that is not a string, is blank, or holds a line break or another
character the window cannot show — `muteGet` included — whose command as the ready gate sends it
(`muteGet` as `get <muteGet>`) is longer than 256 characters, or that names no command the ready gate
reads (the gate's own refusal, `HygieneSpec.Problem`); a pair that asks for more than 300 levers; a
lever holding a line break or another such character; a lever longer than 256 characters; and a
`{placeholder}` nothing can bind — one its shot does not declare, or any in `adapter.json`
(`SyncNova.DeliveryRefusal`). These checks read the text the kit's readers will read back; a file
they cannot read is refused by name, never read as asking for nothing.

## Levers: a delivered shot cannot write into your game until you tick it

A `shots.json` can contain `cheat` commands, which are reflection writes into your running game. One
you wrote yourself is yours. One that arrived from the website is not — so it is refused until a
person ticks it in **Tools > Recorder Kit > Nova Capture > "Levers the cloud may use"**. The ticks
live in `Library/Nova/levers.json`, which that window is the only writer of: not the agent, not a
job, not anything the server sends (`NovaCaptureWindow.DrawLevers`).

- **When the gate is live:** while EITHER `Library/Nova/shots.json` or `Library/Nova/adapter.json`
  is a file the cloud has delivered to this project — `synced.json` remembers the hashes, so a file
  written by an earlier send still counts (`Levers.GateActive`). Files you authored or edited on
  this machine are NOT gated: every project that used this kit before 0.7.0 behaves exactly as it
  did. Taking a delivered project back means editing or deleting BOTH files — an edit to
  `shots.json` alone used to turn the gate off while the cloud's `adapter.json` was still in place,
  and its `ready` block is a write into your game. A `synced.json` this kit cannot read gates
  everything (fail closed). The director asks whether the gate is live once at the top of each
  attempt of a shot and holds the answer for that attempt; the ticks are still read for every
  command, so a tick or an untick takes effect on the next one (`LeverGateBridge.HoldLiveness`).
- **Where it is enforced:** one decorator on `ICheatBridge.Run`, installed where the director builds
  its context (`AdDirector`'s constructor). That is the seam every CHEAT a shot can run goes through —
  a shot's `setup`, its `cheat` / `cheatUntil` steps, and the `adapter.json` `ready` block. A
  refused command returns false and writes `lever not approved on this machine: '<command>'` into
  the run's log; nothing is thrown and nothing reaches your game (`LeverGateBridge.Run`).
- **Two writes do NOT go through that interface, and are gated where they happen.** A shot's
  `timeScale` step needs the lever `timeScale` — once, whatever the factor — and the step FAILS
  (and with it the take) when it is not ticked (`AdDirector.DoStep`). Each `overlayTypeNames`
  entry in a delivered `adapter.json` needs `hide-overlay <TypeName>`; an unticked one is simply
  left VISIBLE, with `overlay '<T>' left visible — lever not approved on this machine:
  hide-overlay <T>` in the log, and the run continues (`Levers.OverlaysAllowed`,
  `AdDirector.RunAll`). The generic adapter — the one you get by installing this package and
  nothing else — takes EVERY overlay name it has from `adapter.json`, so on a delivered project
  every one of them is gated, including a name that was in an earlier `adapter.json` and is not in
  today's. Only an overlay type that exists in a game adapter YOU COMPILED is never gated, and that
  is your own code, not something the cloud can write.
- **A third write the cloud can make, and it does not look like one.** `adapter.json`'s `camera`
  block names the three METHODS the `camera-pose` verb calls (`setPosition`, `setRotation`,
  `setFov`) and the Component it finds when a step names no Type of its own (`viewType`). A step
  that DOES name a Type (`camera-pose SaveManager 1 2 3 …`) is REFUSED on a delivered project unless
  that Type is the block's `viewType` — the step's own Type used to win silently (a block with no
  `viewType` leaves the step's Type standing: that step was ticked as written; the Type must be the
  `viewType` exactly, case and all) — and a `camera-pose` holding a quote anywhere, or a placeholder
  in its Type, cannot be ticked at all (the game splits a command at its quotes, so a quote anywhere
  can move which argument is the Type). Approving `camera-pose 1 2 3 …` approves a
  framing, not the method names behind it, so the block needs its own lever:
  `camera-spec <viewType> <setPosition> <setRotation> <setFov>`, with `*` for an absent view type
  and each default spelled out. Un-ticked, the pose is REFUSED — it never quietly falls back to the
  kit's own defaults, because a shot framed by the wrong camera is a wrong recording. A `camera`
  block that says nothing but the defaults asks for no tick at all (`Levers.CameraSpecNeeded`,
  `CameraPose.Pose`). The check lives inside the pose, not in the decorator, because what it guards
  is the block the pose READS — so it applies to the relay operator too (see below).
- **What is NOT gated, said plainly:** a `click` or `hold` step. A delivered shot can press any
  uGUI element in your game by name, with no tick, because that is the same reach the relay gives
  whoever is at this keyboard — and pressing things is how a shot plays your game at all. A
  `vision` step writes its prompt into `Library/AdRelay/vision/requests/` — except during a try
  ("Try this shot", below), where it sends one frame of your screen to the website instead. Nothing
  else a delivered shot carries reaches your game.
- **Two things pass without a tick, deliberately.** (1) The kit's own three fixed verbs: exactly
  `ui-dump`, `show-ui` and `hide-ui`, with no argument (`Levers.IsReadOnly`). They run none of your
  game's code except two things: `ui-dump` reads each label through the UI's own text getter (so a
  Text subclass of yours that overrides `text` runs), and `show-ui` re-enables — running `OnEnable`
  on — only what a ticked `hide-ui <name>` hid; a bare `hide-ui` fails before touching anything.
  (Corrected by the fifteenth audit: this said "the kit's code is all they run".) The director
  itself runs the first two. `hide-ui <name>`, with an argument, is NOT on
  that list and does need a tick. **A `get` is not on it either, since the fourteenth audit
  (2026-09-22):** it was, for thirteen rounds, as "a read", but resolving its path reads your types'
  static `Instance` and every member on the way, and a property getter is code — a lazy singleton
  creates an object in your scene when it is read. So a delivered `get`, from a shot or from
  `adapter.json`'s `ready.muteGet` (sent as `get <muteGet>`), needs a tick like any lever; un-ticked,
  the ready gate fails and says the read was not ticked, never that the bed is unmuted. (2) **The relay's own `run-cheat` is
  exempt** (`RelayServer.cs:215-229`): that command was typed by whoever is at the keyboard of this
  machine, which is exactly who a tick is asking — with ONE exception, `camera-spec`. While the gate
  is live, an operator's own `run-cheat camera-pose …` is refused (`lever not approved on this
  machine: 'camera-spec …'`) until the `camera` block's lever is ticked, whenever that block names a
  view type or a non-default method: the typed command is the operator's, but the method names the
  pose calls come from `adapter.json`, which the cloud may have written. It fails closed, and names
  the lever. The relay's `run-shot` and `ready` are NOT exempt — they go through the director, so
  they are gated like anything else.
- **What a tick means:** the command as authored. Approving `SelectHero {hero}` approves
  `SelectHero Knight`, and refuses `SelectHero Knight Extra` or `SelectHero Button@Label` — the same
  value rule the kit's own parameter binding uses, so an approval cannot be widened by a value
  (`ShotBinding.MatchesTemplate`, which gives up after 100 ms and reads that as NO match). A name
  used twice is ONE value, as the binder writes it: ticked `set Player.{a} {a}` approves
  `set Player.coins coins` and never `set Player.coins 999`. ONE
  function decides which ticked entry covers a command (`Levers.CoveringApproval`): its exact text
  first; else, for a command that is itself a template, the first ticked template of the SAME SHAPE
  — equal once every placeholder is one fixed token, with the same placeholders sharing a name, so
  `SelectHero {x}` covers `SelectHero {hero}`, `set Player.{a} {a}` does not cover
  `set Player.{s} {v}`, and a literal never covers a template; else the first template it is a binding of, where a value
  bound into a TARGET placeholder may hold no `.`. The gate, the window and a probe all ask it, so a
  checkbox answers for its row's own shape as the gate does — the one exception is an entry in the file
  that the gate ignores, shown ticked with the reason so it can be taken out. A row whose box is empty while
  the gate lets some of its commands through is a row another ticked entry partly covers — a
  broader template (ticked `set Player.{a} {b}`, the files asking for `set Player.coins {v}`), a
  narrower or overlapping one (ticked `SelectHero K{x}`, the files asking for `SelectHero {hero}`),
  or one of its bindings ticked as a literal (`SelectHero Knight`). A box answers for its own shape:
  ticking this row approves every command of this form (except one whose value in a target holds a
  `.`), whatever other entries let through. What
  the rows say about it is under "What the window lists". `hide-overlay …` and `camera-spec …` are never templates: a text whose verb is one of them,
  wherever it came from, is approved by its own exact text only, and the kit's binder binds no
  placeholder into one.
- **What the window lists** (`Levers.Rows`): the levers the two files on disk ask for; EVERY other
  entry of your `levers.json`, marked as one nothing asks for any more, so a tick you no longer
  want can always be taken back here; and any command this editor session's gate REFUSED that no
  file names — which is how a game adapter you compiled yourself gets its own ready-gate and
  recovery commands in front of you instead of failing with nothing to tick. Such a row says only
  what the gate knows: "refused in this editor session; no file on disk asks for it now" — true of the FILES: a command your own compiled adapter runs is not in them,
  and the window says so (ticked, its row says "not needed by the files on disk"). A refused binding
  of a template the files list is not listed again — the template's row names it — unless a tick of
  that template would still refuse it (a value in a command's target may not hold a '.'); then it
  is its own row, and says exactly that. A command the kit's binder writes from a template the files list fits that template: since
  the sixth audit the binder and the gate read one value grammar, so a value ending in a line break is
  refused when it is bound. Each
  box answers for its row's own shape by the gate's rule: a row covered by a ticked template — a
  binding of it, or the same template with a parameter renamed — is shown ticked, names that
  template, and has its own box disabled (it is not in `levers.json`, so unticking it could revoke
  nothing — untick the template), and the template's own row names what it covers ("approves
  `SelectHero {hero}`, which the files ask for"). One of the kit's three fixed verbs is shown ticked:
  "a read — needs no tick"; a `get` is an ordinary row, empty until you tick it.
  A row another ticked entry partly covers stays unticked. What the rows say about it is what a
  witness finds, not every case: a ticked template's row never claims the files don't need it — one
  that covers nothing the files ask for says "a template — approves any command that fits it, except
  one whose value in a target holds a '.'; not asked for in this exact form"; a needed row names the
  first ticked entry, in `levers.json`'s order, for which a witness shows that entry letting through
  a command a tick of the needed row would approve too (after a "refused in this editor session as …"
  note, if the row has one) — so a row the window will not let you tick names none. The witness is
  not a proof, and an overlap it misses is not named. A ticked literal that a template the files ask
  for can be bound to (`SelectHero Knight` under `SelectHero {hero}`) says "not asked for in this
  form by the files; it approves only this command, as written"; a literal no template on disk can be
  bound to says "not needed by the files on disk" (a match that runs out of time counts as none).
- **Five kinds of lever cannot be ticked** (`Levers.NotLiteralEnough`, `Levers.NotTickableReason`;
  the website refuses the same shapes, pinned by one shared case file,
  `Editor/Tests/Fixtures/lever-shapes.cases.json`):
  1. a command whose FIRST word is not literal — it contains a `{placeholder}` (`s{a} {b} {c}`,
     `{a} {b}`) or a quote (`" {a}" {b}`: the game splits a command WITH quotes and reads the quoted
     span as the verb, so it is a placeholder verb in disguise);
  2. a command whose TARGET is not literal — for `set`, `call`, `raw`, `click`, `invoke-button` and
     `hide-ui`, the word after the verb may not begin with a placeholder or a quote (`set {a} {b}`,
     `call {t}.Run`, `set "{a}" 1`), and a placeholder in it must be a whole name between dots
     (`set Player.{stat} {v}`, never `call S{a}` or `set Player.x{a} 1`) whose bound value holds no
     `.`; a `camera-pose` may hold no quote anywhere (the game splits a command at its quotes, so
     `camera-pose {t} 1 2 3 4 5 "6"60` is eight arguments with `{t}` as the Type), and with eight
     arguments no placeholder in the Type (`camera-pose {t} 1 2 3 4 5 6 60`);
  3. a command with two placeholders and nothing between them but characters a value may hold —
     letters, digits, `.`, `_` and `-` (`{a}{b}`, `{w}x{h}`, `{a}.{b}`, `{a}_{b}`, `{a}-{b}`):
     `Knight-1` against `{x}-{y}` reads two ways, so a command cannot be read back against it. The
     text between two placeholders must hold a character a value cannot hold (`{a} {b}`, `{a}:{b}`);
  4. in a command with a placeholder, a `{` or `}` that is not part of one (`SelectHero {{h}}`,
     `set Player.{a}} 1`): a value bound inside such braces makes a command that reads as a
     placeholder itself (`h=knight` gives `SelectHero {knight}`);
  5. a `hide-overlay …` / `camera-spec …` lever holding `{` or `}`, which names nothing a C# type or
     method can be called.

  Ticked once, the first three would approve commands you were never shown, and the fourth's
  commands can read as templates themselves; so the window shows each of these four with the reason
  and no working checkbox, and the gate IGNORES one even if it is already in the file
  (`Levers.CoveringApproval`). The fifth, if it got into the file by hand, still approves only its
  own exact text — a type or method name that cannot exist — because those levers are never
  templates. Any of them can be unticked. A placeholder anywhere else is an ordinary approval, and
  so is a quote in a later argument (`call Dialog.Say "Some Arg"`) of any verb but `camera-pose`.
- **Fail closed:** no `levers.json`, or one this kit cannot read — including one holding anything
  that is not a command where a command should be — approves NOTHING (`Levers.Approved`). And a
  `synced.json` this kit cannot read GATES rather than opens (`Levers.GateActive`,
  `SyncNova.ReadSynced`). "Cannot read" includes a file that parses and names no sha at all: `{}`,
  one whose history is not a list, or one whose lists hold no string (`{"cloudShots": [7]}`) is a
  record of a delivery this kit can no longer account for, and it is treated as a delivery.

## What "Try this shot" runs (the `probe` job, new in 0.8.0)

This job RUNS a shot the website sent in your game, so here is all of it.

**What arrives.** One shot object and the `adapter.json` text it was authored against, each with its
SHA-256, on the claim response and nowhere else (`CaptureJob.ParseClaim`, `ProbeRequest`). Both
hashes are checked against the UTF-8 bytes that arrived before either text is used. A missing
block, a hash that does not match, a shot the kit's own loader refuses — or a
`Library/Nova/adapter.json` on this machine that is not byte-for-byte the adapter that arrived (or
no such file: nothing was ever sent here) — ends the job with the reason and runs nothing, before
Play Mode is even entered (`ProbePlan.Prepare`, `NovaCaptureAgent.RunJob`). The last one reads
"press Send to my editor, then try again": a try RUNS with the adapter.json on your disk (below),
so it is only run when that file is the one the website listed the levers from and will grade
against. The script is kept on the job's progress file
(`Library/AdRelay/capture-status.json`) so it survives the Play-Mode reload, and goes with it.

**A second shot, run first: your `tutorial-gate`.** A try of any other shot may carry one more shot:
your pack's shot named `tutorial-gate`, its text and SHA-256 (`ProbeRequest.TutorialGate`), which
the website sends while that shot's newest try reached its end on the text your pack holds. It is
checked as the shot is — its hash against the bytes that arrived, before it is used — and the job is
refused whole, running nothing, if it is not the shot named `tutorial-gate`, if it holds a screen
check (a `vision` step), or if it and the shot do not load together (`ProbePlan.Prepare`). It then
runs BEFORE the shot tried, in the same Play session and director (`AdDirector.Options.Preamble`,
set only from the claim, by `ProbeRun.Start`): its recovery, `setup`, arm wait, steps and end state,
with recording OFF — the recorder is never started for it — and under the same lever gate: the
levers checked before anything runs are the tried shot's, the adapter's AND the gate's, and it runs
through the same forced gate as the shot. The ready gate runs for it first, then again for the shot
after it. A stop in it is the try's stop: the shot tried never runs. The answer says whether it ran,
which of its steps stopped it, and the hash of the gate text that arrived (`gateRan`,
`gateFailedStep`, `gateSha256`).

**What it writes: nothing under `Library/Nova/`.** Not `shots.json`, not `adapter.json`, not
`synced.json`, not `levers.json` — the shot runs from memory. It records nothing: its recorder
starts nothing and its folder is never created (`NoRecordingDriver`). What it DOES write is under
`Library/AdRelay/`, the kit's own working folder:
- its frame, `probe-frame-<run>.png`, deleted once the server has it — or, when the try lost its
  lease mid-run (the frame can land on disk after the run was stopped), at the next try or the next
  job this editor claims: every try's frame but the one of the run in flight is deleted then
  (`ProbeRun.SweepStrayFrames`);
- one frame per `vision` step of the shot, `probe-frame-<run>-vision-<id>.png` — and, only for a
  frame of 8 MiB or more, its smaller JPEG copy `…-vision-<id>.jpg` — deleted when that screen check ends
  (answered, refused, given up, or the run stopped). One left behind by an interrupted run is swept
  like the try's own frame; the JPEG copy is not (`HttpVisionChannel`, `ProbeRun.SweepStrayFrames`);
- the job's progress file, `capture-status.json`, which holds the website's shot and adapter text
  while the job lasts (above);
- the command bridge's report files under `probe/` — every director run opens with `show-ui`, and
  every `ui-dump` and every TICKED `get …` a shot runs leaves its answer there, as it does for
  any shot (`ReflectionCheatBridge.WriteProbe`);
- `camera-hold.json`, if the shot runs a TICKED `camera-pose` (`CameraPose`), released when the
  run ends.

**Levers — the same rule, enforced harder.** A probe is cloud content, so it may run only levers
you ticked. It cannot use the ordinary test for "did this come from the cloud" — that test hashes
the files ON DISK (`Levers.GateActive`), and a probe writes none, so on a project with your own
files it would answer "yours, ungated". Instead:

1. **Before anything runs**, the kit lists every lever the shot and the arriving `adapter.json`
   need (`Levers.NeededFrom` — the list the website computes) and checks each against
   `levers.json` (`Levers.FirstUnticked`). One not ticked, and no director is built at all: not the
   shot's `setup`, not a ready gate, nothing reaches your game, and the answer names the lever
   (`leverRefused`) (`ProbeRun.Start`). **This check is stricter than a capture of the same shot,
   on purpose:** a try needs EVERY lever the shot and the adapter ask for — including a
   `hide-overlay <Type>` a capture would only skip (leaving that overlay visible, with a log line),
   and the adapter's `camera-spec` lever even when the shot has no `camera-pose` (a capture asks for
   it only at a pose). Read-only commands are the one exemption. It fails closed, and every lever it
   asks for is a row you can tick in the Nova Capture window. A lever of a shot you EDITED on the
   website but have not sent is not a row yet — the window lists the levers of the files on your
   disk — so the website then says "Send to my editor first": the send writes the edited shot and
   its row appears.
   A `camera-pose` that names its own Type other than the view type your `adapter.json` camera
   block names is refused here too, whole, before any lever is asked for: no tick can make it run
   (`ProbePlan.CameraTypeRefusal`).
2. **While it runs**, the director is built with the gate FORCED on (`ProbeRun.DirectorOptions`,
   `CloudContent = true` → `Levers.GateActiveFor`), at every point the gate is enforced: the
   command bridge (`LeverGateBridge`) — which also asks for the `camera-spec` lever before a
   `camera-pose` reaches your camera (`CameraPose`'s own check of that lever reads only the files on
   disk, so the bridge asks it with the run's flag) — a `timeScale` step, and overlay hiding. And,
   wrapped round the bridge for a try only, the camera-pose Type rule: a pose whose own Type is not
   the camera block's view type is refused (`ProbeCameraTypeGuard`), because `CameraPose` asks that
   rule, too, only of the files on disk. The flag is fixed when the director is built and nothing
   can turn it off during the run.

The exemptions are the ones in the levers section: the kit's three fixed verbs, and driving your UI by name
(`click`/`hold`). Your own game adapter's ready-gate and recovery commands run through the same
gate during a probe, so a compiled adapter whose commands you have not ticked fails its ready gate
there and says so — those commands then appear in the window to tick.

**What it runs against.** THIS project's adapter: the one registered in Play Mode, and your
`Library/Nova/adapter.json` for its `ready` block, overlays and `camera` block. The adapter text
that arrives with the probe works out the levers above, and the try runs only when your
`adapter.json` is byte-for-byte that text — so the levers you are asked to tick are the ones of the
adapter that runs, and the answer is graded against the adapter that ran.

**What it runs.** The one shot, once (`MaxAttempts = 1`), through the same director, `setup`, ready
gate, recovery and pre-shot wait a capture of that shot would use (`NovaCaptureAgent.RunProbe`) —
after the `tutorial-gate` shot, when one arrived with it (above).

**What goes back** (`POST …/result`): whether it reached the end, which step stopped it (index and
kind) and the director's own sentence, the names on screen at that moment, the Game view's render size,
`Time.timeScale`, the director's log, the levers needed and ticked, the two hashes (of the texts
that ARRIVED), and ONE frame (`ProbeFacts.Build`). Two of those carry text from your game: the names on screen are the rows
`ui-dump` prints — UI object names, component type names and visible button labels
(`ReflectionCheatBridge.OnScreenRows`), at most 200 of 120 characters — and the log can carry the
text of an exception your game threw. If the result cannot be posted it is retried as a POST; the
shot is not run again to answer the same question.

**The screen check: one more frame per `vision` step, to the website, during a try only**
(`POST …/vision`, `HttpVisionChannel`). A `vision` step asks "does the screen match this prompt?".
In a try, that question goes to the website, which answers it (with one model call on your own
Claude key: that is the website's rule, not something this package can enforce). For each `vision`
step the kit sends ONE frame of the Game view (a PNG, as the try's own frame
is taken; sent again, with the same id, only on a retry), the step's position in the shot, and an
id for the question. It sends NOT the prompt
(the website reads it from the shot it sent), and nothing else from your game. A frame is sent only
when it is under 8 MiB: one of 8 MiB or more is re-encoded as a JPEG (quality 85) and halved in
size until it is under; one that still is not is not sent, and the step fails saying so. A capture's `vision` steps are unchanged: they write to
`Library/AdRelay/vision/requests/` for a person on this machine and send nothing. How the answer
reads in the try's result:
- the website answered "no": `… did not resolve — the screen check answered: the screen did not
  match`. This is the only case that says "did not match".
- the website REFUSED (a 4xx: the try has used its screen checks, the amount you confirmed is spent,
  the lease ran out, the check could not be answered): `… — the site refused the screen check at
  step N (409): <the website's own sentence>`. It is not asked again, and it is never reported as
  "did not match".
- no answer (a 5xx, a 408/429, or no response): the SAME question is sent again, with the same id,
  so it is paid for once. Up to 4 sends, 1, 2 and 4 s apart, then `… — the site did not answer the
  screen check at step N after 4 attempts with the same request id (last: …)`.
- the step's own `timeout` (60 s unless the shot says otherwise) still bounds all of it: `… — no
  answer to the screen check within the step's 60s timeout`.

## What it never does
- Writes under `Assets/` or creates a branch — everything the export builds lands in
  `Library/AdRelay/export/`, which Unity's standard `.gitignore` already excludes. (One exception
  that is NOT the export: a project upgraded from a pre-0.3 kit keeps its small relay command files
  in a project-root `AdRelay/` folder, because an in-flight session must not be split across two
  trees. The export build is never written there.)
- **Loads your ScriptableObjects.** The export reads them as TEXT, off the asset files; it does
  not load them, so none of your `Awake`, `OnEnable` or `OnValidate` runs. Until this release it
  did load them, and they did run. (A project set to FORCE BINARY asset serialisation has no text
  to read; the export says so in its `errors` instead of reporting no colours.) It does load
  textures, audio clips and `.ttf`/`.otf` files to measure them — engine formats with no script of
  yours attached.

  ONE HONEST QUALIFICATION, because "runs none of your code" would be too strong a sentence: to
  give memory back the export asks Unity to drop assets nothing is using, and dropping a
  `ScriptableObject` that your own tooling had loaded and let go runs that object's `OnDisable` /
  `OnDestroy`. Unity does exactly the same sweep on its own, all day; the export changes WHEN one
  happens, not WHETHER your code can be reached that way — and this release made them more
  frequent. It never loads one of yours in order to read it.
- **Changes what your Editor is showing.** Memory is released only by asking Unity to drop assets
  NOTHING is using (`ExportCollector.cs:1687-1703`). Until this release it also unloaded each
  texture and audio clip by hand, including ones your open scene was using — which read back grey
  until something re-assigned them. That release now also runs in the pass that reads your AUDIO,
  which loads every clip in the project: until this release nothing swept those at all, so on a
  game whose clips are set to preload, the whole audio library stayed in your editor's memory after
  the export finished. The kit's own suite holds the safe half of this against a real clip an
  `AudioSource` in the open scene is using, the way it already did for a texture.
- **Leaves your project directory.** Every folder walk skips symlinks and junctions outright, so a
  folder inside your project that points somewhere else on your disk is listed and never read
  (`ExportScan.cs:94-167`). It also cannot be sent into an infinite loop by one.

  Being exact, because the walker was only half of it. UNITY follows a symlink under `Assets/`: a
  folder linked in from elsewhere on your disk is imported, and its textures, audio and fonts have
  guids like any other asset. Until this release the art and identity passes took whatever Unity
  had imported, so those files' ORIGINAL BYTES were being packed and uploaded — from outside your
  project, while this page said otherwise. Now: the one walk the export already makes over
  `Assets/` records which folders and files are links, every asset under one is LISTED in the
  inventory (guid, path, size — Unity imported it, and pretending otherwise would be its own kind
  of lie) and NONE of them is read: no original, no thumbnail, no colours, no font family, no
  audio facts. Each link is named in `errors`. Verified on macOS with a real `ln -s`; Windows
  junctions and directory links are UNVERIFIED — the same `FileAttributes.ReparsePoint` test
  covers them and nothing has run it there.
- Sends anything to the box beyond the job's own result and a status report: a clip and its
  `.steps.json` for a `capture`, one screenshot and the facts above for a `self-test`, the shot
  names / parse errors / hashes / lever lists above for a `sync-nova`, one frame and the facts
  above (names on screen, the director's log) for a `probe` — plus, during that try, one frame per
  `vision` step with its step index and question id (the screen check, above) — and for an `export` exactly
  the table above — which is a great deal, and is why it has its own section, is owner-only on the
  web, and never runs unless this project picked that workspace.

  Being exact about "status report", because it is the part with free text in it. Every job
  reports claim/progress/done, and a failure carries the reason as text — an exception message
  from the agent, an HTTP response body, or a shot-file parse error
  (`NovaCaptureAgent.Fail`, `NovaCaptureAgent.EnsurePlayModeStartScene`, `CaptureJob.cs:693-722`). The self-test facts
  likewise include three free-text fields (`frameNote`, `factsError` if a call inside the
  editor threw, and — new in 0.9.0 — `firstConsoleError`, your game's first logged error, scrubbed as
  described above). None of it is gathered on purpose. The EXPORT's error strings are scrubbed of
  absolute paths (above); these status strings are NOT — an exception message from the agent
  itself can contain a path from your machine, so it is named here rather than implied. Every
  call to our API also carries the kit's version header, and the workspace you picked — one
  function for every call, the screen check's included (`UnityStudioHttp.ApplyStudioHeaders`,
  `StudioApi.cs:136-150`).
- Runs with more than your Editor's own rights.

## The human gates are the containment boundary
A recording starts only from a job you allowed the agent to take (the tick) or a file you — or a
tool you ran — dropped locally. There is no automation path that bypasses either. Since 0.7.0 there
is a second gate of the same kind: a shot the website sent may be WRITTEN to your disk by a job you
allowed, but the reflection writes in it do not RUN in your game until a person ticks them in the
Nova Capture window — and a shot the website asks this editor to TRY (the `probe` job, 0.8.0) is held
to the same ticks even though it writes no file — with the exemptions named in the levers section (the kit's three fixed verbs, the local
relay, and driving your UI by name, which is never gated). That is the line we hold, and any change
to it is a kit release you install on purpose.

## Least credential
The studio key is capture-scoped and account-bound: it can claim, upload to and finish YOUR
capture jobs and nothing else — it cannot read or write any other account's data, and it is not a
login. We store it as a sha256, never in the clear.
