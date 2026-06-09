# Prompt Sessions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the separate picker interaction state with named prompt sessions for plugins, macros, and keybindings.

**Architecture:** Keep the existing command/file/search/buffer prompt behavior working while adding richer prompt session kinds. Move plugin/macro/keybinding active state into `PromptState`, reuse the picker semantic item/action model under prompt-owned names, and delete `Model.Picker` after tests cover the new prompt-session behavior.

**Tech Stack:** F#/.NET 10, xUnit, FsUnit, existing MVU `Editor.update` loop and pure `View.render` renderer.

---

### Task 1: Add Prompt Session State

**Files:**

- Modify: `src/Fedit/Model.fs`
- Modify: `tests/Fedit.Tests/UpdateTests.fs`

- [ ] **Step 1: Write failing tests**

Add tests proving `:plugins`, `:macros`, and `:keybind` open prompt sessions instead of `Model.Picker`.

- [ ] **Step 2: Run red test**

Run: `just test --filter "plugins command opens the plugin prompt session|macros command opens the macro prompt session|keybind command opens the keybinding prompt session"`

Expected: fail to compile or fail assertions because `PromptSessionKind` does not exist and/or commands still open `Model.Picker`.

- [ ] **Step 3: Implement state types**

Add prompt session types to `Model.fs`, extend `PromptState` with session-owned selected item and pending confirmation state, and remove `Model.Picker` once all call sites are migrated.

- [ ] **Step 4: Run green test**

Run the same filtered tests. Expected: pass.

### Task 2: Rename Picker Semantic Model To Prompt Surface

**Files:**

- Rename or replace: `src/Fedit/PickerTypes.fs`
- Rename or replace: `src/Fedit/Pickers.fs`
- Modify: `src/Fedit/Fedit.fsproj`
- Modify: `tests/Fedit.Tests/PickersTests.fs`
- Modify: `tests/Fedit.Tests/Fedit.Tests.fsproj`

- [ ] **Step 1: Write failing semantic tests**

Update picker tests to assert `PromptSurfaces.buildView` or equivalent builds plugin, macro, and keybinding prompt surfaces.

- [ ] **Step 2: Run red test**

Run: `just test --filter "plugin prompt surface|keybinding prompt surface|destructive confirmation"`

Expected: fail because prompt-surface modules/types do not exist yet.

- [ ] **Step 3: Implement prompt surface builders**

Move existing item/action/view logic from `Pickers.fs` to prompt-owned names. Keep the behavior and layouts stable.

- [ ] **Step 4: Run green test**

Run the same filtered tests. Expected: pass.

### Task 3: Unify Prompt Key Routing

**Files:**

- Modify: `src/Fedit/Editor.fs`
- Modify: `tests/Fedit.Tests/UpdateTests.fs`
- Modify: `tests/Fedit.Tests/PickersTests.fs`

- [ ] **Step 1: Write failing routing tests**

Add tests proving typing in a plugin/macro/keybinding prompt session filters through `Prompt.Text`, up/down changes shared selected state, action keys execute selected-item actions, and Escape closes the prompt session.

- [ ] **Step 2: Run red test**

Run: `just test --filter "prompt session"`

Expected: fail because picker key routing still owns those behaviors.

- [ ] **Step 3: Implement routing**

Move the picker key-routing cases into `runPrompt`/prompt helpers. Dispatch named session actions by prompt session kind.

- [ ] **Step 4: Run green test**

Run the same filtered tests. Expected: pass.

### Task 4: Render Prompt Sessions

**Files:**

- Modify: `src/Fedit/View.fs`
- Modify: `tests/Fedit.Tests/ScreenTests.fs`
- Modify: `tests/Fedit.Tests/PickersTests.fs`

- [ ] **Step 1: Write failing render tests**

Add tests proving command-line text shows `Plugins: <query>`, `Macros: <query>`, or `Keybindings: <query>`, and the dock renders the prompt surface rows.

- [ ] **Step 2: Run red test**

Run: `just test --filter "renders plugin prompt session|renders keybinding prompt session"`

Expected: fail because the renderer still special-cases `Model.Picker`.

- [ ] **Step 3: Implement rendering**

Render prompt surfaces from `PromptState` and delete the `Model.Picker` render path.

- [ ] **Step 4: Run green test**

Run the same filtered tests. Expected: pass.

### Task 5: Delete Picker Mode

**Files:**

- Delete: `src/Fedit/PickerTypes.fs`
- Delete or replace: `src/Fedit/Pickers.fs`
- Modify: `src/Fedit/Fedit.fsproj`
- Modify: `tests/Fedit.Tests/Fedit.Tests.fsproj`
- Delete or replace: `tests/Fedit.Tests/PickersTests.fs`

- [ ] **Step 1: Write deletion check**

Run `rg -n "Model\\.Picker|PickerState|Fedit\\.PickerTypes|open Fedit\\.PickerTypes|runPicker|openPicker" src tests`.

Expected before deletion: matches remain.

- [ ] **Step 2: Remove picker references**

Remove or rename all remaining picker-specific references so prompt sessions are the only transient interaction state.

- [ ] **Step 3: Run deletion check again**

Run the same `rg` command. Expected: no matches.

- [ ] **Step 4: Run full verification**

Run: `just test` and `git diff --check`.

Expected: both pass.
