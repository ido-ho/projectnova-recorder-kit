# What the Recorder Kit can reach on your machine

For a studio deciding whether to install `com.projectnova.recorder-kit`. Every statement below
names the code it comes from (checked against kit 0.5.0, 2026-09-20). If the code and this page
disagree, the code wins — tell us.

## Two channels into your Editor

**1. The cloud agent — opt-in, off by default.** `Editor/Cloud/NovaCaptureAgent.cs` polls
`https://<box>/studio/capture-jobs` with the studio key you paste in the Nova Capture window. It is
armed only when you tick "Run capture jobs" (`EditorPrefs`, `NovaCaptureAgent.cs:95-100`, default
`false`): no job is fetched or run until both that tick and a key are present (`:152`). One call
does happen without the tick — pressing **Connect** sends a single `GET /studio/me` to check the
key you just pasted (`:117-121`, `:166-170`). That is the button doing what it says, not the
agent running.

A job is DATA, not a command. It carries a **kind** and, for a recording, a shot NAME from your own
`Library/Nova/shots.json`, a take number and an optional target stage
(`Editor/Cloud/CaptureJob.cs:61-78`). A kind this kit has no handler for is refused whole, before
anything runs (`CaptureJob.cs:70-74`), and a shot name that is not in your file is refused
(`NovaCaptureAgent.cs:358-361`). The cloud cannot send a command, a script or a path; it cannot
write to your project.

There are exactly two kinds:

- **`capture`** — records the named shot and uploads the clip and its `.steps.json`.
- **`self-test`** (new in 0.5.0) — an editor check you start from the web. It **records nothing and
  runs no cheat**: it enters Play Mode, waits for the game to draw, takes ONE screenshot, and posts
  that PNG together with a small set of facts about the editor. Two things it does touch, both
  editor state rather than your game: it enters Play Mode, and if your project has no
  `playModeStartScene` it pins one for the run and releases it afterwards (`:281-286`, `:769-774`).
  It leaves the editor in Play Mode when it finishes. The facts: kit and Unity version, whether `Application.runInBackground` is on,
  whether Unity Recorder is present and which recorder driver is in use, `timeScale`, how many
  frames were drawn and for how long, the boot scene path, how many shots loaded and any shot-file
  parse errors, whether an `adapter.json` is present, and the game id your project reports
  (`Editor/Cloud/SelfTestFacts.cs:54-105`). It
  records no gameplay and runs no cheat — it only observes. It is the one thing in this kit that
  sends a screenshot rather than a clip, which is why it is called out here.

**2. The file relay — always armed while the Editor is open.** `Editor/Relay/RelayBoot.cs:13-41`
starts a pump on every Editor session (only batch mode disables it) that executes every `*.json`
dropped in `Library/AdRelay/commands/` (`Editor/Relay/RelayServer.cs:134`). This is the LOCAL
channel an operator's tools use on your machine; nothing remote writes to that directory. Two facts
you should know, both open on our side and both fixable only by a kit release you install on
purpose: it has no arm switch, no indicator and no idle stop; and its `open-scene` command is not
restricted to `Assets/` (`RelayServer.cs:44-52, 350-372`). Anything with write access to
`Library/AdRelay/commands/` on your machine can drive the Editor through it.

## What the commands can do (`Editor/Adapter/ReflectionCheatBridge.cs:16-41`)

`singletons` · `dump <Type>` · `get <Type>.<member>` · `set <Type>.<member> <value>` ·
`call <Type>.<Method> [args]` · `ui-dump` · `click <name>` · `invoke-button <name>` ·
`raycast-at` / `tap-at` / `press-at` / `release-at` / `tap-through` / `drag` · `input-probe` ·
`content-scan` · `help`, plus any cheat you list in your own `shots.json`. In plain words: over the
relay, reflection can read and write public members of your game's live singletons and invoke their
methods. That is the whole point (it is how shots are driven without touching your code) and the
whole risk. Nothing here reaches outside the Editor process or your project directory.

## What it never does
- Writes under `Assets/` or creates a branch — everything lands in `Library/`, gitignored.
- Sends anything to the box beyond the job's own result and a status report: a clip and its
  `.steps.json` for a `capture`, one screenshot and the facts above for a `self-test`.

  Being exact about "status report", because it is the part with free text in it. Every job
  reports claim/progress/done, and a failure carries the reason as text — an exception message
  from the agent, an HTTP response body, or a shot-file parse error
  (`NovaCaptureAgent.cs:715-720`, `:747-758`, `CaptureJob.cs:353-365`). The self-test facts
  likewise include two free-text fields (`frameNote`, and `factsError` if a call inside the
  editor threw). None of it is gathered on purpose and none of it includes file paths from your
  machine by design — but it is text your editor produced, so it is named here rather than
  implied. Every call also carries the kit's version header (`StudioApi.cs:96`).
- Runs with more than your Editor's own rights.

## The human gates are the containment boundary
A recording starts only from a job you allowed the agent to take (the tick) or a file you — or a
tool you ran — dropped locally. There is no automation path that bypasses either. That is the line
we hold, and any change to it is a kit release you install on purpose.

## Least credential
The studio key is capture-scoped and account-bound: it can claim, upload to and finish YOUR
capture jobs and nothing else — it cannot read or write any other account's data, and it is not a
login. We store it as a sha256, never in the clear.
