# ProjectNova Recorder Kit

Editor-side ad-capture director + file-based CommandRelay for driving **any** Unity game.
Add the package. `ping` and `screenshot` work immediately. Nothing is required of the game's
runtime code, and nothing is written under `Assets/` or to a `recorder` branch.

Per-game knowledge (shots, optional named cheats) is **data** Nova drops into `Library/Nova/` —
already gitignored by Unity. Command traffic lives in `Library/AdRelay/`.

**What this package can reach on your machine, and what it never does:** [`TRUST.md`](TRUST.md),
beside this file — read it before installing.

## Install

One line in the game project's `Packages/manifest.json` (`main` is fine; the kit is Editor-only
and does not ship in the player):

```json
"com.projectnova.recorder-kit": "https://github.com/ido-ho/projectnova-recorder-kit.git#v0.5.0"
```

Always pin a tag — `#v<version>`, matching a release on that repository. It is public and holds
only this package, so no credentials and no GitHub account are needed. (Releases start at v0.5.0;
earlier kit versions were never published there.)

Local development, on a machine that has the kit's source checked out — point at the directory
holding this `package.json`:

```json
"com.projectnova.recorder-kit": "file:/absolute/path/to/com.projectnova.recorder-kit"
```

**The `file:` form is only for a machine that has the kit's source checked out.** Keep the git-URL
pin on any project that does not. A kit fix reaches you as a **new tag** and a bump of that one
line to it — while your `manifest.json` names a tag, that tag is the only thing that decides what
Unity resolves. (No version number is written into this paragraph on purpose: the one in the
install line above is the pin, and a second copy in prose is a copy that goes stale.)

You do not need a `recorder` branch, and do not add `AdRelay/` to `.gitignore` — it lives under
`Library/`, which a Unity project's standard `.gitignore` already excludes.

Optional but recommended: `com.unity.recorder` (any 4.x/5.x). With it, clips record via
Unity Recorder at 1080×1920@60 H.264 with audio; without it the kit falls back to
ScreenCapture frames + ffmpeg (slower, no audio, frame pacing caveats).

## The seams a game adapter fills

| Seam | Question it answers | Default |
|---|---|---|
| `IStateProbe` | what logical state is the game in? | always `null` |
| `ICheatBridge` | perform a named state change | warn + fail |
| `IReadyGate` | is the game at a capturable baseline? | always ready |
| `IRecoveryPolicy` | get back to a known screen between shots, **and for the ready gate's one retry** | no-op |
| `IUiDriver` (optional) | click/read a non-uGUI UI stack | built-in uGUI driver |
| `GameAdapter.Shots` | the game's shot list (`AdShot` grammar) | empty |
| `GameAdapter.OverlayTypeNames` | debug overlays to hide during capture | none. Generic boot also reads `Library/Nova/adapter.json` `overlayTypeNames` (MonoBehaviour type names, re-read every `run-shot`). Example: `"overlayTypeNames": ["FPS"]`. `hide-ui` is uGUI-only; IMGUI/`OnGUI` overlays use this list. |

A game that still needs a custom seam can register from `[InitializeOnLoad]`:
`AdapterRegistry.Current = new GameAdapter { ... };` — that wins over the generic default.
Prefer `Library/Nova/shots.json` (re-read every command, no domain reload) over C# shot lists.

## Shots as data (`shots.json`)

A game's shot list can be a JSON file instead of hand-written C#, which means **editing a shot never
triggers a domain reload** — the loader re-reads the file on every relay command. Register it with:

    Shots = () => JsonShotLoader.LoadFromPath(RelayPaths.NovaShotsFile(projectRoot)).Shots,

`Shots` is a `Func<IReadOnlyList<AdShot>>`, not a list: that is what makes the re-read possible. A
game keeping its shots in C# just returns its array — `Shots = () => XShotList.V1`.

```json
{
  "$schemaVersion": 1,
  "shots": [
    {
      "name": "board_bigwin",
      "note": "WHY this shot is shaped this way — carried over from the C# comments. Keep it.",
      "setup": ["RollTargetType LargeCoin"],
      "steps": [
        { "kind": "click", "name": "RollBTN" },
        { "kind": "wait", "seconds": 9, "note": "clears the handler's measured 6.2s worst case" }
      ],
      "settle": { "kind": "all", "parts": [
        { "kind": "present", "name": "RollBTN" },
        { "kind": "absent", "name": "CollectBtn" }
      ] },
      "expectState": "BoardState",
      "settleTimeoutSec": 15
    }
  ]
}
```

**Step kinds** — `click` (`name`, `until?`, `index` 0, `timeout` 6, `retryEvery` 0.6) ·
`hold` (`name`, `seconds`, `index` 0) · `wait` (`seconds`) · `waitFor` (`condition`, `timeout` 8) ·
`cheat` (`command`) · `cheatUntil` (`command`, `until`, `timeout` 30, `retryEvery` 1.5) ·
`timeScale` (`factor`) · `vision` (`prompt`, `timeout` 60). Every kind takes an optional `note`.

**Condition kinds** — `present` / `absent` / `interactable` (`name`) · `textContains` (`name`,
`substring`) · `state` (`name`) · `all` (`parts`, non-empty, nests freely).

**Shot fields** — `name`, `steps` and `settle` are REQUIRED. `settle` has no default on purpose: a
`WaitCondition` is a non-nullable struct, so an omitted one would become `Present("")`, which nothing
ever satisfies — every shot would fail its capture check silently. Optional: `setup` (default `[]`),
`expectState` (default null), `settleTimeoutSec` (8), `arm` (null), `armTimeoutSec` (15),
`parameters` (`[]`), `baseline` (default `"board"`), `resist` (bool, default stay-alive on board).

`baseline` declares where the shot must START — the baseline the game's `IReadyGate` establishes
before the shot runs. `"board"` (the default) is the primary play surface a bare `ready` reaches;
`"lobby"` is the menu/home surface. Only `"lobby"` and `"board"` are accepted — an unknown value is
rejected at load, so a typo can't silently mis-route the shot. The kit only carries the tag to the
gate (via each `UpcomingShots` entry); what a baseline MEANS — how to reach it — is the gate's
business. Declaring it per-shot is what lets a gate stop keeping a parallel lobby/board shot-name list
in sync by hand.

`resist` (optional bool) is the stay-alive write the data-driven ready gate applies on a board
shot. Omitted / `true` runs `adapter.json` `ready.resistOn`. `false` runs `ready.resistOff`
(death takes). Lobby shots ignore it.

## Step times beside the take (kit 0.4.4, Step 4 — 2026-09-08)

The director records WHEN each `steps` entry ran, in seconds since the recorder started, and the
capture agent writes them beside the take as `<shot>_<take>.steps.json` and uploads them with the
clip (the `steps` multipart field; the box lands the file beside the take it keeps):

```json
{ "$schemaVersion": 1, "shot": "board-news-inbox",
  "marks": [ { "i": 0, "kind": "Click", "label": "Click 'NewsBtn'", "atSec": 0.42 },
             { "i": 1, "kind": "WaitFor", "label": "WaitFor present 'InboxPanel'", "atSec": 1.9 },
             { "i": 2, "kind": "settled", "label": "settled", "atSec": 6.55 } ] }
```

A story then starts a beat at a moment BY NAME — `board-news-inbox@click-newsbtn` in the On-screen
cell — and the compiler cuts 0.3 s before that step. The name survives a re-record; a guessed second
does not (on 2026-09-08 every guessed in-point on a re-recorded take moved and the story bounced
three times). The relay's `run-shot` result carries the same marks as `stepMarks`. A take recorded
before 0.4.4 has no sidecar: a named moment on it is refused with the reason, a number still works.

## Ready hygiene (`adapter.json`)

`Library/Nova/adapter.json` can declare footage-hygiene writes the generic ready gate runs
before any shot (invariant 16). Without a `ready` block the gate is a no-op.

```json
{
  "gameId": "sentaur",
  "overlayTypeNames": ["FPS"],
  "ready": {
    "mute": "set BattleSceneManager._backgroundMusic.mute true",
    "muteGet": "BattleSceneManager._backgroundMusic.mute",
    "resistOn": "call Player.ApplyDamageResist 1 0",
    "resistOff": "set Player._damageReductionAmount 0"
  }
}
```

Board shots (and a bare `ready`) apply mute + resist, then force `Time.timeScale = 1`.
A leftover timescale-0 or an unmuted `muteGet` fails `ready`. Lobby-only runs skip mute
and resist — TitleScene has no `BattleSceneManager`. HUD-off is not this block.

Every omitted number takes **the same default the C# factory uses** — the loader builds steps through
`AdStep.Click(...)` etc. rather than deserializing onto fields, so there is one set of defaults, not
two. This matters: Newtonsoft would default a missing `retryEvery` to 0, which re-clicks every pump.

**`note` is the format's reason to exist.** The C# shot lists carry more comment than code (714 of
roguelegend's 1171 lines), and that rationale — why a wait is 9s and not 5, which three earlier
versions failed and how — is the expensive part. A migration that drops it is a regression.

Load errors never throw: a malformed file yields an empty shot list, and the reasons appear in
`ping`'s `shotLoadErrors` — nothing is logged to Unity's console, so `ping` is the only place to look.

## Nova Capture — jobs from the cockpit (v0.4.2)

The hosted pipeline never dials into a studio machine. Instead the kit **pulls** capture jobs over
HTTPS with a **studio key** and records them in this editor:

1. `Tools/Recorder Kit/Nova Capture` → paste the API base URL (hosted: `https://admiral-ads.fly.dev/api`) and the `nova_sk_…` key the platform
   gave you → **Connect** (shows the account the key belongs to). The key lives in `EditorPrefs`,
   per project — never in a file under your repo.
2. Tick **Run capture jobs**. Every 15 s the agent asks `GET /studio/capture-jobs`. A job carries
   a KIND: a `capture` is a list of `{ shot, take }` where `shot` is a NAME in this project's
   `Library/Nova/shots.json`; a `self-test` carries no shot and is described in
   [`TRUST.md`](TRUST.md). A kind this kit has no handler for is refused whole.
3. It claims the run (a 30-minute lease, refreshed by activity), enters Play Mode if needed, runs
   each shot through the normal `AdDirector`, uploads the take (`POST …/clips`, multipart) and
   reports `POST …/done`. Progress is written to `Library/AdRelay/capture-status.json` after every
   item, so a domain reload resumes at the next item instead of re-recording.

What the key can do: only the `/studio/*` routes marked for it (six today), on its own account's
runs. It cannot read
a workspace, list ads, or spend — and a user login cannot call the studio routes. The kit holds no
database URL, storage key or model key. Unknown shot names, failed takes and failed uploads are
reported per item in `done` and shown in the window; the run's clip count is what actually landed
on the server, not what the kit reported.

Requirements: `com.unity.recorder` (recommended — the takes are read from its `Recordings/`
folder; the ScreenCapture fallback works too), and the editor left open. Batch mode never polls.

## Driving it

- Menu: `Tools/Recorder Kit/Run All Shots` (skips vision shots — they need an agent in the loop).
- CommandRelay: drop `{"id","action","args"}` JSON into `<project>/Library/AdRelay/commands/<id>.json`,
  read `Library/AdRelay/results/<id>.json`. Actions: ping, probe-state, play, stop, run-cheat,
  run-shot, screenshot, recompile. Editor liveness + domain reloads: watch `Library/AdRelay/status.json`
  (`bootId` changes = new domain; `compileErrors` = the old domain reporting a failed compile,
  dated by `compileGeneration` — see the rules below).
- From the ProjectNova platform's own tooling, which drives the same files (not part of this
  package).

**Built-in cheats** — available to every game through `run-cheat`, before its adapter adds any of its
own. `help` lists these plus the game's; `raw <cmd>` bypasses them if a game defines a verb of the
same name.

| verb | what it does |
|---|---|
| `singletons` / `dump <Type>` / `get <path>` / `set <path> <v>` / `call <Type>.<M> [args]` | reflection into the live game |
| `ui-dump` | everything on screen worth CLICKING. It cannot see a pure text label — for those, grep the game's localization metadata, which encodes the object's hierarchy path |
| `click <name>[@Label] [i]` | real pointer events |
| `invoke-button <name>[@Label] [i]` | fires `Button.onClick` directly, for widgets synthetic pointer events cannot reach (modal cards, custom raycast setups) |
| `hide-ui <name>[@Label] [i]` / `show-ui` | **footage hygiene**: suppress uGUI dev chrome (settings gears, speed toggles) or any HUD element that CONTRADICTS the ad's claim. IMGUI/`OnGUI` overlays (FPS counters) go in `adapter.json` `overlayTypeNames`, not `hide-ui`. Purely visual, reverts on scene load, and `show-ui` restores from a ledger because a hidden object is no longer findable by name. It HIDES and never rewrites — suppressing a confusing label is framing, editing what it reads would be fabricating game state |
| `content-scan` | enumerate the game's content for parameterized shots |
| `camera-pose [Type] x y z pitch yaw roll fov` | pin the live camera for **static** framings/stills. **Added in v0.3.3.** Hold is `beginCameraRendering` on the posed camera only (`SetRotation` then `SetPosition` then `SetFov`). Needs `adapter.json` `camera.viewType` + `projection` (no `FindFirstObjectByType<Camera>()` fallback). Play Mode required; refuse unless instance count is 1. Not for rolls / pawn tracking. Release before any camera clip (raid, steal, build spline) — an armed hold during a clip is parent∘child garbage, not a blocked clip. `setFov` is refused when `projection` is not `perspective`. |
| `camera-release` | unsubscribe the hold and restore **rotation** from the snapshot (within 0.2°). **Added in v0.3.3.** Position and FOV go back to follow — a pinch FOV latch (e.g. 14) is a pass, not a fail. Idempotent if nothing is armed. |
| `help` / `raw <cmd>` | list verbs / bypass a shadowed built-in |

A selector may contain SPACES (`hide-ui Settings Button`) — names like that are common in Unity, and
the parse rule is shared by `click`, `invoke-button` and `hide-ui`: an arg containing `@` makes the
whole remainder a `Name@Label` selector; otherwise a trailing INTEGER is the index and everything
before it is the name.

Rules the relay enforces (learned live, do not soften):
- `recompile` is refused while the editor is playing — exit Play Mode first.
- **`run-shot`'s timeout abandons the WAIT, not the SHOT.** A shot that outlives the client's timeout
  keeps recording in the editor; the CLI exits non-zero and the file on disk is an unfinalized MP4
  with no `moov` atom, which looks exactly like a corrupted capture. It is not — poll until `ffprobe`
  can read it. A retry meanwhile is refused with `busy: a shot is already running`. Pass
  `--timeout <seconds>` for any shot whose footage runs past ~4 minutes.
- In-flight commands can be dropped by a domain reload; the client times out and re-sends
  under a new id. Commands are never replayed after a reload.
- **`compileErrors` alone never means "your recompile failed."** Between a recompile request and
  the editor actually starting the pass, the previous failure's errors are still on disk under an
  unchanged `bootId`. `compileGeneration` increments at the start of every compile pass: read it
  with `bootId` before requesting, and treat same-`bootId` errors as yours only once the
  generation has advanced. `RelayClient.readDomainBaseline()` + `awaitNewDomain()` do this.

## Vision steps

`AdStep.Vision("prompt")` posts a screenshot + prompt under `Library/AdRelay/vision/requests/` and
waits (async, with timeout) for `Library/AdRelay/vision/responses/<id>.json` — an agent answers
`{"id","match":true|false}`. Shots containing vision steps are semi-interactive; fully
autonomous runs skip them.
