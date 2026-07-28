import changelogMarkdown from "../content/changelog.md?raw";

/**
 * Source: src/content/changelog.md — a curated, release-by-release log
 * (one `## vX.Y.Z — YYYY-MM-DD` section per release, `## Next` for
 * unreleased work, terse bullet items). The root CHANGELOG.md stays the
 * phase-based development log; this file is the user-facing view of it.
 * Update it as part of release prep.
 */
export interface ChangelogRelease {
  /** "v1.8.0", or "Next" for the unreleased section. */
  version: string;
  /** ISO date YYYY-MM-DD, or "" for Next. */
  date: string;
  items: string[];
}

export function loadChangelog(): ChangelogRelease[] {
  const releases: ChangelogRelease[] = [];

  for (const line of changelogMarkdown.split("\n")) {
    const heading = line.match(/^## (.+?)(?: — (\d{4}-\d{2}-\d{2}))?$/);

    if (heading) {
      releases.push({ version: heading[1], date: heading[2] ?? "", items: [] });
      continue;
    }

    if (line.startsWith("- ") && releases.length > 0) {
      releases[releases.length - 1].items.push(line.slice(2));
    }
  }

  return releases;
}

function escapeHtml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

export function renderInlineMarkdown(value: string): string {
  return escapeHtml(value)
    .replace(/`([^`]+)`/g, "<code>$1</code>")
    .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
    .replace(
      /\[([^\]]+)\]\((https?:\/\/[^)]+)\)/g,
      '<a href="$2" target="_blank" rel="noopener">$1</a>',
    );
}
