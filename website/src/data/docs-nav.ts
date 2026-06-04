/**
 * Single source for the docs sidebar and the /docs hub cards.
 * Adding a doc is one entry here plus the page file.
 */
export interface DocEntry {
  href: string;
  label: string;
  summary: string;
}

export const docsNav: DocEntry[] = [
  {
    href: "/docs/plugins",
    label: "Plugin guide",
    summary: "Write a plugin: one .fs file, no IPC, no JSON manifests.",
  },
  {
    href: "/docs/keybindings",
    label: "Keybindings",
    summary: "Every binding plus every unbound action — searchable, filterable by context.",
  },
  {
    href: "/docs/architecture",
    label: "Architecture",
    summary: "How fedit is built: the pure MVU loop and piece-table buffers.",
  },
];
