/**
 * Sample plugin catalog for the /plugins page.
 *
 * THIS IS A PREVIEW CATALOG. There is no public plugin registry yet. The six
 * entries with `bundled: true` are the real reference plugins under
 * `examples/` (manifest data mirrored from examples/<name>/plugin.json); the
 * rest are plausible placeholders to show what a registry listing would look
 * like.
 *
 * The page embeds this array as JSON and drives Alpine.js search/sort/filter
 * client-side. Keep field shapes flat — Alpine reads them directly.
 */
export interface Plugin {
  /** plugin.json name — also the install target name. */
  name: string;
  version: string;
  apiVersion: string;
  description: string;
  author: string;
  homepage: string;
  /** Tags for filtering. Lowercase, hyphen-separated. */
  tags: string[];
  /** Argument passed to `fedit plugins install <source>`. */
  source: string;
  /** Placeholder install count. Real plugins carry no number (no registry). */
  downloads: number;
  /** ISO date (YYYY-MM-DD) of the last update. */
  updated: string;
  /** True for the six real bundled reference plugins. */
  bundled?: boolean;
}

export const plugins: Plugin[] = [
  // ----- real bundled reference plugins (examples/) -----
  {
    name: "wordcount",
    version: "0.1.0",
    apiVersion: "1",
    description: "Adds :wc to count words in the active buffer.",
    author: "fedit maintainers",
    homepage: "https://github.com/HelgeSverre/fedit",
    tags: ["text", "official"],
    source: "https://github.com/HelgeSverre/fedit",
    downloads: 1842,
    updated: "2025-05-19",
    bundled: true,
  },
  {
    name: "journal",
    version: "0.1.0",
    apiVersion: "1",
    description: "Adds :journal to insert a local timestamp at the cursor.",
    author: "fedit maintainers",
    homepage: "https://github.com/HelgeSverre/fedit",
    tags: ["editing", "official"],
    source: "https://github.com/HelgeSverre/fedit",
    downloads: 1190,
    updated: "2025-05-19",
    bundled: true,
  },
  {
    name: "todo-count",
    version: "0.1.0",
    apiVersion: "1",
    description: "Counts TODO: markers across the workspace.",
    author: "fedit maintainers",
    homepage: "https://github.com/HelgeSverre/fedit",
    tags: ["workspace", "official"],
    source: "https://github.com/HelgeSverre/fedit",
    downloads: 974,
    updated: "2025-05-19",
    bundled: true,
  },
  {
    name: "todo-list",
    version: "0.1.0",
    apiVersion: "1",
    description: "Lists TODO: markers in the workspace with file:line context.",
    author: "fedit maintainers",
    homepage: "https://github.com/HelgeSverre/fedit",
    tags: ["workspace", "official"],
    source: "https://github.com/HelgeSverre/fedit",
    downloads: 861,
    updated: "2025-05-19",
    bundled: true,
  },
  {
    name: "todo-next",
    version: "0.1.0",
    apiVersion: "1",
    description: "Jumps the cursor to the next TODO: in the active buffer (wraps).",
    author: "fedit maintainers",
    homepage: "https://github.com/HelgeSverre/fedit",
    tags: ["navigation", "official"],
    source: "https://github.com/HelgeSverre/fedit",
    downloads: 803,
    updated: "2025-05-19",
    bundled: true,
  },
  {
    name: "jot",
    version: "0.1.0",
    apiVersion: "1",
    description: "Session scratchpad: jot code locations, check them off, jump back.",
    author: "fedit maintainers",
    homepage: "https://github.com/HelgeSverre/fedit",
    tags: ["workspace", "navigation", "official"],
    source: "https://github.com/HelgeSverre/fedit",
    downloads: 0,
    updated: "2026-06-11",
    bundled: true,
  },

  // ----- placeholder catalog (preview only) -----
  {
    name: "vim-bindings",
    version: "1.4.2",
    apiVersion: "1",
    description:
      "Modal editing keymap: normal, insert, and visual modes mapped onto fedit chords. hjkl motion, dw/dd, and a : command bridge.",
    author: "kaja",
    homepage: "https://github.com/kaja/fedit-vim",
    tags: ["navigation", "keybindings"],
    source: "gh:kaja/fedit-vim",
    downloads: 12480,
    updated: "2026-05-02",
  },
  {
    name: "git-blame",
    version: "0.9.0",
    apiVersion: "1",
    description:
      "Annotates the current line with the last commit's author and short hash. Reads git CLI; no live state held.",
    author: "torvald",
    homepage: "https://github.com/torvald/fedit-git-blame",
    tags: ["git", "workspace"],
    source: "gh:torvald/fedit-git-blame",
    downloads: 9320,
    updated: "2026-04-21",
  },
  {
    name: "bracket-match",
    version: "0.3.1",
    apiVersion: "1",
    description:
      "Jumps the cursor to the matching bracket under the caret. Handles (), [], {}, and <>.",
    author: "nori",
    homepage: "https://github.com/nori/fedit-bracket-match",
    tags: ["navigation", "editing"],
    source: "gh:nori/fedit-bracket-match",
    downloads: 6105,
    updated: "2026-03-30",
  },
  {
    name: "markdown-preview",
    version: "2.0.0",
    apiVersion: "1",
    description:
      "Renders the active Markdown buffer to plain text in the dock — headings, lists, and code fences flattened for a quick read.",
    author: "sol",
    homepage: "https://github.com/sol/fedit-md-preview",
    tags: ["text", "markdown"],
    source: "gh:sol/fedit-md-preview",
    downloads: 8744,
    updated: "2026-05-11",
  },
  {
    name: "rainbow-indent",
    version: "0.2.4",
    apiVersion: "1",
    description:
      "Reports the indent depth of the current line and the surrounding block. Useful in deep YAML.",
    author: "iris",
    homepage: "https://github.com/iris/fedit-rainbow-indent",
    tags: ["editing", "text"],
    source: "gh:iris/fedit-rainbow-indent",
    downloads: 3290,
    updated: "2026-02-14",
  },
  {
    name: "trim-trailing",
    version: "1.1.0",
    apiVersion: "1",
    description: "Strips trailing whitespace from every line of the active buffer in one command.",
    author: "bjorn",
    homepage: "https://github.com/bjorn/fedit-trim-trailing",
    tags: ["editing", "formatting"],
    source: "gh:bjorn/fedit-trim-trailing",
    downloads: 5560,
    updated: "2026-04-02",
  },
  {
    name: "sort-lines",
    version: "0.7.0",
    apiVersion: "1",
    description:
      "Sorts the selected lines alphabetically, case-insensitive, with a flag to reverse.",
    author: "mira",
    homepage: "https://github.com/mira/fedit-sort-lines",
    tags: ["editing", "text"],
    source: "gh:mira/fedit-sort-lines",
    downloads: 4112,
    updated: "2026-01-28",
  },
  {
    name: "uuid-insert",
    version: "1.0.3",
    apiVersion: "1",
    description:
      "Inserts a v4 UUID at the cursor. Bind it to a chord for one-keystroke identifiers.",
    author: "dag",
    homepage: "https://github.com/dag/fedit-uuid",
    tags: ["editing", "snippets"],
    source: "gh:dag/fedit-uuid",
    downloads: 2870,
    updated: "2026-03-09",
  },
  {
    name: "json-format",
    version: "1.2.1",
    apiVersion: "1",
    description:
      "Pretty-prints the active buffer as JSON with two-space indent. Reports parse errors in the dock.",
    author: "vega",
    homepage: "https://github.com/vega/fedit-json-format",
    tags: ["formatting", "text"],
    source: "gh:vega/fedit-json-format",
    downloads: 7015,
    updated: "2026-05-18",
  },
  {
    name: "open-recent",
    version: "0.5.0",
    apiVersion: "1",
    description:
      "Lists the 20 most recently opened files from config and reopens the one you pick.",
    author: "elin",
    homepage: "https://github.com/elin/fedit-open-recent",
    tags: ["workspace", "navigation"],
    source: "gh:elin/fedit-open-recent",
    downloads: 3640,
    updated: "2026-02-26",
  },
  {
    name: "line-jump",
    version: "0.4.2",
    apiVersion: "1",
    description: "Jumps to a relative line offset from the cursor — :j +12 down, :j -5 up.",
    author: "nori",
    homepage: "https://github.com/nori/fedit-line-jump",
    tags: ["navigation"],
    source: "gh:nori/fedit-line-jump",
    downloads: 1995,
    updated: "2026-01-15",
  },
  {
    name: "case-convert",
    version: "1.3.0",
    apiVersion: "1",
    description:
      "Converts the selection between camelCase, snake_case, kebab-case, and SCREAMING_CASE.",
    author: "saga",
    homepage: "https://github.com/saga/fedit-case",
    tags: ["editing", "text"],
    source: "gh:saga/fedit-case",
    downloads: 4880,
    updated: "2026-04-29",
  },
  {
    name: "word-frequency",
    version: "0.2.0",
    apiVersion: "1",
    description:
      "Counts the ten most frequent words in the active buffer and lists them in the dock.",
    author: "frida",
    homepage: "https://github.com/frida/fedit-word-frequency",
    tags: ["text", "analysis"],
    source: "gh:frida/fedit-word-frequency",
    downloads: 1420,
    updated: "2026-02-03",
  },
  {
    name: "toc-outline",
    version: "0.6.1",
    apiVersion: "1",
    description:
      "Builds a heading outline from Markdown or code comments and jumps to any entry you select.",
    author: "sol",
    homepage: "https://github.com/sol/fedit-toc",
    tags: ["markdown", "navigation"],
    source: "gh:sol/fedit-toc",
    downloads: 3155,
    updated: "2026-03-22",
  },
  {
    name: "snippet-expand",
    version: "1.0.0",
    apiVersion: "1",
    description:
      "Expands a short trigger word into a saved snippet at the cursor. Snippets live in a config file.",
    author: "dag",
    homepage: "https://github.com/dag/fedit-snippets",
    tags: ["snippets", "editing"],
    source: "gh:dag/fedit-snippets",
    downloads: 6230,
    updated: "2026-05-07",
  },
  {
    name: "fzf-files",
    version: "0.8.3",
    apiVersion: "1",
    description:
      "Fuzzy-finds a file across the workspace and opens it. Shells out to fzf when present.",
    author: "kaja",
    homepage: "https://github.com/kaja/fedit-fzf",
    tags: ["workspace", "navigation"],
    source: "gh:kaja/fedit-fzf",
    downloads: 8990,
    updated: "2026-05-14",
  },
  {
    name: "hex-color",
    version: "0.3.0",
    apiVersion: "1",
    description:
      "Reads the hex color under the cursor and reports its RGB and 256-color ANSI index.",
    author: "iris",
    homepage: "https://github.com/iris/fedit-hex-color",
    tags: ["text", "analysis"],
    source: "gh:iris/fedit-hex-color",
    downloads: 1120,
    updated: "2026-01-09",
  },
  {
    name: "duplicate-line",
    version: "1.0.1",
    apiVersion: "1",
    description:
      "Duplicates the current line below the cursor. Bind to a chord for quick repetition.",
    author: "bjorn",
    homepage: "https://github.com/bjorn/fedit-duplicate-line",
    tags: ["editing"],
    source: "gh:bjorn/fedit-duplicate-line",
    downloads: 4405,
    updated: "2026-04-11",
  },
  {
    name: "git-branch",
    version: "0.5.2",
    apiVersion: "1",
    description: "Shows the current git branch and dirty-state count in a dock notification.",
    author: "torvald",
    homepage: "https://github.com/torvald/fedit-git-branch",
    tags: ["git", "workspace"],
    source: "gh:torvald/fedit-git-branch",
    downloads: 5710,
    updated: "2026-04-25",
  },
  {
    name: "base64",
    version: "1.1.2",
    apiVersion: "1",
    description: "Encodes or decodes the selection as Base64. Two commands, no dependencies.",
    author: "vega",
    homepage: "https://github.com/vega/fedit-base64",
    tags: ["editing", "text"],
    source: "gh:vega/fedit-base64",
    downloads: 2240,
    updated: "2026-02-19",
  },
  {
    name: "spell-flag",
    version: "0.1.5",
    apiVersion: "1",
    description:
      "Flags words in the buffer that miss a local dictionary file and lists them with line numbers.",
    author: "frida",
    homepage: "https://github.com/frida/fedit-spell-flag",
    tags: ["text", "analysis"],
    source: "gh:frida/fedit-spell-flag",
    downloads: 1680,
    updated: "2026-03-04",
  },
];
