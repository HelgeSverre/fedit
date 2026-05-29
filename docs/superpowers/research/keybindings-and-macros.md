# Configurable Keybindings & Macros — Research

> Status: research + recommendation. Companion to the prior audit in
> [`docs/wip-keybinds.md`](../../wip-keybinds.md), which maps where every
> chord currently lives. This doc adds the cross-tool survey (terminals,
> editors, IDEs), the macro-system landscape, the config-format / F#-DSL
> analysis, and a concrete recommended design.
>
> Decisions framed into this doc: target a **full remap layer** (unified
> action vocabulary, per-context maps, multi-key sequences, expanded key
> universe), **sketch macros but defer** implementation, and **recommend a
> config surface freely**.

---

## TL;DR

The single insight that ties everything together: **both configurable
keybindings and macros need the same missing substrate — one named,
addressable `Action` vocabulary with a single dispatcher.** fedit doesn't
have that yet; bindable behavior is scattered across three places. Build
that registry once and both features fall out of it.

Recommended design, in five pillars:

1. **One recursive `Action` DU** naming every bindable operation, with a
   `Chain of Action list` case (macros and multi-action bindings come for
   free), dispatched by a single `runAction : Action -> Model -> Model *
Effect list`. Collapses today's three scattered dispatch sites.
2. **A richer key model**: a `Chord` (modifiers + key) over an expanded key
   universe (Ctrl+Shift, F-keys, any Ctrl+letter), and a `KeyStroke = Chord
list` for sequences (`ctrl-k ctrl-c`). Pending-prefix state in the Model
   with a configurable timeout, shown in the status bar.
3. **Per-context keymaps** keyed by a _closed enum_ (`Editor | Sidebar |
Prompt | Global`), resolved with Zed's two-tier rule (context
   specificity, then load order so user config overrides defaults), with
   `null`/`unbind` to drop a default.
4. **Config format**: a type-safe **internal F# DSL for the compiled-in
   defaults** (`ctrl 's' ==> Save`) mirrored 1:1 by a **tiny Ghostty-style
   one-line-per-binding user file** parsed with `Split` + active patterns —
   zero new dependencies, ~0 startup cost, hot-reloadable, AOT-safe.
5. **Macros (deferred)**: because `Action` is recursive and the MVU loop is
   already an event stream, record/replay is near-free later — record raw
   `KeyInput` into a register, replay by re-enqueuing into the existing tick
   queue. Design the registry and input path now so it drops in cleanly.

Most interesting alternative: make keymaps a facet of the **existing
compiled-plugin pipeline**, so an F#-capable user's binding can run
arbitrary type-checked editor logic (real macros as functions). Keep it as
the power-user tier, not the default surface.

---

## 1. Current state of fedit

(Condensed from the audit; see [`docs/wip-keybinds.md`](../../wip-keybinds.md)
for the full chord-by-chord matrix.)

### 1.1 The core problem: no unified action abstraction

Bindable behavior lives in **three disconnected places**, none of which
shares a vocabulary:

| Where                        | File                        | What it binds                                                                                                          |
| ---------------------------- | --------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| `Command` DU                 | `Commands.fs:14-42`         | Only the typed prompt verbs (`:write`, `:theme`, `:open`, goto…). Parsed from text; never reached by a chord directly. |
| Global `Ctrl+letter` handler | `Editor.fs:1219-1324`       | Inline transitions: `Ctrl+Z` undo, `Ctrl+C` copy, the three-state `Ctrl+B`, buffer jumps. Hard-coded `match key with`. |
| `runEditor` / `runSidebar`   | `Editor.fs:839-917`, `770+` | Inline motions/edits/selection per focus. Hard-coded `match key with`.                                                 |

So "save" exists as `Command.Write` (prompt-reachable) _and_ as inline logic
behind `Ctrl+S` (`Editor.fs:1294`, calls `saveActiveBuffer`). There is no
single name a keymap could point at. **Step zero of any remap system is to
unify these into one `Action` type and one dispatcher.**

### 1.2 The key model is narrow and lossy

- `KeyInput` (`Primitives.fs:23-55`) is a closed DU. There is **no
  `Ctrl+Shift+X`, no F-keys, and no arbitrary `Ctrl`+letter** — only a
  hand-picked whitelist plus a few `Alt`/`Ctrl` specials.
- `Input.tryMap` (`Input.fs:9-72`) **drops** anything off that whitelist,
  and discards the Shift bit the moment Ctrl is held (`Input.fs:31`). So
  Zed/VS Code-style `Ctrl+Shift+P` is structurally impossible today
  (audit observation #5).
- Word motion is Mac-style `Alt+←/→`; `Ctrl+←/→` is unmapped.
- A full remap layer requires reworking both: a `Chord` representation
  (modifiers + key) over a wider key space, and a `tryMap` that yields
  chords instead of silently dropping keys.

### 1.3 A partial key abstraction already exists (plugins)

The plugin API already has half of this:

- `KeyChord` (`Fedit.PluginApi/Types.fs:56-61`): `Char | Ctrl | Alt |
CtrlShift | F of int`.
- Plugins register `(KeyChord * string)` pairs
  (`Host.fs:15`, collected in `Plugins.fs:46,243-247`).
- `toKeyChord` (`Editor.fs:834-837`) maps a `KeyInput` to a `KeyChord` —
  but **only `Ctrl c` today**; everything else returns `None`, so plugins
  can effectively only bind `Ctrl`+letter.
- Plugin chords dispatch first in editor focus (`Editor.fs:845-862`), but
  global chords (`Editor.fs:1219+`) intercept before that, so a plugin can
  never shadow `Ctrl+S` etc. (audit #13).

`KeyChord` is part of the **versioned public contract** (`apiVersion "1"`),
so it must evolve additively. The host's richer internal `Chord` can be a
superset, with a total mapping `Chord -> KeyChord option` for the plugin
boundary.

### 1.4 The MVU loop is ideal for macros

The runtime is `KeyPressed → Msg → Editor.update (pure) → (Model', Effect
list) → runEffect → Msg` drained from a `ConcurrentQueue` each tick. The
whole interaction is **already a serializable stream of `KeyInput`/`Msg`
values.** Recording is "append each `KeyInput` to a list"; replay is
"re-enqueue that list." No second code path. This is the MVU form of Vim's
"a macro is just recorded keystrokes," and it inherits record/replay's
robustness for free (semantic motions generalize; absolute ones don't).

### 1.5 Config precedent

Config is JSON at `~/.config/fedit/config.json`, hand-parsed field-by-field
with `System.Text.Json` and clamped (`Config.fs`). Themes are separate JSON
in `~/.config/fedit/themes/*.json`. There is precedent for _both_ a single
config file and a directory of small declarative files. Keybindings fit
naturally as a sibling file (or a `keybinds` block), but see §4 for why a
non-JSON line format reads better for this specific data.

---

## 2. How others do it — keybinding configuration

### 2.1 Terminal emulators

| Tool          | Format           | Single binding                                        | Sequences / modes                                                        | Action model                                           | Unbind                                                                 |
| ------------- | ---------------- | ----------------------------------------------------- | ------------------------------------------------------------------------ | ------------------------------------------------------ | ---------------------------------------------------------------------- |
| **Ghostty**   | flat `key = val` | `keybind = ctrl+s=save`                               | `>` sequences (`ctrl+a>n`), **no timeout**                               | named actions + `text:`/`csi:` raw bytes (no run-cmd)  | `=unbind` (passthrough) / `=ignore` (swallow); `clear` resets all      |
| **Kitty**     | line `map …`     | `map ctrl+shift+t new_tab`                            | `>` chords **and** `--mode` key-tables w/ `push/pop_keyboard_mode` stack | named + `send_text`/`send_key`/`launch`/`combine`      | no-action map (passthrough) / `no_op` (swallow); `clear_all_shortcuts` |
| **WezTerm**   | Lua tables       | `{key='c',mods='CTRL',action=act.CopyTo 'Clipboard'}` | `leader` (w/ timeout) + `key_tables` + `ActivateKeyTable` stack          | `wezterm.action.*` + `action_callback` (arbitrary Lua) | `disable_default_key_bindings`; `DisableDefaultAssignment`             |
| **Alacritty** | TOML             | `{key="N",mods="Control\|Shift",action="…"}`          | **none** (single keypress only)                                          | `action` / `chars` / `command`                         | `None` (swallow) / `ReceiveChar` (passthrough)                         |

Recurring patterns:

- **`mods + key` trigger grammar is universal** (joiner is `+` or `|`; case
  is cosmetic). Modifier aliasing (`cmd`/`super`, `alt`/`opt`) is expected.
- **Layout-dependent vs physical keys is a real, recurring problem.**
  Ghostty (codepoint vs `KeyA`), WezTerm (`mapped:`/`phys:`/`raw:`),
  Alacritty (chars vs scancodes). Pick a default and document it; for an
  editor, layout-dependent codepoints (users think in characters) is the
  right default.
- **Three action kinds recur:** named semantic action; send raw text/bytes;
  run external command. Named-first with a `text:` escape hatch is the core.
- **Two unbind levels:** a global "drop all defaults" reset _and_ a precise
  per-key disable, each distinguishing **swallow vs pass-through**.

Standout ideas worth stealing:

- **Ghostty `performable:`** — only consume the key if the action actually
  succeeds, else let it through. Gold for an editor: "format selection"
  should pass the key through when there's no selection rather than eating
  it silently.
- **Kitty `kitty_mod`** — one alias modifier (default `ctrl+shift`) that all
  defaults are expressed in terms of, so the whole scheme rebases from one
  line.
- **WezTerm/Kitty modal key-tables with an explicit push/pop stack** — the
  right primitive if fedit ever wants vim-like or transient ("resize",
  "select") layers. Models cleanly as a stack of mode names in the Model.
- **WezTerm shows the active key-table in the status bar** — discoverability
  for pending state.

Pitfalls to avoid:

- **Ghostty's indefinite sequence timeout** — a stuck-feeling prefix is
  worse in an editor than a terminal; always time out and show pending
  state.
- **Ghostty's subtle prefix-shadowing** (`ctrl+a` vs `ctrl+a>n` mutually
  unbinding) — define one predictable rule and report conflicts at load.
- **Alacritty's two opaque unbind idioms** (`None` vs `ReceiveChar`) — name
  them clearly.
- **WezTerm-style full programmability as the _primary_ surface** — overkill
  and verbose for keybindings; keep config declarative, push programmability
  into the plugin API.

Sources: [Ghostty keybind](https://ghostty.org/docs/config/keybind) ·
[sequences](https://ghostty.org/docs/config/keybind/sequence) ·
[actions](https://ghostty.org/docs/config/keybind/reference) ·
[Kitty mapping](https://sw.kovidgoyal.net/kitty/mapping/) ·
[Kitty actions](https://sw.kovidgoyal.net/kitty/actions/) ·
[WezTerm keys](https://wezterm.org/config/keys.html) ·
[WezTerm key tables](https://wezterm.org/config/key-tables.html) ·
[Alacritty config](https://alacritty.org/config-alacritty.html)

### 2.2 Code editors (declarative keymaps)

**Zed is the closest structural analog to fedit** — non-modal, JSON config,
context-scoped — and the single best-designed system surveyed.

```jsonc
// Zed keymap.json — array of blocks, each a context + a bindings map
[
    {
        "context": "Editor && mode == full",
        "bindings": { "ctrl-right": "editor::SelectLargerSyntaxNode" },
    },
    { "context": "ProjectPanel && not_editing", "bindings": { "o": "project_panel::Open" } },
    { "bindings": { "ctrl-q": "app::Quit" } }, // no context = global
]
```

Why Zed is the model to copy:

- **Bindings grouped under a shared `context` block** (less repetition than
  VS Code's flat per-binding `when`). Context is a predicate over the live
  UI tree (`Editor`, `ProjectPanel`, `Terminal`) — the direct equivalent of
  fedit's `Editor | Sidebar | Prompt` focuses.
- **Argument syntax `["action", arg]`** where `arg` is any JSON value
  (scalar or object). Cleaner than VS Code's separate `args` field with its
  array-wrapping footgun. Maps straight onto an F# DU
  (`["pane::activate", 0]` → `Action.ActivatePane 0`).
- **Sequences are space-separated** (`"ctrl-k ctrl-c"`), with a ~1s timeout
  on pending prefixes.
- **`null` to unbind** — also suppresses fallback to parent-context
  bindings.
- **Two-tier conflict resolution** (the best of the five): (1) specificity
  — a context-scoped binding beats a no-context global one; (2) load order
  as tiebreaker — last defined wins, and user config loads after defaults,
  so users override built-ins for free.

The other four, briefly:

- **VS Code** — defines the de-facto `when`-clause language (`&&`, `||`,
  `!`, `==`, `=~` regex, `in`). Flat `{key, command, when, args}`. Remove a
  default with `-command`. Resolution is pure bottom-to-top order, **no**
  specificity scoring (more user burden than Zed). fedit does **not** need
  this expression-language weight — its context set is small and closed.
- **Helix** — TOML `[keys.normal]` etc.; the elegant bit is **prefix keys
  as nested tables** (`[keys.normal.g]`), where config structure mirrors key
  structure. Tied to its modal model, though.
- **Neovim** — imperative `vim.keymap.set(mode, lhs, rhs, opts)`; `rhs` can
  be an arbitrary Lua function (no serialized-arg problem). The lasting
  lesson is `desc` on every binding powering **which-key** discoverability.
- **Kakoune** — `declare-user-mode` + `map … -docstring` + autoinfo popup:
  leader menus as a first-class, declarable concept with built-in
  discoverability driven by per-binding docstrings.

**Discoverability lesson (Neovim/Kakoune):** which-key/autoinfo popups are
powered by a _description carried on each binding_. fedit already has a
unified command prompt — reuse the command's own label rather than
re-stating it per binding, and build the keymap loader to produce a
`keystroke ↔ command` index up front so both the prompt and a future
which-key popup read from one source.

Sources: [Zed key bindings](https://zed.dev/docs/key-bindings) ·
[VS Code keybindings](https://code.visualstudio.com/docs/getstarted/keybindings) ·
[VS Code when-clauses](https://code.visualstudio.com/api/references/when-clause-contexts) ·
[Helix remapping](https://docs.helix-editor.com/remapping.html) ·
[Neovim map](https://neovim.io/doc/user/map/) ·
[which-key.nvim](https://github.com/folke/which-key.nvim) ·
[Kakoune mapping](https://github.com/mawww/kakoune/blob/master/doc/pages/mapping.asciidoc)

### 2.3 GUI/IDE keymaps — layering & inheritance

|                | IntelliJ/Rider                                               | Fleet                             | Emacs                                  |
| -------------- | ------------------------------------------------------------ | --------------------------------- | -------------------------------------- |
| Format         | XML, one file/keymap                                         | JSON (`user.json` + workspace)    | Lisp (runtime data)                    |
| Action model   | string action IDs                                            | kebab action IDs                  | named command symbols                  |
| Stored content | **delta vs a named parent**                                  | **delta vs base**                 | full maps, composed at runtime         |
| Inheritance    | child copy of one predefined parent + per-action inheritance | implicit delta + 3-layer priority | precedence cascade over N maps         |
| Remove         | clear in UI                                                  | `-action-id`                      | `(define-key map key nil)`             |
| Multi-stroke   | "second stroke" (max 2)                                      | single key string                 | **true prefix-keymap composition (N)** |

Takeaways:

- **Keymap as a delta over a base** (JetBrains/Fleet) is the right starting
  complexity for a non-modal editor: ship a base map, let user config be a
  small delta with a `-action` removal form.
- **Emacs's many-maps + fixed precedence cascade** is the more flexible
  model and the one to reach for _only if_ fedit later grows contexts/modes
  that need independent overlays. Its compositional prefix keys (`C-x C-f`
  resolves `C-f` within the keymap bound to `C-x`, and prefix maps from
  different sources _combine_) are the gold standard for sequences, but more
  than fedit needs in v1.

Sources: [IntelliJ keymap](https://www.jetbrains.com/help/idea/settings-keymap.html) ·
[Fleet settings](https://www.jetbrains.com/help/fleet/settings.html) ·
[Emacs active keymaps](https://www.gnu.org/software/emacs/manual/html_node/elisp/Active-Keymaps.html)

---

## 3. Macro systems

> Decision: **sketch only, defer.** This section informs how to shape the
> action vocabulary and input path so macros drop in later without rework.

### 3.1 The landscape

- **Vim** — _a macro is just text in a register._ `q{reg}` records
  keystrokes into register `reg`; `@{reg}` types them back through the input
  pipeline; `@@` repeats; `18@a` / `999@a` runs N times / to-EOF (stops on
  first error). Registers are shared with yank/delete, so you can yank text
  into a register and execute it, or paste a macro out, edit it, and yank it
  back. Append with an uppercase register; set without recording via `:let
@a = '…'`. Recursive macros call themselves at the end.
- **Emacs keyboard macros** — same record/replay philosophy, richer tooling:
  `F3`/`F4` to record, `C-x e` to run, a macro _ring_, an **incrementing
  counter** (`C-x C-k C-i`), `name-last-kbd-macro` + `insert-kbd-macro` to
  name and persist a macro into init (the bridge to "scripted" macros), and
  `kmacro-edit-macro` to edit in human-readable `kbd` form.
- **Kakoune** — `Q`/`q` record/replay into registers (same model);
  `execute-keys` is the scripted-command analogue.

### 3.2 Two philosophies

|                                   | Record-and-replay keystrokes                          | Scripted command sequences     |
| --------------------------------- | ----------------------------------------------------- | ------------------------------ |
| Captures                          | literal input events                                  | named editor commands          |
| Storage                           | opaque keystroke string / register                    | structured config (diffable)   |
| Authoring                         | record live; hand-edit is fiddly (escaping)           | hand-author trivially          |
| Captures ad-hoc typed text?       | **yes**                                               | no                             |
| Survives rebinding internal keys? | re-resolves at replay (usually wanted)                | references command identity    |
| Needs                             | one input path + replay guard + "am I replaying" flag | a stable named-action registry |

### 3.3 Why record/replay fits fedit's MVU loop almost for free

The loop already _is_ an event stream drained from a `ConcurrentQueue`.
Record/replay is therefore additive, not a new engine:

- **Record raw `KeyInput`** (pre-`update`), not resolved `Msg` and not
  `Model` deltas. Re-feeding `KeyInput` means the _current_ keymap
  re-interprets the keys at replay (matching Vim/Emacs semantics).
- **Register table is just more pure `Model` data**: `Map<char, KeyInput
list>`, alongside buffers/cursors — mirroring Vim's "registers are shared
  data."
- **Replay = re-enqueue** the recorded list into the existing queue, with a
  `replaying` guard so injected events aren't re-captured. Stop on first
  no-op/error so "replay 9999×" terminates at EOF.
- **Effects replay correctly** because file/clipboard I/O already routes
  through `runEffect`.

MVP, when it lands: a register table, a record-toggle action, a
replay-action with count, and repeat-last. Counters, recursion,
region-apply, and a macro ring are natural follow-ons.

### 3.4 The unification point with keybindings

Both features share one substrate — the **named action registry**. A saved
macro becomes a first-class action (`RunMacro of name`), and the keybinding
layer points a chord at it like any built-in:

```jsonc
ctrl-shift-r = replay-macro:a       // a keybinding pointing at a macro
```

To persist a recorded macro across sessions (the Emacs `insert-kbd-macro`
move), serialize the register's `KeyInput list` into the config as a named
macro the keybinding file can reference.

> Note: CLAUDE.md reserves plain `Char` chords for text input, so
> record/replay should use modifier chords or a leader prefix, **not** bare
> `q`/`@` like Vim.

Sources: [Vim macros](https://vim.fandom.com/wiki/Recording_keys_for_repeated_jobs) ·
[recursive macros](https://vim.fandom.com/wiki/Record_a_recursive_macro) ·
[Emacs keyboard macros](https://www.gnu.org/software/emacs/manual/html_node/emacs/Basic-Keyboard-Macro.html) ·
[kmacro counter](https://www.gnu.org/software/emacs/manual/html_node/emacs/Keyboard-Macro-Counter.html) ·
[Kakoune macros](https://imfrom.github.io/post/kakoune-macros-recording-and-replay/)

---

## 4. Config format & the F# DSL question

### 4.1 F# DSL building blocks (internal/embedded DSL)

F# is unusually strong at _internal_ DSLs — the host language is the DSL, so
you inherit the type checker and tooling. The relevant techniques, each as
it applies to keybindings (drawn from the
[DSL cheatsheet](https://github.com/dungpa/dsls-in-action-fsharp/blob/master/DSLCheatsheet.md)
and the [Betfair article](https://dev.to/bfexplorer/f-dsls-what-they-are-why-they-matter-and-how-they-improve-betfair-bottrigger-strategies-3b3p)):

1. **DUs as the AST / command vocabulary** — invalid bindings become
   unrepresentable. fedit already has the chord half (`KeyChord`).
2. **Records + a list = declarative config** — exactly today's `(KeyChord *
string) list`, promoted to a typed `Binding list`.
3. **Custom operators** — `let (==>) chord cmd = …` gives `ctrl 's' ==>
Save`, reading almost like Ghostty's `ctrl+s=save` but fully typed.
4. **Computation expressions / builders** — `keymap { bind … }`. Over-
   engineering here; the record-list is clearer. CEs earn their keep only
   for short-circuit _sequencing_ (macro that bails on failure).
5. **Pipelines & partial application** — overlay user bindings on defaults
   as a pipeline (`defaults |> applyOverrides userBindings`).
6. **Active patterns for parsing** — the bridge that turns an external
   token `"ctrl+s"` into a `KeyChord` while still pattern-matching cleanly.
7. **Units of measure** — N/A for keybindings.

Internal vs external trade-off: an internal DSL is gorgeous for _devs_
(compile-time safety, IntelliSense) but **unusable by non-F# users without
paying a compilation cost.** That single fact drives the decision below.

### 4.2 How an F# config would actually be consumed — four mechanisms

| Mechanism                                        | Startup         | Dep weight   | Friendly to non-F# users | Type-safe user config | Hot-reload                   | AOT-safe | Fit             |
| ------------------------------------------------ | --------------- | ------------ | ------------------------ | --------------------- | ---------------------------- | -------- | --------------- |
| **B1** data file + compiled-in DSL defaults      | ~0              | none         | yes                      | no (validated)        | trivial (re-read)            | yes      | **Best**        |
| B2 FSI eval `keybinds.fsx`                       | **+~2s**        | huge (FCS)   | no                       | yes                   | yes                          | **no**   | Poor            |
| B3 compiled-plugin DLL (fedit already does this) | build once      | SDK required | no                       | yes                   | hard (collectible ALC leaks) | partial  | Power-user only |
| B4 source generator                              | n/a (dev build) | none         | n/a                      | dev only              | n/a                          | yes      | Dev convenience |

Key findings:

- **B2 (FSI / FSharp.Compiler.Service) is disqualified** for a minimalist
  TUI: even a trivial `.fsx` is ~1.8–2.7s through the F# scripting path —
  the entire perceived launch time of a terminal editor, before first paint
  ([dotnet/fsharp#12636](https://github.com/dotnet/fsharp/issues/12636)); it
  drags tens of MB of FCS into the binary; and it **breaks
  NativeAOT/trimming**
  ([#13398](https://github.com/dotnet/fsharp/issues/13398)), which a
  minimalist editor likely wants. A bad script can also `StackOverflow` the
  host uncatchably.
- **B3 already exists** (`Plugins.fs`: auto-generated fsproj → `dotnet build
-c Release` → `AssemblyLoadContext`, and a plugin already returns
  `Keybindings: (KeyChord * string) list`). Right home for power-user
  bindings/macros-as-functions; **wrong for casual rebinding** (needs the
  SDK + a multi-second build to change `ctrl+s`, and hot-reload would mean
  flipping the load context to `isCollectible = true` and fighting
  cooperative-unload leaks).

### 4.3 External data format comparison

| Format                                        | F# parsing                                                                  | Human-friendliness for keybinds                                 | Minimalism                       |
| --------------------------------------------- | --------------------------------------------------------------------------- | --------------------------------------------------------------- | -------------------------------- |
| **Tiny custom line format** (`ctrl+s = save`) | `Split` + active pattern, or FParsec for robustness — **no package needed** | **Best** — one line, one binding; the exact mental model        | **Best** — zero structural noise |
| TOML                                          | Tomlyn (AOT-ready)                                                          | decent; `[[keybind]]` array-of-tables is verbose for one-liners | one ~200KB dep                   |
| JSON                                          | System.Text.Json (in-box)                                                   | poor to hand-edit — quotes, commas, no comments                 | in-box but noisy                 |
| KDL                                           | third-party, version-fragmented                                             | good node syntax but niche                                      | external dep + spec risk         |
| S-expr                                        | FParsec / hand-rolled                                                       | familiar only to Lisp users                                     | own it yourself                  |

The **tiny line format wins on every axis fedit cares about** and — the
elegant part — it mirrors the internal DSL 1:1, so the parser is a thin
bridge to the same `KeyChord`/`Action` types:

```
ctrl+s        = save
ctrl+shift+p  = command-palette
ctrl+k ctrl+c = comment-line          # a sequence
ctrl+x        =                       # empty RHS = unbind a default
```

If a unified config file is ever wanted (themes + plugin settings +
keybinds in one place), **TOML via Tomlyn** is the AOT-safe upgrade — but
don't reach for it just for keybinds. (Note: fedit's `config.json` is JSON
today; a separate `keybinds` line-file keeps that boundary clean and avoids
forcing keybindings into JSON's punctuation.)

Sources: [FCS interactive](https://fsharp.github.io/fsharp-compiler-docs/fcs/interactive.html) ·
[fsi startup #12636](https://github.com/dotnet/fsharp/issues/12636) ·
[F# AOT #13398](https://github.com/dotnet/fsharp/issues/13398) ·
[.NET unloadability](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability) ·
[Tomlyn](https://github.com/xoofx/Tomlyn) ·
[FParsec](https://github.com/stephan-tolksdorf/fparsec)

---

## 5. Recommended design

Targeting the **full remap layer**. The work is a real refactor (it touches
`Primitives`, `Input`, the `Editor.update` dispatch, and the PluginApi
boundary), so it is sequenced into phases below. The end state:

### 5.1 The action vocabulary (the keystone)

One recursive DU naming every bindable operation, plus a single dispatcher.
This subsumes the scattered `Command` cases and the inline `Ctrl`/motion
logic.

```fsharp
type Action =
    // editing / motion / selection (today inline in runEditor)
    | MoveLeft | MoveRight | MoveUp | MoveDown
    | MoveWordLeft | MoveWordRight | MoveHome | MoveEnd
    | ExtendLeft | ExtendRight | /* … shift-motions … */ SelectAll
    | Indent | Unindent | DeleteWordBack | DeleteWordForward
    | Undo | Redo | Copy | Cut | Paste
    // commands (today the Command DU)
    | Save | SaveAs of string | Quit | OpenPalette | OpenFilePicker
    | NextBuffer | PrevBuffer | SetTheme of string | Goto of int * int option
    | RunPlugin of source: string * name: string * arg: string
    // panel/focus primitives — each a complete, valid transition (see §5.4)
    | RevealSidebar | HideSidebar | ToggleSidebar | FocusSidebar | FocusEditor
    // composition & control flow — macros, multi-action & stateful bindings
    | Chain of Action list
    | When of Cond * thenDo: Action * elseDo: Action   // branch on Model state (§5.4)
    | RunMacro of register: char        // (Phase 2; parses now, no-op until macros land)

/// The single dispatcher. Replaces the three scattered match sites.
val runAction : Action -> Model -> Model * Effect list
```

`Editor.update`'s `KeyPressed` branch becomes: resolve `KeyInput` →
(optional) `Chord` → look up `Action` in the active keymap → `runAction`.
Text input (a bare `Character`) stays the fast default when no binding
matches. The existing `Command` DU can remain as the _prompt's_ parse target
and map onto `Action`, or be folded in — either way there is now one
dispatcher behind both surfaces.

### 5.2 The key model

```fsharp
type Modifier = Ctrl | Alt | Shift | Super
type Key =
    | Char of char            // layout-dependent codepoint (the editor default)
    | Named of NamedKey       // Enter, Esc, Tab, arrows, Home/End, PgUp/Dn, Backspace, Delete
    | Fn of int               // F1..F24
type Chord     = { Mods: Set<Modifier>; Key: Key }
type KeyStroke = Chord list   // length > 1 = a sequence like ctrl-k ctrl-c
```

- Rework `Input.tryMap : ConsoleKeyInfo -> Chord option` to **stop dropping
  keys** — emit a `Chord` for any modifier+key combination, including
  `Ctrl+Shift+_` and F-keys (fixes audit #5).
- **Sequences**: hold a `PendingPrefix of Chord list * deadline` in the
  Model. On a chord that is a prefix of some binding, enter pending state,
  show it in the status bar (WezTerm lesson), and fire on completion or
  cancel on timeout/Escape (Zed's ~1s, configurable — never Ghostty's
  indefinite wait).
- **PluginApi boundary**: keep `KeyChord` (v1 contract) unchanged; add a
  total `Chord -> KeyChord option` so plugin bindings keep working, and
  optionally surface a richer `KeyChord` in a future `apiVersion "2"`.

### 5.3 Keymap, contexts, resolution

```fsharp
type Context = Global | Editor | Sidebar | Prompt    // closed enum — typos fail at load
type Binding = { Stroke: KeyStroke; Context: Context; Action: Action option } // None = unbind
type Keymap  = Binding list                          // defaults ++ user delta
```

- **Context = closed enum** (Zed's model, but typo-proof — an unknown
  context name is a load error, not a silently-dead binding).
- **Resolution = Zed's two tiers**: context specificity first (a
  context-scoped binding beats a `Global` one for the same stroke), then
  load order (user delta appended after defaults → user wins). `Action =
None` unbinds and suppresses fallback to the `Global` binding.
- Build a `keystroke ↔ action/label` **index** at load so the command prompt
  can show bound keys and a future which-key popup can render continuations.

### 5.4 Stateful actions — the tri-state sidebar

Some bindings aren't a constant action; they depend on the model. The
canonical case is today's three-state `Ctrl+B` (`Editor.fs:1269-1287`):

| State                    | `Ctrl+B` does       | transition |
| ------------------------ | ------------------- | ---------- |
| sidebar hidden           | reveal + focus it   | 1 → 3      |
| visible, editor-focused  | focus it (no close) | 2 → 3      |
| visible, sidebar-focused | hide + focus editor | 3 → 1      |

A naive `stroke → action` map can't express this, because the action is a
_function of the model_, not a constant. Three ways the design handles it,
from least to most reconfigurable.

**(a) One stateful action — the keystone insight.** `runAction : Action ->
Model -> Model * Effect list` already takes the `Model`, so a conditional
transition is the natural shape, not a bolt-on. The minimal,
zero-behavior-change move (Phase 1) is to lift today's handler verbatim into
one named action and bind `ctrl+b` to it:

```fsharp
// runAction body == today's Editor.fs:1269-1287, unchanged:
| ToggleSidebarFocus ->
    if not model.Panels.SidebarVisible then reveal model |> focus Sidebar     // 1 → 3
    elif model.Focus <> Sidebar       then focus Sidebar model                // 2 → 3
    else hide model |> focus Editor                                           // 3 → 1
```

This is exactly how Zed/VS Code ship it: one command (`ToggleLeftDock`-style)
whose _body_ branches; the palette shows a single entry. The keymap stays
trivial (`ctrl+b = toggle-sidebar-focus`). The only downside: the policy is
opaque, baked into the action. (`ToggleSidebarFocus` need not be its own DU
case — it can be sugar for the `When` tree in (c); (a) and (c) are the same
behavior, the choice is whether you hand-write the branch in `runAction` or
compose it from `When`.)

**(b) Per-context maps absorb the _focus_ dimension for free.** The resolver
(§5.3) already keys on `Focus`, which splits the 3-state into "which context"
plus a single leftover visibility branch:

```fsharp
// using the §5.5 DSL: `==>` defaults to Editor context, `inCtx` overrides it
(chord [Ctrl] (Char 'b') ==> Chain [ HideSidebar; FocusEditor ]) |> inCtx Sidebar  // 3 → 1
 chord [Ctrl] (Char 'b') ==> When(SidebarVisible,
                                  FocusSidebar,                           // 2 → 3
                                  Chain [ RevealSidebar; FocusSidebar ])  // 1 → 3  (Editor)
```

When you're in the sidebar, context resolution picks the sidebar binding
(collapse); in the editor, it picks the editor binding, which only decides
visible-vs-hidden. The focus branch disappears into the context system —
that's the payoff of per-context maps. (`Ctrl+B` is global today, firing
before focus routing; to preserve "works from the prompt too," either keep
one `Global` smart action per (a), or add a `prompt` row mirroring `editor`.)

**(c) A `When` combinator makes the conditional _data_.** `Chain` already
sequences; its control-flow sibling lets conditional toggles be expressed
without a VS Code-style `when`-clause language over open context keys:

```fsharp
type Cond =
    | SidebarVisible | SidebarFocused | EditorFocused | HasSelection
    | Not of Cond | AllOf of Cond list

let rec runAction action model =
    match action with
    | When (c, t, e) -> runAction (if evalCond c model then t else e) model
    | Chain xs       -> xs |> List.fold (fun (m, fx) a ->
                             let m', fx' = runAction a m in m', fx @ fx') (model, [])
    | RevealSidebar  -> { model with Panels = { model.Panels with SidebarVisible = true } }, []
    // …
```

The whole tri-state then becomes one value — today's state machine, as data:

```fsharp
When(SidebarFocused,
     Chain [ HideSidebar; FocusEditor ],
     When(SidebarVisible,
          FocusSidebar,
          Chain [ RevealSidebar; FocusSidebar ]))
```

`When` also generalizes Ghostty's `performable:` (§2.1) — "act only if the
model is in the right state, else do the other thing."

**The ergonomic split (the important point).** You would _not_ write that
`When` tree in the flat line file — it would be unreadable. Instead:

- **The line file binds strokes → _named_ actions** (`editor ctrl+b =
reveal-or-focus-sidebar`). Always one line, never a conditional.
- **Composite/conditional logic is authored in the F# DSL defaults (or a
  plugin)** with `When`/`Chain`, type-checked, and _surfaced as a named
  action_.
- **Primitives are exposed too** (`reveal-sidebar`, `hide-sidebar`,
  `toggle-sidebar`, `focus-sidebar`, `focus-editor`), so a user who dislikes
  the cycle composes their own with zero F# — e.g. `ctrl+b = toggle-sidebar`
  (visibility only) + `ctrl+e = focus-sidebar` for non-cyclic behavior.

So: a trivial config surface, full power underneath; the line format never
needs conditionals because complex behavior is always reachable as a _name_.

One invariant this relies on: **every primitive must be a complete, valid
transition.** `HideSidebar` must also clear the sidebar's incremental search
(today's handler does `Workspace.clearSearch`), so a user-built `chain` can't
strand the model in a half-state. That is itself the argument for
well-defined named primitives over ad-hoc keystroke chains.

### 5.5 Config surface (the recommendation)

**Compiled-in DSL defaults + a tiny line-format user override file** (B1 + §4.3).

Defaults, authored by devs as a type-safe internal DSL:

```fsharp
let private (==>) stroke action = { Stroke = stroke; Context = Editor; Action = Some action }
let private inCtx ctx b = { b with Context = ctx }

let defaults : Keymap = [
    chord [Ctrl] (Char 's')                         ==> Save
    chord [Ctrl; Shift] (Char 'p')                  ==> OpenPalette
    seq [ chord [Ctrl] (Char 'k'); chord [Ctrl] (Char 'c') ] ==> Chain [ /* comment */ ]
    (chord [] (Named Enter)                          ==> ActivateSidebarItem) |> inCtx Sidebar
]
```

Users edit `~/.config/fedit/keybinds`, parsed with `Split` + active patterns
into `Result<Binding,err>` and overlaid:

```
# ~/.config/fedit/keybinds  — one binding per line, "[context] stroke = action"
editor  ctrl+s        = save
editor  ctrl+k ctrl+c = comment-line
sidebar enter         = open
        ctrl+x        =                 # empty RHS unbinds the default
```

Properties: zero new dependencies, ~0 startup cost, AOT/trim-safe, hot-
reload by re-reading the file into the pure Model (`KeybindsChanged`
effect → re-parse → swap `Keymap`), and a parser that is a thin bridge to
the same DU the defaults use. Errors report the offending line + stroke and
fall back to defaults so the editor always boots (mirroring
`ConfigIO.load`).

### 5.6 The "most interesting" alternative

Make keymaps a facet of the **existing compiled-plugin pipeline** (B3). A
plugin already registers `(KeyChord * string)`; extend it so a "keymap
plugin" can bind a chord to an F# function — a real, type-checked macro with
the existing `AssemblyLoadContext` isolation. The honest cost is the SDK
requirement, a multi-second first build, and that true hot-reload would mean
flipping to a collectible load context and fighting unload leaks. So this is
the **power-user tier, not the default**. The clean end state is _both_: the
line-format file for everyone, plus "your plugin can also bind keys (and run
arbitrary logic)" for the F#-capable — which is already the shape of fedit's
plugin model.

### 5.7 Suggested phasing

The big win is front-loaded into Phase 1; user-facing config is Phase 3.

1. **Unify actions (no behavior change).** Introduce the `Action` DU
   (including `Chain`/`When` and the panel/focus primitives) and `runAction`;
   route the existing global chords, `runEditor`, and `runSidebar` through it.
   The tri-state `Ctrl+B` (§5.4) moves verbatim into one action. Hard-coded
   defaults still, but now one dispatcher. Pure refactor, fully testable
   against current behavior.
2. **Expand the key model.** New `Chord`/`Key` types; rework `Input.tryMap`
   to stop dropping keys; add `Ctrl+Shift`/F-keys/`Ctrl+←→`; add the
   pending-prefix sequence engine + status-bar indicator. Fixes audit
   #4/#5/#8.
3. **User keymap file.** The line-format parser + overlay + context
   resolution + `unbind`; hot-reload; `:keybind` introspection and a
   keymap section in the docs. Migrate the PluginApi boundary mapping.
4. **(Deferred) Macros.** Register table in the Model, record-toggle +
   replay actions, replay guard in the runtime, `RunMacro` wired live,
   persistence of named macros into the keybinds file.

### 5.8 Key decisions / risks to confirm before building

- **`Command` vs `Action`**: fold `Command` into `Action`, or keep `Command`
  as the prompt's parse target that maps onto `Action`? (Lean: keep
  `Command` as a thin parse layer; one `runAction` underneath.)
- **PluginApi evolution**: stay on `KeyChord` v1 with a host-side mapping
  (additive, safe), vs introduce `apiVersion "2"` with the richer chord
  (more power, migration cost). Lean: additive mapping now.
- **Layout-dependent vs physical keys**: default to layout-dependent
  codepoints (editor users think in characters); document it; revisit only
  if non-QWERTY reports arrive.
- **Scope guard**: modal key-tables (Kitty/WezTerm) and which-key popups are
  _attractive but out of scope_; the design leaves room (pending-prefix
  state, the binding index) without building them in v1.

---

## Appendix: source index

- Terminals — Ghostty: [keybind](https://ghostty.org/docs/config/keybind),
  [sequence](https://ghostty.org/docs/config/keybind/sequence),
  [reference](https://ghostty.org/docs/config/keybind/reference) · Kitty:
  [mapping](https://sw.kovidgoyal.net/kitty/mapping/),
  [actions](https://sw.kovidgoyal.net/kitty/actions/) · WezTerm:
  [keys](https://wezterm.org/config/keys.html),
  [key tables](https://wezterm.org/config/key-tables.html) · Alacritty:
  [config](https://alacritty.org/config-alacritty.html)
- Editors — [Zed](https://zed.dev/docs/key-bindings),
  [VS Code keybindings](https://code.visualstudio.com/docs/getstarted/keybindings),
  [VS Code when-clauses](https://code.visualstudio.com/api/references/when-clause-contexts),
  [Helix](https://docs.helix-editor.com/remapping.html),
  [Neovim](https://neovim.io/doc/user/map/),
  [which-key.nvim](https://github.com/folke/which-key.nvim),
  [Kakoune](https://github.com/mawww/kakoune/blob/master/doc/pages/mapping.asciidoc)
- IDEs — [IntelliJ](https://www.jetbrains.com/help/idea/settings-keymap.html),
  [Fleet](https://www.jetbrains.com/help/fleet/settings.html),
  [Emacs keymaps](https://www.gnu.org/software/emacs/manual/html_node/elisp/Active-Keymaps.html)
- Macros — [Vim](https://vim.fandom.com/wiki/Recording_keys_for_repeated_jobs),
  [Vim recursive](https://vim.fandom.com/wiki/Record_a_recursive_macro),
  [Emacs kmacro](https://www.gnu.org/software/emacs/manual/html_node/emacs/Basic-Keyboard-Macro.html),
  [Kakoune macros](https://imfrom.github.io/post/kakoune-macros-recording-and-replay/)
- F# DSL / .NET — [DSL cheatsheet](https://github.com/dungpa/dsls-in-action-fsharp/blob/master/DSLCheatsheet.md),
  [Betfair F# DSL](https://dev.to/bfexplorer/f-dsls-what-they-are-why-they-matter-and-how-they-improve-betfair-bottrigger-strategies-3b3p),
  [FCS interactive](https://fsharp.github.io/fsharp-compiler-docs/fcs/interactive.html),
  [fsi startup](https://github.com/dotnet/fsharp/issues/12636),
  [F# AOT](https://github.com/dotnet/fsharp/issues/13398),
  [.NET unloadability](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability),
  [Tomlyn](https://github.com/xoofx/Tomlyn),
  [FParsec](https://github.com/stephan-tolksdorf/fparsec)
