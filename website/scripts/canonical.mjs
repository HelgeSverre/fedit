/**
 * Single source of truth for the canonical-doc mirror: which repo docs
 * are mirrored into src/content/canonical/, and the transform applied on
 * the way in. sync-canonical-docs.mjs writes with it; verify-generated.mjs
 * checks with it — keep them in lockstep by keeping the logic here.
 */
export const canonicalDocuments = [
  { source: "docs/lsp.md", mirror: "src/content/canonical/lsp.md" },
  { source: "docs/macros.md", mirror: "src/content/canonical/macros.md" },
];

/**
 * Repo docs title themselves "fedit <topic>" for standalone reading; on
 * the site the prefix is redundant and wraps the h1. Strip it from the
 * title only — body text is untouched.
 */
export function toCanonical(markdown) {
  return markdown.replace(/^# fedit (.)/m, (_, first) => `# ${first.toUpperCase()}`);
}
