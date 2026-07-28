/**
 * Single source for the docs sidebar and the /docs hub cards.
 * Adding a doc is one entry here plus the page file.
 */
export interface DocEntry {
  href: string;
  label: string;
  summary: string;
  group: "use" | "language tooling" | "automate + extend" | "internals";
}

export const docsNav: DocEntry[] = [
  {
    href: "/commands",
    label: "Commands",
    summary: "Every command-palette verb, generated from the parser specs.",
    group: "use",
  },
  {
    href: "/docs/keybindings",
    label: "Keybindings",
    summary: "Default and reserved gestures plus every configurable action.",
    group: "use",
  },
  {
    href: "/docs/hex",
    label: "Hex + binary files",
    summary: "Inspect and edit raw bytes without passing through text encoding.",
    group: "use",
  },
  {
    href: "/docs/lsp",
    label: "Language servers",
    summary: "Configure servers, navigate code, inspect diagnostics, and troubleshoot.",
    group: "language tooling",
  },
  {
    href: "/docs/syntax-highlighting",
    label: "Syntax highlighting",
    summary: "Tree-sitter languages, grammar updates, themes, and parse flow.",
    group: "language tooling",
  },
  {
    href: "/docs/macros",
    label: "Macros",
    summary: "Record semantic steps, replay them, and keep registers on disk.",
    group: "automate + extend",
  },
  {
    href: "/docs/plugins",
    label: "Plugin guide",
    summary: "Build trusted F# plugins for the out-of-process JSON-RPC host.",
    group: "automate + extend",
  },
  {
    href: "/plugins",
    label: "Reference plugins",
    summary: "Six bundled examples covering commands, buffers, navigation, and actions.",
    group: "automate + extend",
  },
  {
    href: "/docs/architecture",
    label: "Architecture",
    summary: "The deterministic editor core and its runtime process boundaries.",
    group: "internals",
  },
];
