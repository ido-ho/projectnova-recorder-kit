# ProjectNova Recorder Kit

Editor-side ad-capture director + file-based CommandRelay for driving **any** Unity game.
Add the package. `ping` and `screenshot` work immediately. Nothing is required of the game's
runtime code, and nothing is written under `Assets/` or to a `recorder` branch.

Per-game knowledge (shots, optional named cheats) is **data** Nova drops into `Library/Nova/` —
already gitignored by Unity. Command traffic lives in `Library/AdRelay/`.

**What this package can reach on your machine, and what it never does:** [`TRUST.md`](TRUST.md),
beside this file — read it before installing.

## Install

Easiest: in Unity, **Window › Package Manager › + › Add package from git URL**, paste
`https://github.com/ido-ho/projectnova-recorder-kit.git#v0.16.0` and press Add — Unity writes the
manifest line for you. Or add that one line to the game project's `Packages/manifest.json` by hand
(`main` is fine; the kit is Editor-only and does not ship in the player):

```json
"com.projectnova.recorder-kit": "https://github.com/ido-ho/projectnova-recorder-kit.git#v0.16.0"
```

Always pin a tag — `#v<version>`, matching a release on that repository. It is public and holds
only this package, so no credentials and no GitHub account are needed. (Releases start at v0.5.0;
earlier kit versions were never published there. Kit 0.6.0 and 0.7.0 were never published
either: v0.8.0 is the first release carrying what this page marks "kit 0.7.0", and what
`TRUST.md` marks "new in 0.6.0" or "new in 0.7.0".)

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

### Upgrading to 0.16.0 — stays connected while it waits for you, learns your game by itself, cheats wait for your answer

(0.16.0 carries everything in 0.15.0, below — one recompile.)

- **Stays connected during long jobs.** While Unity waits for you to show a moment, exports your game or records, it
  keeps telling the website it is there, so "Make this ad" never says your game is not open when it is. A request
  waiting for you shows on every page of the website ("👋 Waiting for you in Unity").
- **Learns your game by itself after you connect.** Once you tick "Run capture jobs", the kit runs the free editor
  check and then exports your game for the read — only for a project the check proves is this game. The window says so
  under the tick; see `TRUST.md` for exactly what leaves your machine.
- **Cheats wait for one answer.** The export now records which server your editor talks to (your scripting defines and
  a few code patterns). No cheat runs by itself until you answer one question on the website: "Can a cheat run in your
  Unity editor change a real player's data?"
- **Secret-looking values are removed** from the code lines the export sends (keys, tokens, long random strings).
- **A cancelled request says why** in plain words, never a run id.
- **The kit puts your editor's volume back** after a recording that muted it.
- **Optional: keep a copy of the last export** (Details › Export copy, off by default).

### Upgrading to 0.15.0 — takes start on the first press, exact checks, repeat-until, a late button is waited for

(0.15.0 carries everything in 0.14.4, below — one recompile.)

- **A take starts when its first press is on screen**, and **runs as long as its story beat needs** — that length
  survives Unity's Play Mode reload.
- **"Show me" is filmed**: a person's demonstration is recorded, so its footage can be used.
- **Run All Shots is a developer tool**; "Not connected yet" shows only when it is true.
- **Connect** leads with the one next press, names a wrong project first, and pre-fills the hosted API address.
- **Exact checks**: `textEquals` and `textNumber` — "5" never matches "15"; `-5` and `1,000` read right.
- **Repeat until a value**: a `waitFor` may go back to an earlier step, at most 20 times, in the take and in the
  tutorial gate; each step keeps one moment mark.
- **Dials**: a run's per-shot values reach the director; the tutorial gate binds only its own values; instance shots
  (`hero-intro.draco`) load as ordinary shots.
- **A late button is waited for**: a recipe step polls for its own element up to 10 s (closes run meanwhile) before
  any near match.

### Upgrading to 0.14.4 — Unity waits for you: no time limit on "put the game on …"

When a job finds the game on another screen than the one it starts on, the Nova Capture window asks you to put it
there and press Continue. Until 0.14.3 that ask gave up after 5 minutes and every remaining take of the job failed —
while you were away. Now it waits with no time limit, and the window has a **Stop this job** button: the only way the
ask ends unanswered. (0.14.4 carries everything in 0.14.3, below — one recompile.)

### Upgrading to 0.14.3 — the file relay is off until you arm it; cheats reach only your game's code

(0.14.3 carries everything in 0.14.2, below — one recompile. Found by the 2026-09-29 deep review; invariant 192.)

- **The file relay is OFF by default** (`Library/AdRelay/commands/`, the channel an operator's tools use — `admiral relay`,
  an `/onboard-game` session). The website never uses it. To use it, a person at this machine picks **Tools › Recorder Kit ›
  File Relay › Arm for 60 minutes** (or Nova Capture › Details › File relay); it disarms itself after 60 minutes without a
  command, and while it is armed the top of the Nova Capture window says so, with a **Disarm** button. A command dropped
  while it is off is answered "not armed" and never run.
- **A cheat reaches only your game's own code.** A `call` or `set` into the runtime or the engine (`System.*`,
  `UnityEditor.*`, `UnityEngine.*`, … — `call System.IO.File.WriteAllText …`, a bare `Process.Start`) is never ticked and
  never run, by any door — except the few engine members shots use: `set Time.timeScale`, `set AudioListener.volume|pause`,
  and `PlayerPrefs` `SetInt/SetFloat/SetString/Save/HasKey/Get…` (never a delete). Game code that exists only as a
  precompiled DLL is not "your game's code" to this rule (the cheat search never read it either).
- **The relay's `run-cheat` honours your ticks** on a project the website has delivered to — the same gate a local
  `run-shot` runs under. Before, it was exempt.
- **`open-scene` over the relay** refuses while a scene has unsaved changes (it used to discard them) and takes only a scene
  under `Assets/`.
- **The API URL must be `https://`** (plain `http://` only to this machine — localhost). A studio key is never sent over
  plain http, and a redirect is never followed with it.
- **A shot name is a file name:** letters, digits, `.`, `_`, `-`, starting with a letter or digit, at most 80 — the
  website's rule, now the kit's too. A hand-written shot whose name has a space or a `/` is refused by name at load (the
  other shots still load). **A job's run id** that is not a plain token is refused before anything runs.
- **Jobs that write into `Library/Nova` or run in your game** (`sync-nova`, a Try, a cheat search or proof, Teach,
  auto-try, a recipe Try, `export`) now also need THIS project bound to the job's workspace in the kit itself, not only on
  the website.
- A progress file the kit cannot read is set aside as `capture-status.json.corrupt-<time>` instead of being written over;
  a recording take that crashes the agent three times is failed and the job moves on; the static cheat search runs off
  the editor's main thread (no ~20 s freeze).
- **"Allow these N" shows every read it ticks.** A cheat's check read (`get <path>`, `snapshot <root>`, `ui-text`) is its
  own line under that cheat, and a re-send that changes it is "The list changed — look again." A needed command that is
  not your game's code is named in one line instead of left out silently.
- **A value the kit sends is never read through a credential.** A proof's `get` check (and the `get` verb's report) is
  refused when its path names a credential (`AuthToken`, `password`, `apiKey` …) or passes through an object of yours
  that holds one — the rule the snapshot already had; the proof stops before its cheat runs. A text/screen proof's label
  rows leave out what a person typed and credential-named labels (TRUST.md, "What goes up").
- **Pictures stay under `Library/`** even in a project whose relay still lives in a project-root `AdRelay/` (a pre-0.3
  project); earlier kits' leftover frames there are deleted by the next try, and a screen check's JPEG copy is swept too.
- A player-assembly list the editor could not read (mid-compile) is no longer remembered until the next domain reload —
  every game cheat was refused as "not your game's code" until then; relay `presses-*.json` files are kept to the newest 20.
- **Upgrade order:** the website first, then this kit — the website already drops a runtime/engine cheat from every tick
  list it sends, for every kit version.

New in TRUST.md terms: the relay is armed only by a person and disarms itself; its `run-cheat` is gated; `call`/`set` never
leave the game's code; the key goes only over https; run ids and shot names never become paths that leave their folder.

### Upgrading to 0.14.2 — "Allow these N" in one press; Teach finishes itself; a quieter window

(0.14.2 carries everything in 0.14.1, below — one recompile.)

- **"Allow these N" (invariant 190).** A Send now also says which cheats your current work needs — the shots in it, the cheats your current stories name, your go-home lever, and the adapter's own writes — and the top of the Nova Capture window lists them by name ("3 cheats your ads need") with ONE button, **Allow these 3** — every one it would tick is listed, with its exact command under its name. It ticks exactly those — and if a newer Send changed the list since the window showed it, nothing is ticked and the window says "The list changed — look again." — as if you ticked each row (and their check reads with them); a cheat already ticked is not counted. A risky one (a give, a reset…) is ticked and still waits until you tick "this editor talks to a non-production server" — the window says which ones in one sentence. Every row is still there under **Details › All levers**.
- **Teach finishes itself.** After **Stop**, the kit replays the path once to confirm it — no button, in the same editor session only (after an editor restart it waits for your **Replay to confirm**) (Cancel still stops it before anything is pressed) — and uploads it when the replay confirms it (or when the website can confirm it by picture). Only a recipe it could not confirm stops in the window, with one sentence why and **Teach again · Upload anyway · Discard**.
- **A quieter window.** The main view is the ask, the Allow box and one line — "Connected · your game · kit 0.14.2". The key fields, the game pick, the polling toggle, the non-production tick, every lever row, the settle field and the status are under **Details**, in folds this project remembers. Nothing was removed.
- **Upgrade order:** the website first, then this kit. The needed list is never sent to a kit before 0.14.2 (a 0.14.1 or older kit would refuse the whole send); those kits get the list without it and tick row by row as before.

New in TRUST.md terms: one "Allow these N" press ticks the listed fixed commands through the same writer as a row's tick, only when the list on disk is still the one the window showed (risky ones still wait for the non-production tick); a teach's confirming replay starts by itself after Stop, in the same editor session only, and a confirmed recipe is uploaded without a second press.

### Upgrading to 0.14.1 — every run checks the start screen and asks you when the game is elsewhere; Teach from here; popup closes found by what they are

- **The start check.** A game can remember where you left it: a fresh Play may open on the board mid-fight. Before a Try, a recording, a declared start or a teach's Replay presses anything, the kit now checks the game is on the screen its path starts on (the first press and, for a recipe taught with 0.14.1, the names the teach saw on that screen). It first gives the game its usual minute to come up (a screen with nothing to press is still loading — that is a boot, retried as before). When it is up elsewhere: it runs your game's **go-home lever** — one proven cheat you choose on Setup › Show me once (a reset or spend cheat is never offered), run through the lever gate, so it must be ticked here too — at most once per Try, per recording take and per Replay, only when the start is the lobby, and looks again; then the Nova Capture window asks, at the top of the window, **"Put the game on the lobby and press Continue."** (or "the screen you started the teach on" / "the screen this shot starts on"), and the website shows the same line on every page. After 5 minutes the run stops with that sentence — and a recording then fails its remaining takes in that sentence at once, without asking again. It never presses into another screen. A Try that stopped this way never marks a recipe as failed.
- **Teach from here.** Beside Teach there is **Teach from here**: press Play, go to the screen you want to start from, press it — no Play restart. Its Replay is never a restart either: put the game back on that screen (the kit asks if it is not). The website can ask for it too ("the screen the game is on now").
- **Popup closes by what they are.** A press on something that covers the whole screen and is invisible ("tap anywhere to claim"), or a close button inside a popup (a dimmer behind it, or a popup canvas drawn above the main one), is kept as a popup close, not a step — even when the game keeps all its popups under one root that never goes away. A name alone never makes a press a popup close.
- **Upgrade order:** the website first, then this kit — an older website ignores the new press facts and the start names. A teach from here, or a Try of a recipe taught from here, is never sent to a 0.14.0 kit.

New in TRUST.md terms: the go-home lever is one of your ticked cheats, run by the kit at most once per Try, per recording take and per Replay, before it asks you; nothing else is new.

### Upgrading to 0.14.0 — a take that starts on the wrong screen says so; Teach keeps the screen you stopped on

(0.13.5 was never released as a tag: its changes, below, reach you in 0.14.0 — one recompile.)

- **A failed take says the step that failed**, in the director's own words ("Click 'Pet_Tab' … did not resolve"), not the log's closing `FAIL … state='' expected=''`.
- **The shot's first screen, for a Try and a recording alike.** The kit still waits for the shot's screen first (its `arm`, else its settle), then for the shot's own leading `wait` seconds. When the shot's first press is still certainly not on screen, the shot is not run into a press that cannot land: it fails at once — "The game opened on a screen without Pet_Tab (it showed: …) — this shot starts in the lobby. Start the game on its lobby screen and try again." (When your website can send a way back to the lobby, it says "Teach the way back to the lobby once on Setup › Show me once." instead.) A shot whose setup, first cheat, `waitFor`, click-`until`, hold or adapter could get it started anyway is run as before. For a `baseline: lobby` shot, when the website sends your game's taught way back to the lobby (one confirmed recipe made for the lobby), the kit plays it once and tries again; one that cannot be used is set aside with a note, and only a lobby shot that needs it fails.
- **Teach reads the screen you pressed Stop on.** At Stop the kit keeps the names on screen, how long after your last press you stopped, and a picture (`stop.jpg`); the recipe's arrival is read from that screen, so a destination that builds itself a few seconds after the last press (a fight on the game world) now counts. The confirming replay waits the same time after its last step before it looks (at most 20 s) and keeps a picture of where it ended (`replay-stop.jpg`). Stop on the screen you were asked for (if you closed it first, the arrival is read one beat after your last press, as before). Teach pictures are 360 px wide (were 160). Recipes taught with an older kit read exactly as before.
- **Upgrade order:** the website must be updated before (or with) this kit — an older website refuses every 0.14.0 teach (it does not know `stop.jpg`). The release order is: kit tag → website → studios upgrade.

No new lever or read (TRUST.md: the first screen, Teach).

### Upgrading to 0.13.5 — a failed press names what is near it on screen

When a `click` or `hold` fails because its name is not on screen, the director's log (the Console, and a try's log on the website) now adds one line naming up to three on-screen names near it — `near names on screen for 'Tab_Equipment': 'Equip_Tab' (1)`. The rule (`NearName`) is the website's too, held to one shared fixture: names split into words (`Tab_Equipment` → tab, equipment), `equip` matches `equipment` by prefix, and a shared role word such as "tab" alone is never enough. The website offers the same name as a one-press fix ("Did you mean `Equip_Tab`? — Use it and try again") and works on older kits too. Also in 0.13.5: a dump's live-instance line never runs a static `Instance` getter; the forward cache is keyed by (method, forwards left); a call on a nested class (`Ns.Outer+Inner`) is run by its full name. No new lever, read or send (TRUST.md).

### Upgrading to 0.13.4 — "Prove this cheat" waits for your game's server, and knows a set-to cheat

A proof now reads the check again every second after the cheat, for up to 12 s (was one read after 3 s): it stops as soon as the value moved the way the check expects, so a cheat that asks your game's server ("Give …", "Reset user") is seen when the answer lands, and a local one ends in a second or two. A snapshot of your player's data with no value named yet (the first Prove, which finds what changed) always waits the whole 12 s and then reads once more, so a server's late half is never missed. A watched value the cheat ADDS (an entry your data did not hold) counts as moved when it meets the check. A check may also say what a value BECOMES (`expect: "becomes", value: 10000`), for a cheat that sets a value rather than adding to it: its second run moves nothing, so "goes up" can never pass twice. "becomes 10000" is proven only when the value MOVED onto 10000 — a value already at 10000 before the cheat proves nothing (a cheat that does nothing would pass), so lower it first (for example with a reset cheat) and Prove again. The website offers it beside "goes up" when a give cheat's value lands on a round number. No new lever, read or send; the reads are the same ticked, field-only, bounded reads as 0.13.2 (TRUST.md).

### Upgrading to 0.13.3 — the Nova Capture window scrolls, and finds a lever

The window now scrolls, and "Levers the cloud may use" has a **Find** box: type any part of a command (for example `topaz`) to list only the rows that hold it. Ticked rows always stay listed. Nothing else changed; no new lever, read or send.

### Upgrading to 0.13.2 — prove a cheat without typing where its effect lives

0.13.2 changes "Prove this cheat": a cheat with no check chosen is proven by a SNAPSHOT of your player's data. "Search
for cheats" now also notes where that data lives (a static member of your code holding a data model — found from types,
nothing read), and the next "Send to my editor" puts `snapshot <that root>` on each such cheat's row, so ticking the
cheat ticks the read with it. The proof then lists what changed ("it changed: Currencies[topaz].Amount 50 → 10,050"),
and one press makes that value the cheat's check: a snapshot that WATCHES that one path, so the next Prove runs under
the same ticked read — no new tick — and is graded by that value alone. A `get` path can now address a dictionary entry
or a list item: `Save.Currencies.Currencies[topaz].Amount`, `Save.Levels[3].DidClear`, `Save.Levels.Count` (the key is
written bare, never quoted). A key that looks like an identifier (a uid, a guid, a long number) is sent as a salted
short hash (`[#1a2b3c4d5e6f]`) whose salt stays in `Library/AdRelay/` — per machine, so a hashed check made on one
editor does not match on a teammate's. No read runs a static property's body (a path through `GameManager.Instance`
uses the manager that already exists, or is refused); the snapshot reads fields only, never opens a collection of your
own code (or a runtime wrapper around one), and skips any object holding a token, password, PIN or similar field.
Reading a static field can run a class's static constructor the first time, as the game's own first use would. Press
"Search for cheats" once after
upgrading. Exactly what the snapshot runs, reads and sends is in `TRUST.md` ("Prove this cheat").

### Upgrading to 0.13.1 — cheats registered through a forwarder are proposed

0.13.1 changes one thing in "Search for cheats": when your code registers its debug actions on a class that only hands
the name on to another class (a global `DevConsole.RegisterAction` forwarding to a namespaced registry), the search now
follows that forward and proposes `call <Registry>.Invoke "<name>"` through the registry's own by-name method, instead
of listing the name with "no executor found". It still runs nothing until you tick the exact command (see `TRUST.md`).

### Upgrading to 0.13.0 — declared starts, the popup loop, auto-try and the recipe Try

0.13.0 adds two jobs the website can ask this editor to run, `auto-try` and `recipe-try`, and lets a Try or a recording
START from a recipe. It changes nothing about what 0.12.0 lets through (full detail in `TRUST.md`):

- **A declared start.** A Try or a recording may name a recipe to start from. The kit first plays that recipe's chain —
  the recipe it starts from, back to the lobby after boot — pressing only the names the recipes hold, through the same
  press guard every kit job uses. Nothing is recorded while it plays. A job that names a start AND carries a tutorial
  gate is refused whole; a job that names no start runs the tutorial gate as before.
- **The popup loop.** Between every step of a start, the kit taps whichever known close (the ones a teach saw) is on
  screen, until two quiet passes (at most 12 taps each time), each through the press guard; a recording gets one such
  pass before the recorder starts and none while it records. A close the guard refuses (a risky word) is left alone,
  and a button of the screen the start arrives at is never pressed as a close.
- **Levers are asked before a take.** A recording behind a declared start runs under the forced lever gate, so every
  cheat it needs (the shot's setup, the start's, the reset) must be ticked before the take runs — otherwise the take is
  refused by name. One attempt per take.
- **Unknown overlay = stop and ask.** If, after a step of a start, something is on screen that the teach never saw and
  no known close removed, the run STOPS and says so — it never guesses (it could be a spend screen). On a game whose
  panels all sit under one wrapper object the kit cannot see this, and says so (`screensBlind`).
- **A close that changed a watched value is flagged.** When a popup close changes a value one of your ticked cheats
  checks (a daily reward, a free claim), it is reported, never hidden and never a stop.
- **The restart rule between takes.** A recording from a start resets inside one Play session when it can (a proven
  reset cheat the website names); otherwise it restarts Play after the settle wait (30 s by default). A start that never
  came up is a boot failure, retried at most twice, never counted as a failed replay.
- **Auto-try** (`auto-try`) presses at most 6 buttons the website chose from READ's names for one screen, at most 3
  tries within 300 s, and reports which export names appeared after each press — stopping at the first press that
  changes the screen without arriving. No screen check is asked.
- **Recipe Try** (`recipe-try`) plays a recipe's chain once and reports whether each recipe arrived. The website asks
  for one before every recording that names a start; a Try that does not arrive stops the recording and asks for a
  short re-teach — never an AI repair.
- **The edge log.** Every press the kit makes in these jobs is reported as the screen before, the press, the screen
  after (at most 200 per job and 48 KB of text, the rest counted) — for the website's log, nothing else.

### Upgrading to 0.12.0 — "Show me once" (Teach)

0.12.0 adds one job the website can ask this editor to run, `teach`, and changes nothing about what 0.11.0 lets
through (full detail in `TRUST.md`):

- When the website asks to be shown a screen the kit could not reach by itself, the request appears in the Nova
  Capture window with a **Teach** button. Nothing happens in your game until someone presses it.
- **Teach** starts a fresh Play session (after a settle wait — 30 s by default, a field in the window — because some
  native plugins crash on fast Play restarts), and records what each press FIRES — the button Unity sends the click to,
  gestures on the game world, a cheat run through the kit — only until **Stop**. After each press it keeps the names on
  screen and a small picture.
- After Stop, **Replay to confirm** replays the path ONCE in another fresh Play session — only when pressed — pressing
  only the names the recording holds, through the same press guard every kit job uses, to check it arrives the same
  way. **Discard without replay** ends it with nothing pressed. **Cancel** stops a start before anything is pressed.
- A teach never enters Play Mode on its own: after an editor restart (or a start that stalls) it goes back to waiting
  for Teach, or to the review for Replay. Every Play Mode exit — yours, the game's, the kit's — starts the settle wait.
- The recipe is SHOWN in the window before anything is sent: confirmed or not and why, the steps, the popup closes,
  where it arrives. **Upload** sends it; **Discard** sends only that it was discarded; **Not now** ends the request.

### Upgrading to 0.11.0 — the deep cheat search

0.11.0 adds two jobs the website can ask this editor to run, and changes nothing about what 0.10.0 lets through
(full detail in `TRUST.md`):

- **"Search for cheats"** (`cheat-search`) reads your game's compiled code for cheats — known debug consoles' command
  attributes, `Register…("…")` call sites, and methods of classes named Debug / Cheat / Dev / Test / GM — without
  running any of it. Reading the RUNNING game's own cheat list is one more tick in the Nova Capture window,
  `cheat-registry`; until it is ticked, the search never presses Play.
- **"Prove this cheat"** (`cheat-proof`) runs ONE cheat you ticked, once, and reads the value the website chose to
  check before and after. The read is its own tick (`get <path>`, `snapshot <root>`, or `ui-text` for a label check —
ticked with the cheat when you tick the cheat's row). A read that fails before the cheat stops the proof: the cheat is
not run.
- Two bridge verbs: `ui-text` (every label on screen, not only buttons) and `snapshot <Type>.<member>` (every number
  under one root of your data). Delivered in a shot, each needs a tick like any other command.

### Upgrading to 0.10.0 — what changes for a delivered project

0.10.0 changes what the lever gate lets through, so read this before bumping the pin (full detail in `TRUST.md`):

- **Only fixed commands are approved.** A tick covers exactly the command you ticked, arguments and all. A template
  ticked by an older kit (`SelectHero {hero}` in `Library/Nova/levers.json`) no longer covers `SelectHero Knight`:
  the refusal and the Nova Capture window say so and name the template — tick each fixed command instead.
  (One exception since Fix 4, released in 0.14.2: a `set Type.Member {v}` row the deep cheat search found on your dev
  UI can be ticked, and lets any one value of that member through — see `TRUST.md`.)
- **Risky cheats wait for one more tick**, "this editor talks to a non-production server", at the top of the same
  window: a ticked cheat whose words touch money, deleting, resetting, the account or the server (or that the
  website's tick list calls risky) is refused until it is ticked. That includes a `camera-spec …` lever whose
  method names read that way, and a recording that runs your `tutorial-gate` is refused whole, up front.
- **Takes are written under `Library/Nova/Recordings/`**, not a project-root `Recordings/`; ProjectNova's readers
  look in both, the new folder first.

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
`hold` (`name`, `seconds`, `index` 0) · `wait` (`seconds`) · `waitFor` (`condition`, `timeout` 8; optional `repeatFrom` + `times` — when the condition does not hold, run again from that EARLIER step, at most `times` (1–20) times: "roll, wait for the move, until the level shows 5") ·
`cheat` (`command`) · `cheatUntil` (`command`, `until`, `timeout` 30, `retryEvery` 1.5) ·
`timeScale` (`factor`) · `vision` (`prompt`, `timeout` 60). Every kind takes an optional `note`.

**Condition kinds** — `present` / `absent` / `interactable` (`name`) · `textContains` (`name`,
`substring`) · `textEquals` (`name`, `text` — the whole text, trimmed, any case) · `textNumber` (`name`,
`value` — the FIRST number the text shows equals it: "Level 5" and "Lv.5" hold 5; "Level 15" and "Floor 3/5" do not; "-5" is minus 5 and "1,000" is 1000;
use it, never `textContains`, for "until the level is 5") · `state` (`name`) · `all` (`parts`, non-empty;
nests up to 8 deep, and one condition holds at most 64 parts, counting the parts of every `all` inside it).

**Shot fields** — `name`, `steps` and `settle` are REQUIRED. `settle` has no default on purpose: a
`WaitCondition` is a non-nullable struct, so an omitted one would become `Present("")`, which nothing
ever satisfies — every shot would fail its capture check silently. Optional: `setup` (default `[]`),
`expectState` (default null), `settleTimeoutSec` (8), `arm` (null), `armTimeoutSec` (15),
`parameters` (`[]`), `baseline` (default `"board"`), `resist` (bool, default stay-alive on board).
Every number of seconds — `timeout`, `retryEvery`, `seconds`, `settleTimeoutSec`, `armTimeoutSec` — must be finite, at
least 0.1 and at most 600; a shot name at most 80 characters; a shot at most 500 `setup` commands and 500 steps: the loader
refuses the shot otherwise, by name, and so does the delivery check before it writes a file the website sent.

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

## Shots sent from the website, and lever approval (kit 0.7.0)

You can author a game's shots on the ProjectNova website and press **Send to my editor**. The kit
claims that job (`sync-nova`), writes the two files under `Library/Nova/` — `shots.json` and
`adapter.json`, byte-for-byte as they were sent — and answers with what its own
`JsonShotLoader` made of them: the shot names in order. Nothing enters Play Mode and nothing runs.
A file that loader would refuse any part of — or an `adapter.json` the ready gate would fail on — is
not written at all: the send is refused by name, with the loader's own first error, and nothing on
disk changes. A file you edited yourself is copied to `<name>.local-<utc>.bak` before it is
replaced, and the answer says so. `Library/Nova/synced.json` records what the last send wrote, and
every pair of SHA-256s the cloud has ever delivered here (the newest 20 of each) — that history is
what the lever gate reads.

**A delivered shot's writes need one tick, per lever.** A `cheat` (or a `setup` entry, or the
`ready` block's `mute` / `resistOn` / `resistOff`) reaches your game only when it is ticked in
**Tools > Recorder Kit > Nova Capture > "Levers the cloud may use"**, which lists exactly the
levers the files on disk ask for and writes `Library/Nova/levers.json`. An unticked one fails its
step with `lever not approved on this machine: '<command>'` in the run log. Three levers are not
cheat commands: `timeScale` (any shot with a `timeScale` step, whatever the factor),
`hide-overlay <TypeName>` (one per `adapter.json` `overlayTypeNames` entry) and
`camera-spec <viewType> <setPosition> <setRotation> <setFov>` (the `adapter.json` `camera` block,
which decides which three methods a `camera-pose` step calls and, when the step names no Type of
its own, which Component it moves — `*` stands in for an absent view type, and a block holding
nothing but the defaults needs no tick). On a delivered project a step that names its own Type is
REFUSED unless that Type is the block's `viewType`, case and all (when the block names one).
An unticked overlay is left VISIBLE, with `overlay '<T>' left visible — lever not approved on this
machine: hide-overlay <T>` in the log — the run continues. An unticked `camera-spec` REFUSES the
pose rather than posing with the kit's defaults: a shot framed by the wrong camera is a wrong
recording.

Eight things are worth knowing:

- The gate is live while **either** `shots.json` or `adapter.json` is a file the cloud has sent to
  this project (`synced.json` remembers the last 20 of each). Files you wrote or edited on this
  machine are never gated — and taking a delivered project back means editing or deleting **both**
  files, not one: an edit to `shots.json` alone leaves the cloud's `adapter.json` in place, and its
  `ready` block is a write into your game.
- The kit's own three fixed verbs pass without a tick: exactly `ui-dump`, `show-ui` and `hide-ui`.
  (`hide-ui <name>` has an argument and is not one of them.) A `get` needs a tick like any lever
  since the fourteenth audit (2026-09-22) — resolving its path runs your property getters, which is
  code — and so does `adapter.json`'s `ready.muteGet`, listed as the command the ready gate sends,
  `get <muteGet>`.
- The relay's own `run-cheat` goes through the same gate since 0.14.3 (it was exempt before: "typed by
  whoever is at this keyboard" — but anything that could write the commands folder was that person). While the
  gate is live, an operator's `run-cheat` runs only a ticked command, and a `camera-pose` also needs its
  `camera-spec …` lever. The relay's `run-shot` and `ready` run through the director and are gated like anything
  else. And whatever the gate says, a `call`/`set` reaches only the game's own code (see "Upgrading to 0.14.3").
- **What is NOT gated:** a `click` or `hold` step. A delivered shot can press any uGUI element in
  your game by name without a tick — the same reach a person driving the relay has. Gate what a
  press can do inside the game the way you would for a playtester, not with this list.
- The window lists more than the two files ask for: every other entry of your `levers.json` (so a
  tick you no longer want can be taken back even after the command has left the files), and any
  command the gate refused in this editor session that no file names ("refused in this editor
  session; no file on disk asks for it now") — which is how a game adapter you compiled yourself
  gets its own commands in front of you instead of failing with nothing to tick. That note is true
  of the FILES: a command your own compiled adapter runs is not in them, and the window says so
  (ticked, its row says "not needed by the files on disk"). A refused BINDING of a template the files
  list (`SelectHero Knight` for `SelectHero {hero}`) is not a row of its own: the template's row names
  it. The exception is a binding that a tick of that template would still refuse (a dotted value in
  the target): it is its own row, and says so. The binder and the gate read one value grammar, so a value
  ending in a line break is refused when it is bound.
- Each checkbox answers for its row's own shape (placeholder names aside, the same placeholders
  sharing a name), by the rule the gate runs. A row a ticked TEMPLATE covers — `SelectHero Knight` under a ticked `SelectHero {hero}`, or
  the files' `SelectHero {hero}` under a ticked `SelectHero {x}` (the same template with its
  parameter renamed) — is shown ticked, names the template ("approved by the ticked template
  `SelectHero {x}` — untick that to revoke"), and its own box is disabled: it is not in
  `levers.json`, so unticking it could revoke nothing. The template's own row names what it covers
  ("approves `SelectHero {hero}`, which the files ask for"). One of the kit's fixed verbs (`ui-dump`,
  `show-ui`, `hide-ui`) is shown ticked, "a read — needs no tick"; a `get` is an ordinary row.
- One thing a box cannot show whole: a row another ticked entry PARTLY covers. Ticked
  `set Player.{a} {b}` lets the files' `set Player.coins {v}` through, a ticked `SelectHero K{x}`
  some of the files' `SelectHero {hero}`, and a ticked `SelectHero Knight` one of them — but none is
  that row's shape, so the row stays unticked. Ticking this row approves every command of this form
  (except one whose value in a target holds a `.`), whatever other entries let through. What the rows
  say is what a witness finds, not every case: a ticked template's row never claims the files don't
  need it ("a template — approves any command that fits it, except one whose value in a target holds
  a '.'; not asked for in this exact form"); a needed row names the first ticked entry, in
  `levers.json`'s order, for which a witness shows that entry letting through a command a tick of the
  needed row would approve too — so a row the window will not let you tick names none. The witness is
  not a proof, and an overlap it misses is not named. A ticked literal a template on disk can be bound
  to (`SelectHero Knight`) says "not asked for in this form by the files; it approves only this
  command, as written"; a literal no template on disk can be bound to says "not needed by the files
  on disk" (a match that runs out of time counts as none).
- Installing only the package gives you the GENERIC adapter, whose overlay list comes from
  `adapter.json` — so on a delivered project every one of its overlay names is gated, including a
  name from an earlier `adapter.json`. Only an adapter you compiled carries names of your own.

Approving a command WITH placeholders approves the shape: `SelectHero {hero}` allows
`SelectHero Knight` and refuses `SelectHero Knight Extra` or `SelectHero Button@Label` (the same
value rule as `parameters` binding). A name used twice is one value: `set Player.{a} {a}` allows
`set Player.coins coins`, never `set Player.coins 999`. Five kinds of lever cannot be ticked at all:

1. A command whose FIRST word is not literal — it holds a placeholder (`{a} {b}`, `s{a} {b} {c}`),
   so ticked once it would approve anything of that shape, or a quote (`" {a}" {b}`), which the game
   reads as whatever the quotes enclose: a placeholder verb in disguise.
2. A command whose TARGET is not literal. `set`, `call`, `raw`, `click`, `invoke-button` and
   `hide-ui` name what they act on in the word after the verb; that word may not begin with a
   placeholder or a quote (`set {a} {b}`, `call {t}.Run`, `set "{a}" 1`), and a placeholder in it
   must be a whole name between dots — `set Player.{stat} {v}` is fine, `call S{a}` and
   `set Player.x{a} 1` are not — whose bound value holds no `.` (`set Player.{stat} {v}` approves
   `set Player.coins 0`, never `set Player.Instance.save.coins 0`). `camera-pose` with eight
   arguments names the Type it poses in its first one, which may hold no placeholder
   (`camera-pose {t} 1 2 3 4 5 6 60`), and a `camera-pose` may hold no quote anywhere: the game
   splits a command at its quotes, so `camera-pose {t} 1 2 3 4 5 "6"60` is eight arguments with
   `{t}` as the Type. A quote in a later argument of any other verb is fine
   (`call Dialog.Say "Some Arg"`).
3. A command with two placeholders and nothing between them but characters a value may hold —
   letters, digits, `.`, `_` and `-` (`{a}{b}`, `{w}x{h}`, `{a}.{b}`, `{a}_{b}`, `{a}-{b}`): nothing
   says where one value ends and the next begins, so a command cannot be read back against it. The
   text between two placeholders must hold a character a value cannot hold (`{a} {b}`, `{a}:{b}`).
4. In a command with a placeholder, a `{` or `}` that is not part of one (`SelectHero {{h}}`,
   `set Player.{a}} 1`): a value bound inside such braces makes a command that reads as a
   placeholder itself (`h=knight` gives `SelectHero {knight}`).
5. A `hide-overlay …` or `camera-spec …` lever holding `{` or `}`, which names nothing a C# type or
   method can be called.

The first four are IGNORED even if one is already in the file. The fifth, if it got into the file
by hand, still approves only its own exact text — a type name that cannot exist — because
`hide-overlay …` and `camera-spec …` levers are never templates at all. Any of the five can be
unticked. A placeholder anywhere else is an ordinary approval. A missing or malformed `levers.json` approves nothing — and a `synced.json` this kit cannot
read gates everything rather than nothing, which includes one that parses and names no sha at all
(`{}`, a history that is not a list, or a list with no string in it).

**Known limits, said plainly.**

- This kit reads JSON with Newtonsoft, which is LENIENT: `//` comments, trailing commas and single
  quotes load here. The website's parser is strict and refuses all three. So a `shots.json` or
  `adapter.json` you hand-edit can work perfectly in your editor and be refused when the site reads
  it back — if the site says a file is not JSON and the kit loaded it, that is the difference.
- `click` and `hold` are not levers (above). Neither is a `vision` step's prompt, which is written
  to `Library/AdRelay/vision/requests/` and read by a person (during a try it is asked of the
  website instead: see "Screen checks during a try" below).

## Try this shot (the `probe` job, kit 0.8.0)

On the website, **Try this shot** asks this editor to run ONE shot and say what happened. The kit
claims that job (`probe`). The shot, and the `adapter.json` text it was written against, ride the
claim with their SHA-256s, and both are checked against the bytes that arrived before either is
used. The shot is read by the same `JsonShotLoader` as your own `shots.json` — a shot it refuses is
refused with the loader's own sentence — and then run once, in Play Mode, through the normal
director: its `setup`, the ready gate, recovery and the wait for the shot's screen behave exactly
as for a capture of that shot. One attempt, never a retry.

**A second shot, run first: your `tutorial-gate`.** A try of any other shot may carry one more shot
— your pack's `tutorial-gate` text, with its SHA-256, checked against the bytes that arrived before
it is used (the website sends it while that shot's newest try reached its end on the text your pack
holds). It runs BEFORE the shot tried, in the same Play session, with recording off, under the same
lever gate — its levers are checked with the shot's and the adapter's before anything runs — and a
stop in it ends the try there: the shot tried never runs. The ready gate runs for it first, then
again for the shot. A `tutorial-gate` that holds a screen check (a `vision` step) is refused whole.
The answer says whether it ran (`gateRan`), which of its steps stopped it (`gateFailedStep`) and the
hash of the gate text that arrived (`gateSha256`).

What it does NOT do:

- **It records nothing.** No take, and no file under any recorder's folder: the probe's recorder
  is a no-op whose folder is never created.
- **It writes no shots file.** Not `Library/Nova/shots.json`, not `adapter.json`, not
  `synced.json`: the shot runs from memory, so what this machine records later is exactly what it
  was before. (The adapter text that arrives with it lists the levers it needs; the shot runs
  against THIS project's `Library/Nova/adapter.json` — so it runs only when that file is
  byte-for-byte the text that arrived. Otherwise the job is refused before Play Mode: "press Send
  to my editor, then try again".)
- **It runs only levers you ticked** — even on a project whose own files are ungated. Before it
  starts, the kit lists every lever the shot and its adapter need (the same list the website
  computes) and compares it with `levers.json`; if one is not ticked, the shot is not run at all
  and the answer names it (`leverRefused`). The run itself is gated as cloud content whatever your
  files say, so nothing un-ticked reaches your game half-way through either. Read-only commands
  and `click`/`hold` are exempt, as they are for any delivered shot.

What goes back (`POST …/result`, the Doctor's door): whether the shot reached the end (every step
ran and its `settle` held); which step stopped it — its index in the shot's `steps` and its kind —
and the director's own sentence; the names on screen at that moment (the same rows `ui-dump`
prints, at most 200, each cut to 120 characters); the Game view's render size and `Time.timeScale`; the
director's log (its last 200 lines, 300 characters each); the levers needed and ticked; the two
SHA-256s of the texts that arrived; and ONE frame of the Game view, taken at the stop, before
anything is restored — except on a run that ABORTED or was stopped from outside: there the names are
still read before the restore, but the frame is written at the end of that frame, after the
director has put back the overlays, the time scale and the camera. Facts only: whether the shot
needs changing is decided on the website.

### Screen checks during a try (kit 0.8.0)

A `vision` step ("does the screen match this prompt?") in a TRY is asked of the website, not of
`Library/AdRelay/vision/requests/` (`HttpVisionChannel`, chosen by `ProbeRun.DirectorOptions` only
when the tried shot has a `vision` step; a capture keeps the file channel). For each one the kit
takes ONE frame of the Game view the way the try takes its own (`ScreenCapture`, a PNG under
`Library/AdRelay/`, deleted when the check ends) and posts `POST …/capture-jobs/:runId/vision` as
the claimed run, with the same headers as every other job call: multipart `frame`, `step` (the
0-based index of the step in the shot's `steps`) and `requestId`. The prompt is not sent: the site
reads it from the shot it sent. A frame goes up only when it is under 8 MiB (the website refuses one
that reaches it): a frame of 8 MiB or more is re-encoded as a JPEG (quality 85) and halved until it
is under (at most 6 times), or the step fails by name and nothing is asked.

How the step ends:

| The website… | The step | Its reason (`FailedReason`, the try's `reason`) |
|---|---|---|
| answers `{ requestId, step, match: true }` | passes | — |
| answers `match: false` | fails | `… did not resolve — the screen check answered: the screen did not match` |
| refuses (a 4xx, e.g. 409 "this try has used its 1 screen check …") | fails, not asked again | `… — the site refused the screen check at step N (409): <its message>` |
| answers a 5xx / 408 / 429, or nothing | asked again with the SAME `requestId`, up to 4 times (1, 2, 4 s apart) | then `… — the site did not answer the screen check at step N after 4 attempts with the same request id (last: …)` |
| answers a 200 that does not echo this question's id and step, or has no true/false `match` | fails | `… — the site's answer to the screen check at step N … is not a verdict …` |
| says nothing before the step's `timeout` | fails | `… — no answer to the screen check within the step's 60s timeout` |

Only a real answer of no says "did not match". A new question gets a new `requestId`, and so does
the same step asked by a fresh run (a domain reload re-runs the whole shot with a new frame). The
website refuses a new id for a step this try already asked, by name ("step N was already asked on
this try — a re-run does not ask it again"): nothing is billed for it, and the step fails with that
reason (the website's rule).

### A recording runs your `tutorial-gate` first too (kit 0.8.0)

A recording (`capture`) may carry the same shot a try runs first: your pack's `tutorial-gate`, its
text and SHA-256 on the recording's claim (`CaptureGateClaim`). The website decides that once per
recording, by the rule a try's gate is decided by. It runs before EVERY take, in the same director:
its hash is checked against the bytes that arrived before it is used (`CaptureGatePlan.Prepare`, the
reader a try's gate goes through), it runs with recording OFF, and the take runs under the lever gate
a try runs under — so before each take every lever it needs (the gate's, the shot's and the
adapter's) is checked against `levers.json`, and one that is not ticked refuses that take before
anything runs. A lever no file on disk lists — one a shot compiled into your game needs — is then
listed in the Nova Capture window as refused in this session, so it can be ticked there. A stop in
the gate fails that take; no clip is made. The website hands such a recording only to a kit of 0.8.0
or newer in a Unity project bound to its workspace: any other editor that claims it is refused by
name. Once a recording is under way its gate is decided, and if its editor goes away the website does
not list it to such an editor: each poll reads on past the runs that editor cannot take, through at
most the 50 oldest runs waiting, so such a recording stands in front of the recordings behind it only
when 50 or more runs that editor cannot take are waiting ahead of them; a recording not yet started
is listed, and the first such claim fails it with the reason. Because
every take runs the gate, write each of its steps with an `until` that already holds when the
tutorial is not showing, so it does nothing then.

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
A leftover timescale-0 or an unmuted `muteGet` fails `ready`. On a project the cloud delivered to,
each of the four is a lever — `muteGet` as the read the gate sends, `get <muteGet>` — and an un-ticked
`muteGet` fails `ready` saying the read was not ticked, not that the bed is unmuted. Lobby-only runs
skip mute and resist — TitleScene has no `BattleSceneManager`. HUD-off is not this block.

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
   per project — never in a file under your repo. Then pick the **Workspace** — which of your
   account's games THIS Unity project is. One key is often shared by several projects, so the kit
   never guesses: recording works unpicked, but "Learn my game" is only ever sent to a project that
   has said which game it is, and a picked project only sees that game's jobs.
2. Tick **Run capture jobs**. Every 15 s the agent asks `GET /studio/capture-jobs`. A job carries
   a KIND: a `capture` is a list of `{ shot, take }` where `shot` is a NAME in this project's
   `Library/Nova/shots.json`; a `self-test` carries no shot; an `export` ("Learn my game") reads
   the project in Edit Mode and uploads an inventory, facts and art in zip parts; a `sync-nova`
   ("Send to my editor") writes `Library/Nova/shots.json` + `adapter.json` from the website and
   reports what loaded (see above); a `probe` ("Try this shot") runs ONE shot the website sent,
   records nothing and writes no shots file, and reports what it did (see above). All are described in [`TRUST.md`](TRUST.md) — read the export
   section before an owner presses that button. A kind this kit has no handler for is refused
   whole.
3. It claims the run (a 30-minute lease, refreshed by activity), enters Play Mode if needed, runs
   each shot through the normal `AdDirector`, uploads the take (`POST …/clips`, multipart) and
   reports `POST …/done`. Progress is written to `Library/AdRelay/capture-status.json` after every
   item, so a domain reload resumes at the next item instead of re-recording.

What the key can do: only the `/studio/*` routes marked for it (nine today), on its own account's
runs. It cannot read
a workspace, list ads, or spend — and a user login cannot call the studio routes. The kit holds no
database URL, storage key or model key. Unknown shot names, failed takes and failed uploads are
reported per item in `done` and shown in the window; the run's clip count is what actually landed
on the server, not what the kit reported.

Requirements: `com.unity.recorder` (recommended — the takes are read from
`Library/Nova/Recordings/` — a project-root `Recordings/` before the v3 P2 kit; the ScreenCapture fallback works too), and the editor left open. Batch mode never polls.

## Driving it

- Menu: `Tools/Recorder Kit/Run All Shots` (skips vision shots — they need an agent in the loop).
- CommandRelay (**off until armed** — Tools › Recorder Kit › File Relay › Arm for 60 minutes; see "Upgrading to
  0.14.3"): drop `{"id","action","args"}` JSON into `<project>/Library/AdRelay/commands/<id>.json`,
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
- **It runs nothing until a person arms it** in this editor, and disarms itself 60 minutes after its last command; a
  command dropped while it is off is answered `the file relay is not armed …` — arm it and re-send under a new id.
- `open-scene` is refused while any open scene has unsaved changes, and takes only `Assets/…/Name.unity`.
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
`{"id","match":true|false}`. Only a JSON `true` or `false` is an answer: a response whose `match` is
missing or anything else fails the step with `the response file has no true/false match`, never
"did not match". (During a "Try this shot" the website answers instead: see "Screen
checks during a try" above.) **Changed in 0.8.0:** `IVisionChannel.Request(prompt, stepIndex)` now
takes the step's 0-based index in the shot's `steps` — a custom channel passed as
`AdDirector.Options.Vision` must add the parameter (it may ignore it, as `FileVisionChannel` does).
A channel that can end a question without a verdict also implements `IVisionOutcome`. Shots containing vision steps are semi-interactive; fully
autonomous runs skip them.
