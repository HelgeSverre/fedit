# Voice — fedit

Five rules. They apply to README, landing page, CLI help, error messages, release notes, and commit messages.

## The Five

### 1. Lead with the verb.

> Edits files in the terminal.

Not:

> A small terminal text editor that lets you edit files.

The verb does the work. Drop the framing.

### 2. No marketing adjectives. Numbers or nothing.

> Opens a 200MB log file in under 80ms.

Not:

> Blazing fast, even for huge files.

If you don't have a number, drop the claim.

### 3. You, never "users" or "developers".

> Open a workspace with `fedit .`

Not:

> Users can open a workspace by running `fedit .`

### 4. Show, don't say.

A four-line terminal frame beats a paragraph. A working code snippet beats a feature description. If a screenshot would say it better, use the screenshot — but the screenshot is a real terminal, not a polished mockup.

> ```
> $ fedit .
> ^ workspace · 47 files · readonly
> ```

Not:

> fedit provides an intuitive workspace view that displays your project structure.

### 5. No emoji. One em-dash per paragraph, max.

The brand uses the caret symbol when it needs a visual moment. Emoji break the typographic plane and signal a different register. Em-dash chains — like this — then continuing — like that — are the LLM prose signature. One em-dash is fine. Three is the tell.

## Things to Avoid

| Avoid                                        | Why                                                                                   |
| -------------------------------------------- | ------------------------------------------------------------------------------------- |
| "Build the future of editing."               | Vacant.                                                                               |
| "Your all-in-one terminal solution."         | Vacant.                                                                               |
| "Powered by F#" (in a marketing way)         | Cargo cult — only mention F# where the implementation language is genuinely relevant. |
| "Blazing fast", "lightning fast"             | Adjective without a number.                                                           |
| "Simply", "just", "easily"                   | Lies or condescends, often both.                                                      |
| "It's not just an editor — it's a workflow." | The LLM prose tell.                                                                   |

## Applied: README opening

**Before** (typical):

> fedit is a modern terminal text editor that makes file editing fast and easy. With its intuitive interface and powerful features, fedit transforms how you work with files.

**After** (voice rules applied):

> fedit edits files in the terminal. Small, fast, written in F#. Opens a workspace, shows a file tree, edits files, saves to disk.

Half the length. Says more.

## Applied: CLI help

**Before**:

> Welcome to fedit! 🚀
> Usage: fedit [options]
> Easily edit your files from the command line.

**After**:

```
fedit — edit files in the terminal

USAGE
  fedit [options] <path>

OPTIONS
  -h, --help       show this help
  -v, --version    show version
  --readonly       open in readonly mode
  --line N         open at line N

EXAMPLES
  fedit .
  fedit src/Main.fs
  fedit --line 47 README.md
```

## Applied: commit messages

**Before**: `✨ feat: Add amazing new search functionality for users`

**After**: `add search command (--search <pattern>)`

Verb. Specific. No emoji. No adjectives.
