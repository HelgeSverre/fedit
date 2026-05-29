# Configurable Keybindings — Design Spec

> Status: design spec, approved (review decisions folded into §11). Derived from the research in
> [`docs/superpowers/research/keybindings-and-macros.md`](../research/keybindings-and-macros.md)
> (read §5 there for the rationale behind every choice here). This spec is
> the implementation contract; the research doc is the "why."
>
> Scope decisions (set during brainstorming): build the **full remap layer**;
> **sketch macros but defer** them to a later phase; **internal F# DSL for
> defaults + a line-format user file** for config.

---

## 1. Goal & scope

Make every fedit keybinding user-configurable, by first giving the editor a
single named action vocabulary and one dispatcher, then a richer key model
(modifiers + sequences over an expanded key universe), per-context keymaps,
and a user override file.

### In scope

- One `Action` DU naming every bindable operation + one `runAction`
  dispatcher; the three scattered dispatch sites collapse into it.
- A `Chord`/`KeyStroke` key model covering `Ctrl+Shift`, F-keys, any
  `Ctrl`+letter, `Ctrl+←/→`, and multi-key sequences (`ctrl+k ctrl+c`).
- Per-context keymaps (`Global | Editor | Sidebar | Prompt`) with Zed-style
  resolution and an `unbind` form.
- Compiled-in defaults authored as an internal F# DSL, mirrored by a
  user-editable `~/.config/fedit/keybinds` line file with explicit reload.
- Stateful/conditional actions (`Chain`, `When`) so things like the
  tri-state sidebar are expressible.
- Discoverability: a `keystroke ↔ action` index; the command prompt shows
  bound keys; a `:keybind` introspection verb.

### Out of scope (this spec)

- **Macros** (record/replay). Sketched in §10 so the architecture
  accommodates them; not implemented.
- Modal key-tables / vim-like modes (Kitty/WezTerm push-pop stacks).
- A which-key-style popup (the binding index leaves room for it later).
- Mouse bindings.
- A VS Code-style open `when`-clause expression language. `Cond` is a small
  closed DU, not a parser over arbitrary context keys.

### Non-goals / explicit constraints

- No new runtime NuGet dependency (the line parser is `Split` + active
  patterns). Preserves the minimalist brand and keeps the AOT/trim door open.
- `~0` startup cost. No FSI/`.fsx` evaluation (disqualified in research §4.2).
- `NO_COLOR` and existing behavior unchanged when no user keybinds file
  exists — defaults reproduce today's bindings exactly.

---

## 2. Resolved decisions

These were the open questions in research §5.8; resolved here (flagged for
override at the review gate):

1. **`Command` vs `Action`:** keep `Command` (`Commands.fs`) as the prompt's
   parse/complete target. `executeCommand` becomes a thin `Command -> Action`
   translation that calls `runAction`. One executor; the prompt's text-parsing
   surface stays where it is. No big-bang merge.
2. **PluginApi evolution:** keep `KeyChord` (`apiVersion "1"`) **unchanged**.
   Add a total host-side `Chord -> KeyChord option` so existing plugin
   bindings keep working. A richer chord for plugins is deferred to a
   hypothetical `apiVersion "2"`; not in scope.
3. **Layout-dependent vs physical keys:** default to **layout-dependent
   codepoints** (`Key.Char` is the produced character). Document it. Revisit
   only if non-QWERTY reports arrive.
4. **Key representation:** replace `KeyInput` with `Chord` wholesale at
   Phase 2 (a bare character is `Chord {Mods=∅; Key=Char c}`). `KeyInput`
   does not survive as a parallel type.
5. **Hot-reload trigger:** explicit reload is the baseline — re-read on a
   `:keybind reload` command and after the keybinds file is saved through
   fedit. A `FileSystemWatcher` on `~/.config/fedit` is an optional
   follow-on, not required for v1 (the workspace watcher does not cover the
   config dir).

---

## 3. Architecture

```
Console.ReadKey ─▶ Input.tryMap ─▶ Chord
                                     │
                                     ▼
                        Editor.update (KeyPressed chord)
                                     │
                 ┌───────────────────┴───────────────────┐
                 ▼                                         ▼
        sequence engine                            (text fast-path:
   (pending prefix? extend / fire / cancel)         bare Char with no
                 │                                   binding → insert)
                 ▼
        Keymap.resolve ctx stroke  ─▶  Action option
                 │
                 ▼
        runAction : Action -> Model -> Model * Effect list
                 │
       ┌─────────┼─────────────────────────┐
       ▼         ▼                          ▼
  primitive   Chain [..]            When(cond, a, b)
  transition  (fold)                (evalCond → a | b)
```

The keymap is loaded once at startup (`defaults` overlaid with the parsed
user file) and held in the `Model`. Resolution and `runAction` are pure;
file I/O for load/reload is an `Effect`, consistent with the existing MVU
split.

---

## 4. Data model

### 4.1 Keys (`src/Fedit/Keys.fs`, new — after `Primitives.fs`)

```fsharp
namespace Fedit

type Modifier = Ctrl | Alt | Shift | Super

type Key =
    | Char of char            // layout-dependent produced character (the default)
    | Named of NamedKey       // structural keys, layout-independent
    | Fn of int               // F1..F24

and NamedKey =
    | Enter | Escape | Tab | Backspace | Delete
    | Left | Right | Up | Down | Home | End | PageUp | PageDown | Space

type Chord     = { Mods: Set<Modifier>; Key: Key }
type KeyStroke = Chord list            // length > 1 == a sequence
```

`Chord` subsumes today's `KeyInput`: `Tab` is `{Mods=∅; Key=Named Tab}`,
`Shift+Tab` is `{Mods={Shift}; Key=Named Tab}`, `Ctrl+S` is
`{Mods={Ctrl}; Key=Char 's'}` (lowercased; see §6.1). Equality is structural,
so `Chord`/`KeyStroke` are usable as `Map` keys and in `List.tryFind`.

### 4.2 Actions (`src/Fedit/Actions.fs`, new — after `Commands.fs`)

```fsharp
namespace Fedit

type Cond =
    | SidebarVisible
    | SidebarFocused      // Focus = Sidebar
    | EditorFocused
    | PromptActive
    | HasSelection
    | BufferDirty
    | Not of Cond
    | AllOf of Cond list
    | AnyOf of Cond list

type Action =
    // motion / selection (today inline in runEditor)
    | MoveLeft | MoveRight | MoveUp | MoveDown
    | MoveWordLeft | MoveWordRight | MoveHome | MoveEnd
    | MovePageUp | MovePageDown
    | ExtendLeft | ExtendRight | ExtendUp | ExtendDown
    | ExtendHome | ExtendEnd | SelectAll
    // editing
    | Indent | Unindent | DeleteWordBack | DeleteWordForward
    | Undo | Redo | Copy | Cut | Paste
    // commands (today the Command DU, executed via runAction)
    | Save | SaveAs of string | Quit
    | OpenPalette | OpenFilePicker | OpenSearch | OpenBufferPicker
    | NextBuffer | PrevBuffer | JumpToBuffer of int
    | SetTheme of string | Goto of line: int * col: int option
    | ReloadWorkspace | OpenConfig | ReloadKeybinds
    | RunPlugin of source: string * name: string * arg: string
    // panel / focus primitives — each a COMPLETE, valid transition (§6.5)
    | RevealSidebar | HideSidebar | ToggleSidebar | FocusSidebar | FocusEditor
    // sidebar navigation (today inline in runSidebar)
    | SidebarUp | SidebarDown | SidebarPageUp | SidebarPageDown
    | SidebarTop | SidebarBottom | SidebarCollapse | SidebarExpand | SidebarActivate
    // composition & control flow
    | Chain of Action list
    | When of cond: Cond * thenDo: Action * elseDo: Action
    | NoOp
    // deferred (parses + binds now; no-op until macros land — §10)
    | RecordMacro of register: char
    | ReplayMacro of register: char * count: int
```

`Action` carries no `Model` reference, so it lives low in the compile order
and is shared by defaults, the parser, and the dispatcher.

### 4.3 Keymap (`src/Fedit/Keymap.fs`, new — after `Actions.fs`, before `Model.fs`)

```fsharp
namespace Fedit

type Context = Global | Editor | Sidebar | Prompt      // closed enum

type Binding =
    { Stroke: KeyStroke
      Context: Context
      Action: Action option }                          // None == unbind

type Keymap = Binding list                             // defaults @ user delta
```

`Model` gains one field (`src/Fedit/Model.fs`):

```fsharp
    Keymap: Keymap
    PendingPrefix: (Chord list * int) option   // (prefix so far, deadline tick)
```

`Config` is unchanged (keybinds are a separate file, not part of
`config.json`).

---

## 5. Components & file plan

New / changed files, in fsproj `<Compile Include>` order (the order is
load-bearing — see CLAUDE.md gotcha; update the fsproj **and** add each file):

| Position in compile order | File         | New/edit                                                                                                                                                                       | Contents                                                                             |
| ------------------------- | ------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------ |
| after `Primitives.fs`     | `Keys.fs`    | new                                                                                                                                                                            | `Modifier`, `Key`, `NamedKey`, `Chord`, `KeyStroke` + `Chord.parse`/`render` helpers |
| after `Commands.fs`       | `Actions.fs` | new                                                                                                                                                                            | `Cond`, `Action` DUs + `Action.ofCommand : Command -> Action`                        |
| after `Actions.fs`        | `Keymap.fs`  | new                                                                                                                                                                            | `Context`, `Binding`, `Keymap`; `defaults`; line parser; `resolve`; `KeymapIO.load`  |
| `Model.fs`                | edit         | add `Keymap`, `PendingPrefix` fields (+ defaults); `Msg.KeyPressed of Chord`; `Msg.KeybindsLoaded`; `Effect.LoadKeybinds`; `Effect.ReplayKeys` (Phase-4-only)                  |
| `Editor.fs`               | edit         | `runAction`, `evalCond`, sequence engine; rewrite the `KeyPressed` branch; route the global `Ctrl` handler / `runEditor` / `runSidebar` / `executeCommand` through `runAction` |
| `Input.fs`                | edit         | `tryMap : ConsoleKeyInfo -> Chord option` (stop dropping keys)                                                                                                                 |
| `Runtime.fs`              | edit         | handle `LoadKeybinds`/`ReplayKeys` effects; `Console.ReadKey \|> Input.tryMap` now yields `Chord`                                                                              |
| `Status.fs`/`View.fs`     | edit         | render the pending-prefix indicator                                                                                                                                            |

(`Commands.fs` itself is unchanged — it stays the prompt's parse/complete
target; `Action.ofCommand` lives in `Actions.fs`, which compiles after it.)

`runAction`/`evalCond` live in `Editor.fs` because they need `Model`,
`Buffer`, and the existing helpers (`updateActiveBuffer`, `saveActiveBuffer`,
`openPrompt`, …). Everything else is pure data below `Model`.

---

## 6. Behavior specifications

### 6.1 Key decoding (`Input.tryMap`)

`tryMap : ConsoleKeyInfo -> Chord option`. Rules:

- Collect modifiers from `keyInfo.Modifiers` into a `Set<Modifier>` — **do
  not** drop the Shift bit when Ctrl is held (fixes audit #5).
- Map `ConsoleKey` to `Key`: arrows/Home/End/PageX/Enter/Esc/Tab/Backspace/
  Delete → `Named`; `F1..F24` → `Fn`; otherwise use `keyInfo.KeyChar` →
  `Char` when it is a printable character.
- **Normalization (two cases):** - _Bare printable key_ (no `Ctrl`/`Alt`/`Super`): `Chord {Mods=∅; Key=Char
c}` where `c` is the actual produced character — Shift lives in the
  character itself (`A` vs `a`), not the modifier set. This is the text
  fast-path; the real char is what gets inserted. - _`Ctrl`/`Alt`/`Super` + letter_: `Chord {Mods=…; Key=Char (lowercased)}`;
  a held `Shift` stays in `Mods`, so `Ctrl+Shift+P` is distinct from
  `Ctrl+P`, while `Ctrl+S` and `Ctrl+s` unify to one chord. - Matching is exact structural equality on the normalized chord. (Document:
  `Ctrl/Alt/Super`+letter is case-insensitive; a capital with no
  Ctrl/Alt/Super is just text.)
- Return `None` only for genuinely unmappable input (e.g. a lone modifier
  press). Control characters that previously fell through stay `None`.

This is the only place OS key quirks live; `macOS Terminal.app` digit-row
caveats (today's `CtrlDigit` comment) move here.

### 6.2 Resolution & conflict rules

`Keymap.resolve : Context -> KeyStroke -> Keymap -> Action option`

1. Filter bindings whose `Stroke` equals the input stroke.
2. **Specificity:** a binding whose `Context` matches the active context
   wins over a `Global` binding. (With the closed enum there is no deeper
   tree.)
3. **Load order:** within the same context tier, the **last** matching
   binding wins. The user delta is appended after `defaults`, so user
   bindings override built-ins for free.
4. A matched binding with `Action = None` (`unbind`) resolves to "no action,
   and suppress fallback to the `Global` binding" — i.e. it actively frees
   the stroke in that context.
5. No match → `None`. The caller then applies the **focus-specific
   fallthrough** — the existing per-focus default handling, unchanged:
   `Editor` inserts a bare `Char` into the buffer (else ignores), `Sidebar`
   feeds it to the incremental filter, `Prompt` routes to `runPrompt`
   line-editing. This preserves today's per-focus behavior for unbound keys.

Which focuses consult the keymap: `Global` bindings resolve in **every**
focus (so today's global `Ctrl` chords keep firing even in the prompt);
`Editor`/`Sidebar`/`Prompt` bindings add to that in their focus. The
sequence engine (§6.3) runs whenever the keymap is consulted; `Prompt`
otherwise keeps its dedicated line-edit handling.

Prefix-conflict rule (avoid Ghostty's silent shadowing): a stroke that is a
**proper prefix** of any bound sequence cannot also be bound as a standalone
in the same context. Detected at load; reported as a load error, the longer
(sequence) binding kept.

### 6.3 Sequence engine

State: `Model.PendingPrefix : (Chord list * deadlineTick) option`.

On `KeyPressed chord` in a focus that consults the keymap:

- Build `candidate = (pendingPrefix |> Option.map fst |> Option.defaultValue []) @ [chord]`.
- If `candidate` exactly matches a binding's stroke → clear pending, run the
  action.
- Else if `candidate` is a proper prefix of some binding's stroke → set
  `PendingPrefix = Some (candidate, now + timeout)`; render the pending
  chords in the status bar; consume the key.
- Else → clear pending. If `pendingPrefix` was set, the sequence failed:
  emit a brief "no binding for <stroke>" notification and **do not** fall
  through to text insert (the keys were chord-prefixed, not text). If there
  was no pending prefix, apply the normal single-chord path (resolve, else
  text fast-path).

Timeout: the main loop already ticks ~60×/s. On each drain, if
`PendingPrefix` exists and `now > deadline`, clear it (and re-attempt the
buffered first chord as a standalone if it resolves). Default timeout 1000ms,
configurable later. `Escape` always clears a pending prefix. The status bar
shows e.g. `ctrl+k …` while pending (WezTerm lesson).

### 6.4 `runAction` & `evalCond`

```fsharp
let rec runAction (action: Action) (model: Model) : Model * Effect list =
    match action with
    | Chain xs ->
        xs |> List.fold (fun (m, fx) a ->
            let m', fx' = runAction a m
            m', fx @ fx') (model, [])
    | When (c, t, e) -> runAction (if evalCond c model then t else e) model
    | NoOp -> model, []
    | Save -> saveActiveBuffer None model
    | Undo -> updateActiveBuffer Buffer.undo model, []
    | RevealSidebar -> { model with Panels = { model.Panels with SidebarVisible = true } }, []
    | HideSidebar ->
        { model with
            Panels = { model.Panels with SidebarVisible = false }
            Workspace = Workspace.clearSearch model.Workspace }, []   // complete transition (§6.5)
    // … one arm per action; bodies are the existing inline logic, lifted verbatim …

and evalCond (cond: Cond) (model: Model) : bool =
    match cond with
    | SidebarVisible -> model.Panels.SidebarVisible
    | SidebarFocused -> model.Focus = Sidebar
    | HasSelection   -> (activeBufferState model).Selection.IsSome
    | Not c          -> not (evalCond c model)
    | AllOf cs       -> cs |> List.forall (fun c -> evalCond c model)
    | AnyOf cs       -> cs |> List.exists (fun c -> evalCond c model)
    // …
```

`Chain` accumulates effects in order. A `Chain` step that itself produces a
notification overwrites earlier ones (acceptable; document it).

### 6.5 Stateful actions — the tri-state sidebar (worked example)

Full rationale in research §5.4. Default keymap entries that reproduce
today's `Ctrl+B` exactly, using contexts to absorb the focus axis:

```fsharp
(chord [Ctrl] (Char 'b') ==> Chain [ HideSidebar; FocusEditor ]) |> inCtx Sidebar  // visible+focused → hide
 chord [Ctrl] (Char 'b') ==> When(SidebarVisible,
                                  FocusSidebar,                          // visible → focus
                                  Chain [ RevealSidebar; FocusSidebar ]) // hidden → reveal+focus
```

This per-context split is the **chosen default** (decision §11.1). `Ctrl+B`
is global today (fires before focus routing), so to preserve "works from the
prompt too," the `editor`-context entry is also registered under `Prompt`.
(The equivalent single `Global` smart action below is kept only to show the
two are interchangeable — it is _not_ the default we ship:)

```fsharp
chord [Ctrl] (Char 'b') ==> When(SidebarFocused,
                                 Chain [ HideSidebar; FocusEditor ],
                                 When(SidebarVisible, FocusSidebar,
                                      Chain [ RevealSidebar; FocusSidebar ]))
```

**Invariant:** every primitive is a complete transition. `HideSidebar`
clears the incremental search (today's handler does `Workspace.clearSearch`);
`FocusEditor` clears nothing else. This is what makes user-authored `Chain`s
safe — they cannot strand the model in a half-state.

### 6.6 Config file format, parser, load, reload

File: `~/.config/fedit/keybinds`. One binding per line; `#` comments; blank
lines ignored. Grammar:

```ini
line     := [context] WS stroke WS "=" WS [action]
context  := "global" | "editor" | "sidebar" | "prompt"     (default: editor)
stroke   := chord ( WS chord )*                            (space-separated sequence)
chord    := ( mod "+" )* key
mod      := "ctrl" | "alt" | "shift" | "super"   (aliases: cmd/super, opt/alt)
key      := single char | "f1".."f24" | named (enter,esc,tab,space,left,right,
            up,down,home,end,pageup,pagedown,backspace,delete)
action   := kebab-case name [":" arg]                      (empty == unbind)
```

Example:

```
# ~/.config/fedit/keybinds
editor  ctrl+s          = save
editor  ctrl+shift+p    = command-palette
editor  ctrl+k ctrl+c   = comment-line          # a sequence
sidebar enter           = sidebar-activate
editor  f6              = set-theme:gruvbox      # arg-taking action
editor  ctrl+x          =                        # unbind a default
```

Parsing (`Keymap.parseLine : string -> Result<Binding option, string>`):
`Split` on `=`, then on whitespace; active patterns turn `mod+key` tokens
into `Chord` and the kebab name into an `Action`. `None` binding-action means
unbind. Returns `Ok None` for blank/comment lines.

`KeymapIO.load : unit -> Keymap * string list` mirrors `ConfigIO.load`:
read the file if present, parse each line, collect `(lineNo, error)` messages,
and return `defaults @ validUserBindings` plus the error list. **Always
returns a working keymap** so the editor boots even with a broken file
(parse errors surface as a startup warning notification, exactly like theme
errors today).

Loading is an `Effect.LoadKeybinds` → `Msg.KeybindsLoaded of Keymap * string
list`, run at startup, on `:keybind reload`, and after the keybinds file is
saved through fedit. `runAction ReloadKeybinds` emits `LoadKeybinds`.

The internal defaults DSL (devs only) mirrors the file 1:1:

```fsharp
let private chord mods key : Chord = { Mods = Set.ofList mods; Key = key }
let private (==>) (c: Chord) action = { Stroke = [c]; Context = Editor; Action = Some action }
let private bindSeq (cs: Chord list) action = { Stroke = cs; Context = Editor; Action = Some action }
let private inCtx ctx (b: Binding) = { b with Context = ctx }

let defaults : Keymap =
    [ chord [Ctrl] (Char 's')      ==> Save
      chord [Ctrl; Shift] (Char 'p') ==> OpenPalette
      bindSeq [ chord [Ctrl] (Char 'k'); chord [Ctrl] (Char 'c') ] (Chain [ (* comment *) ])
      (chord [] (Named Enter) ==> SidebarActivate) |> inCtx Sidebar
      // … one entry per current binding, reproducing today's behavior exactly … ]
```

(`==>` binds a single chord; `bindSeq` binds a sequence; `inCtx` overrides the
default `Editor` context. The §6.5 tri-state examples use the same helpers.)

### 6.7 Plugin system & PluginApi boundary

The keybindings work touches plugins in four ways: the public API stays
frozen, the host bridge changes shape, the _reachable_ chord set for plugins
grows, and binding precedence flips. The most consequential expansion —
binding plugin commands from the keymap file — is §6.7.5.

#### 6.7.1 API surface: frozen at `apiVersion "1"`

No change to `Fedit.PluginApi`. `KeyChord` (`Char | Ctrl | Alt | CtrlShift |
F of int`), `IPluginHost.RegisterKeybinding`, `PluginCommand`, and
`Plugins.Keybindings : (KeyChord * string) list` are all unchanged. Plugins
compiled against today's `Fedit.PluginApi.dll` keep loading untouched. A
richer chord type for plugins (modifier sets, `Named` keys, sequences) is
deferred to a hypothetical `apiVersion "2"` (§2.2) and is out of scope here.

#### 6.7.2 Host bridge: `Chord.toKeyChord` replaces today's `toKeyChord`

Today the host maps editor keys to plugin chords with a private
`toKeyChord : KeyInput -> KeyChord option` (`Editor.fs`, in `runEditor`) that
handles **only** `Ctrl c`:

```fsharp
let private toKeyChord = function
    | KeyInput.Ctrl c -> Some (KeyChord.Ctrl c)
    | _ -> None
```

Phase 2 replaces `KeyInput` with `Chord`, so this becomes
`Chord.toKeyChord : Chord -> KeyChord option` (lives next to `Chord` in
`Keys.fs`; total; returns `None` for anything the v1 `KeyChord` can't name —
`Super`, `Named` keys, multi-chord sequences). Plugin bindings are matched by
mapping the incoming `Chord` to a `KeyChord` and looking it up in
`Plugins.Keybindings`, preserving today's "plugin chord → plugin command,
else parse as a built-in" fallback.

#### 6.7.3 Side effect: dormant `KeyChord` variants finally fire

`KeyChord` has always advertised `Alt`, `CtrlShift`, and `F`, but they are
**dead** in practice today: `Input.tryMap` never decodes `Ctrl+Shift+*` or
`F*` (wip-keybinds audit #5, #11), and `toKeyChord` maps only `Ctrl`. Phase
2's expanded decoder (Ctrl+Shift, F-keys, Ctrl+←/→) plus a complete
`Chord.toKeyChord` make `RegisterKeybinding(CtrlShift 'p', …)`, `Alt 'x'`,
and `F 5` reachable **for the first time — with no API change**. Flag this:
any existing plugin that registered an `Alt`/`CtrlShift`/`F` chord "for
later" will suddenly start firing once Phase 2 lands.

#### 6.7.4 Precedence flips: user keymap wins over plugins

Resolution order in Editor focus becomes:

```
user keymap (defaults ⊕ user file)  →  plugin bindings  →  text fast-path
```

This **reverses** today's order, where plugin chords are consulted before
built-in editor handling (`runEditor`; wip-keybinds #13). Rationale: a user
editing their keybinds file must be able to reclaim any chord, including one
a plugin grabbed. Consequence: a plugin can no longer shadow a chord the
(default or user) keymap binds — only chords that fall through the keymap
reach the plugin layer. Deliberate behavior change; call it out in the
changelog and in `docs/plugins.md`. Unchanged: plugin bindings stay
**editor-focus only** in v1 (they do not fire in the sidebar or prompt).

#### 6.7.5 The richer path: bind plugin commands from the keymap file

The largest expansion is indirect. `RunPlugin of source * name * arg` is an
`Action` (§4.2), and the keybinds file maps **any** stroke — `Ctrl+Shift`,
F-keys, multi-chord sequences — to an action. So a user can bind the full key
universe to a plugin command through `~/.config/fedit/keybinds`, even though
the plugin's own `RegisterKeybinding` is still limited to the v1 `KeyChord`:

```
editor  ctrl+k ctrl+w  = run-plugin:wordcount/wc     # sequence → plugin command
```

This decouples _what a plugin can bind itself to_ (limited, stable) from
_what a user can bind it to_ (the whole model), and is the migration path
that makes a v2 plugin chord type largely unnecessary for power users.
`Action.ofCommand` maps today's `PluginInvoke(source, name, arg)` command to
`RunPlugin`, so the prompt's `:plugin`-style invocation and a keymapped
invocation share one executor.

> **Open spec gap (unresolved):** §6.6's `action := kebab-name [":" arg]`
> allows only one colon-delimited argument, but `RunPlugin` needs _source_,
> _name_, **and** _arg_. The grammar/parser must define how a plugin
> invocation is spelled (lean: `run-plugin:<source>/<name>` with the
> remainder as `arg`). Resolve before Phase 3 — until then, plugins are
> keymappable only via their own `RegisterKeybinding`.

### 6.8 Prompt / Command integration

The prompt keeps its own input handling (`runPrompt`) and its text→`Command`
parsing/completion. `executeCommand` becomes:

```fsharp
let executeCommand (cmd: Command) (model: Model) = runAction (Action.ofCommand cmd) model
```

where `Action.ofCommand : Command -> Action` is a total mapping. This gives
one executor without disturbing the prompt's parse/complete surface.

The prompt's own keys (line-edit chars/cursor, Enter/Esc/Tab, history) stay
bespoke in `runPrompt` for v1 and are **not** keymap-driven — only `Global`
bindings reach the prompt (so `Ctrl+S`/`Ctrl+Q` still work while it is open).
Making prompt control keys rebindable is a deliberate later phase, not v1.

### 6.9 Discoverability

- `Keymap.index : Keymap -> Map<Action, KeyStroke list>` built at load.
- The command prompt shows the bound keystroke next to each command (reads
  the index).
- New `:keybind` verb: `:keybind` lists effective bindings;
  `:keybind reload` re-reads the file; `:keybind <stroke>` shows what a
  stroke is bound to in each context. (Hidden-from-clutter per existing
  `Spec.Hidden` convention where appropriate.)

---

## 7. Error handling

- **Malformed line:** skipped, recorded as `"keybinds:<n>: <reason>"`,
  surfaced in the startup/reload warning. Other lines still load.
- **Unknown action name:** that line is an error (kept out of the keymap).
- **Unknown context:** load error (closed enum; typo-proof).
- **Prefix conflict:** load error, sequence binding kept (§6.2).
- **Missing file:** defaults only, no error.
- **Unbind of a non-existent default:** silently ignored (idempotent).
- The editor always boots with a valid keymap (defaults floor).

---

## 8. Testing strategy

Project uses xUnit + FsCheck; `update`/`runAction`/`resolve`/`parse` are
pure, so all are unit-testable without a terminal.

- **`Input.tryMap`** table tests: each `ConsoleKeyInfo` shape → expected
  `Chord` (incl. `Ctrl+Shift`, F-keys, case folding).
- **Parser** tests: every grammar form, aliases, unbind, comments, and each
  error class → expected `Result`.
- **Resolution** tests: specificity (context > global), load order
  (user > default), unbind suppresses fallback, prefix-conflict detection.
- **Sequence engine** tests: prefix → pending → complete; timeout clears;
  Escape clears; failed sequence does not insert text.
- **`runAction`** tests: each primitive's transition; `Chain` ordering +
  effect accumulation; `When` branch selection via `evalCond`.
- **Tri-state sidebar** (regression): drive `Ctrl+B` through all three
  states and assert parity with today's behavior (snapshot the
  `(SidebarVisible, Focus)` transitions).
- **Defaults parity:** property/snapshot test that the default keymap
  reproduces the current chord set (guards the Phase-1 "no behavior change"
  claim).
- **PluginApi mapping:** `Chord.toKeyChord` round-trips the expressible
  subset; plugin bindings still fire.

---

## 9. Phasing (each milestone is independently shippable / a candidate plan)

1. **Unify actions — no behavior change.** Add `Actions.fs` (incl. `Chain`/
   `When`/`Cond` and panel/focus primitives), `runAction`/`evalCond`,
   `Action.ofCommand`. Route the global `Ctrl` handler, `runEditor`,
   `runSidebar`, and `executeCommand` through `runAction`. Tri-state `Ctrl+B`
   moves verbatim into one action. Hard-coded defaults still. Full
   defaults-parity test suite. (Pure refactor — biggest structural win.)
2. **Expand the key model.** `Keys.fs`; rewrite `Input.tryMap` to `Chord`;
   change `Msg.KeyPressed of Chord`; add `Ctrl+Shift`/F-keys/`Ctrl+←→`;
   build the sequence engine + status-bar pending indicator; fix the dead
   `Ctrl+O` (audit #4) and word-motion gaps (#8).
3. **User keymap file.** `Keymap.fs` parser + `KeymapIO.load` + overlay +
   `resolve` + unbind; `LoadKeybinds`/`KeybindsLoaded` wiring;
   `:keybind` verb; reload; docs (README keybinds section + a default
   `keybinds` example). Migrate the PluginApi boundary + ordering note.
4. **(Deferred) Macros.** See §10.

---

## 10. Macros — deferred sketch (do not build yet)

Designed so it drops into the above without rework (research §3.3):

- **Model:** `Registers: Map<char, Chord list>`, `Recording: char option`,
  `Replaying: bool`.
- **Record:** while `Recording = Some r`, `update` appends each incoming
  `Chord` to register `r` (pure — just a new Model) unless `Replaying`.
  `RecordMacro r` toggles the flag.
- **Replay:** `ReplayMacro (r, n)` emits one new `Effect.ReplayKeys (chords,
n)`; the runtime re-enqueues those `KeyPressed` msgs into the existing
  `ConcurrentQueue` (the loop already drains it each tick), with `Replaying`
  set so the recorder ignores injected keys. Stop on first no-op so
  "replay 9999×" terminates.
- **Bind a key to a macro:** `ReplayMacro` is already an `Action`, so the
  keybinds file can do `ctrl+shift+r = replay-macro:a` with no new
  machinery. Persisting a named macro = serializing its `Chord list` into
  the keybinds file.
- **Reserved keys:** per CLAUDE.md, plain `Char` chords are text input;
  record/replay use modifier chords, not bare `q`/`@`.

The only net-new primitives macros require beyond this spec: two `Model`
fields, one `Effect` (`ReplayKeys`), and the record-append hook in `update`.
Everything else (the `Action` cases, the queue, `runEffect`) already exists.

---

## 11. Review decisions (resolved)

1. **Sidebar default — per-context split** (not the single `Global` smart
   action). Default keymap uses the §6.5 split; `editor` entry mirrored under
   `Prompt` so `Ctrl+B` still works while the prompt is open.
2. **Plugin binding precedence flips** to user-keymap-first (§6.7).
   Accepted — users override plugins. Note it as a deliberate behavior change
   in the changelog.
3. **Phase 1 planned first and standalone.** The unify-actions refactor ships
   on its own (no behavior change, guarded by the defaults-parity suite);
   Phases 2–3 follow as separate plans.
4. **Keybinds file = sibling `~/.config/fedit/keybinds`** (not a block in
   `config.json`).
