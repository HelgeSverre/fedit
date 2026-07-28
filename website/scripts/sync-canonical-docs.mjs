import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { canonicalDocuments, toCanonical } from "./canonical.mjs";

const websiteRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = resolve(websiteRoot, "..");

mkdirSync(resolve(websiteRoot, "src/content/canonical"), { recursive: true });

for (const { source, mirror } of canonicalDocuments) {
  const markdown = toCanonical(readFileSync(resolve(repoRoot, source), "utf8"));
  writeFileSync(resolve(websiteRoot, mirror), markdown);
  console.log(`synced ${mirror}`);
}
