import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { canonicalDocuments, toCanonical } from "./canonical.mjs";

const websiteRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = resolve(websiteRoot, "..");
const project = resolve(repoRoot, "src/Fedit/Fedit.fsproj");

const references = [
  { command: "keybinds", file: "src/data/keybindings.json" },
  { command: "themes", file: "src/data/themes.json" },
  { command: "commands", file: "src/data/commands.json" },
];

execFileSync("dotnet", ["build", project, "--nologo"], {
  cwd: repoRoot,
  stdio: ["ignore", "ignore", "inherit"],
});

let stale = false;

for (const reference of references) {
  const generated = execFileSync(
    "dotnet",
    ["run", "--project", project, "--no-build", "--", reference.command, "--json"],
    { cwd: repoRoot, encoding: "utf8" },
  );
  const committed = readFileSync(resolve(websiteRoot, reference.file), "utf8");

  if (JSON.stringify(JSON.parse(generated)) !== JSON.stringify(JSON.parse(committed))) {
    console.error(
      `${reference.file} is stale; run "just website::gen-${reference.command}" from the repository root.`,
    );
    stale = true;
  } else {
    console.log(`ok ${reference.file}`);
  }
}

for (const document of canonicalDocuments) {
  const source = toCanonical(readFileSync(resolve(repoRoot, document.source), "utf8"));
  const mirror = readFileSync(resolve(websiteRoot, document.mirror), "utf8");

  if (source !== mirror) {
    console.error(
      `${document.mirror} is stale; run "just website::sync-canonical-docs" from the repository root.`,
    );
    stale = true;
  } else {
    console.log(`ok ${document.mirror}`);
  }
}

const pluginApiSource = readFileSync(resolve(repoRoot, "src/Fedit.PluginApi/Types.fs"), "utf8");
const pluginApiProject = readFileSync(
  resolve(repoRoot, "src/Fedit.PluginApi/Fedit.PluginApi.fsproj"),
  "utf8",
);
const pluginGuide = readFileSync(resolve(websiteRoot, "src/pages/docs/plugins.astro"), "utf8");
const pluginActionBlock = pluginApiSource
  .split("type PluginAction =", 2)[1]
  ?.split("\ntype PluginCommand =", 1)[0];
const pluginActions =
  pluginActionBlock
    ?.match(/^\s*\|\s+([A-Z][A-Za-z0-9_]*)/gm)
    ?.map((line) => line.replace(/^\s*\|\s+/, "").split(/\s/, 1)[0]) ?? [];
const guideActionBlock = pluginGuide
  .split("const actions = [", 2)[1]
  ?.split("const lifecycle =", 1)[0];
const guideActions = [...(guideActionBlock?.matchAll(/name:\s*"([^"]+)"/g) ?? [])].map(
  (match) => match[1],
);
const pluginApiVersion = pluginApiProject.match(/<Version>([^<]+)<\/Version>/)?.[1];

if (JSON.stringify(pluginActions) !== JSON.stringify(guideActions)) {
  console.error(
    "src/pages/docs/plugins.astro action reference is stale against Fedit.PluginApi/Types.fs.",
  );
  stale = true;
} else {
  console.log(`ok plugin action reference (${pluginActions.length} actions)`);
}

if (!pluginApiVersion || !pluginGuide.includes(`Fedit.PluginApi ${pluginApiVersion}`)) {
  console.error("src/pages/docs/plugins.astro does not show the current Plugin API version.");
  stale = true;
} else {
  console.log(`ok plugin API version ${pluginApiVersion}`);
}

if (stale) process.exitCode = 1;
