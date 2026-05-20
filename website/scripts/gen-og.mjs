// gen-og.mjs — generate Open Graph cards (1200×630) per page.
//
// Renders brand-canonical OG images:
//   caret + wordmark + page title + lede, on neutral-950, accent moment once.
// Pulls fonts from node_modules (Departure Mono OTF, JetBrains Mono WOFF2).
//
// Run with `bun scripts/gen-og.mjs` or `just og`.

import { readFile, writeFile, mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, join, resolve } from "node:path";
import satori from "satori";
import { Resvg } from "@resvg/resvg-js";

const __dirname = dirname(fileURLToPath(import.meta.url));
const root = resolve(__dirname, "..");
const outDir = join(root, "public", "og");

// ─── brand tokens ──────────────────────────────────────────────────────────
const BG = "#0B0B0D"; // neutral-950
const FG = "#FAFAFA"; // neutral-50
const FG_MUTED = "#A6A6AD"; // neutral-400
const FG_SUBTLE = "#787880"; // neutral-500
const BORDER = "#27272A"; // neutral-800
const ACCENT = "#00B86B"; // phosphor green

const THEME_SWATCHES = [
  "#00B86B",
  "#1F6FEB",
  "#D2691E",
  "#5FD7FF",
  "#5FD7D7",
  "#FFD700",
  "#FF5F5F",
];

// ─── fonts ─────────────────────────────────────────────────────────────────
const fonts = [
  {
    name: "Departure Mono",
    data: await readFile(
      join(root, "node_modules/@proj-airi/font-departure-mono/dist/files/DepartureMono-Regular.otf")
    ),
    weight: 400,
    style: "normal",
  },
  {
    name: "JetBrains Mono",
    data: await readFile(
      join(
        root,
        "node_modules/@fontsource/jetbrains-mono/files/jetbrains-mono-latin-400-normal.woff"
      )
    ),
    weight: 400,
    style: "normal",
  },
  {
    name: "JetBrains Mono",
    data: await readFile(
      join(
        root,
        "node_modules/@fontsource/jetbrains-mono/files/jetbrains-mono-latin-500-normal.woff"
      )
    ),
    weight: 500,
    style: "normal",
  },
];

// ─── pages ─────────────────────────────────────────────────────────────────
const pages = [
  {
    slug: "default",
    eyebrow: "fedit.dev",
    title: "fedit",
    lede: "Edit files in the terminal. Small. Written in F#.",
  },
  {
    slug: "index",
    eyebrow: "fedit.dev",
    title: "fedit",
    lede: "Edit files in the terminal. Small. Written in F#.",
  },
  {
    slug: "commands",
    eyebrow: "fedit.dev / commands",
    title: "commands",
    lede: "Keybindings, find, the command bar, and everything you can type after Ctrl+P.",
  },
  {
    slug: "themes",
    eyebrow: "fedit.dev / themes",
    title: "themes",
    lede: "Seven accent palettes. Switch live from the command bar.",
    swatches: true,
  },
  {
    slug: "how",
    eyebrow: "fedit.dev / how",
    title: "how it works",
    lede: "Pure-data MVU loop. Piece-table buffers. Effects on the thread pool, messages on the main loop.",
  },
  {
    slug: "brand",
    eyebrow: "fedit.dev / brand",
    title: "brand",
    lede: "The caret. Departure Mono + JetBrains Mono. Phosphor green. Seven themes. Voice rules.",
  },
];

// ─── helpers ───────────────────────────────────────────────────────────────
// satori wants React-style vdom; build it without JSX. Filter falsy children
// because satori complains about multi-child parents that aren't display:flex.
const h = (type, props = {}, ...children) => ({
  type,
  props: {
    ...props,
    children: children.flat().filter((c) => c !== null && c !== false && c !== undefined),
  },
});

const Caret = ({ size = 220, stroke = ACCENT, strokeWidth = 22 }) =>
  h(
    "svg",
    {
      width: size,
      height: size,
      viewBox: "0 0 240 240",
    },
    h("path", {
      d: "M 30 160 L 120 70 L 210 160",
      stroke,
      strokeWidth,
      strokeLinecap: "square",
      strokeLinejoin: "miter",
      fill: "none",
    })
  );

const ThemeStrip = () =>
  h(
    "div",
    {
      style: {
        display: "flex",
        gap: 14,
        marginTop: 36,
      },
    },
    ...THEME_SWATCHES.map((hex) =>
      h("div", {
        style: {
          display: "flex",
          width: 84,
          height: 36,
          background: hex,
          borderRadius: 2,
        },
      })
    )
  );

// 8px-rule "tape" mark in the corner: subtle accent rectangle.
const CornerTape = () =>
  h("div", {
    style: {
      display: "flex",
      position: "absolute",
      left: 72,
      top: 72,
      width: 56,
      height: 4,
      background: ACCENT,
    },
  });

const Card = (page) =>
  h(
    "div",
    {
      style: {
        position: "relative",
        width: 1200,
        height: 630,
        background: BG,
        display: "flex",
        flexDirection: "column",
        justifyContent: "space-between",
        padding: "72px 80px",
        fontFamily: "JetBrains Mono",
        color: FG,
      },
    },
    CornerTape(),
    // top row: eyebrow
    h(
      "div",
      {
        style: {
          display: "flex",
          fontSize: 18,
          letterSpacing: "0.18em",
          textTransform: "uppercase",
          color: FG_SUBTLE,
          marginTop: 36,
        },
      },
      page.eyebrow
    ),
    // middle row: caret + title block
    h(
      "div",
      {
        style: {
          display: "flex",
          alignItems: "center",
          gap: 56,
          flex: 1,
        },
      },
      Caret({}),
      h(
        "div",
        {
          style: {
            display: "flex",
            flexDirection: "column",
            flex: 1,
          },
        },
        h(
          "div",
          {
            style: {
              display: "flex",
              fontFamily: "Departure Mono",
              fontSize: 128,
              lineHeight: 1,
              letterSpacing: "-0.03em",
              color: FG,
            },
          },
          page.title
        ),
        h(
          "div",
          {
            style: {
              display: "flex",
              fontFamily: "JetBrains Mono",
              fontSize: 28,
              lineHeight: 1.45,
              color: FG_MUTED,
              marginTop: 32,
              maxWidth: 760,
            },
          },
          page.lede
        ),
        page.swatches ? ThemeStrip() : null
      )
    ),
    // bottom row: hairline + footer
    h(
      "div",
      {
        style: {
          display: "flex",
          flexDirection: "column",
        },
      },
      h("div", {
        style: { display: "flex", width: "100%", height: 1, background: BORDER, marginBottom: 20 },
      }),
      h(
        "div",
        {
          style: {
            display: "flex",
            justifyContent: "space-between",
            fontSize: 18,
            color: FG_SUBTLE,
          },
        },
        h("div", { style: { display: "flex" } }, "github.com/HelgeSverre/fedit"),
        h("div", { style: { display: "flex", color: ACCENT } }, "^")
      )
    )
  );

// ─── render ────────────────────────────────────────────────────────────────
await mkdir(outDir, { recursive: true });

for (const page of pages) {
  const svg = await satori(Card(page), {
    width: 1200,
    height: 630,
    fonts,
  });

  const png = new Resvg(svg, {
    background: BG,
    fitTo: { mode: "width", value: 1200 },
  })
    .render()
    .asPng();

  const out = join(outDir, `${page.slug}.png`);
  await writeFile(out, png);
  const kb = (png.byteLength / 1024).toFixed(1);
  console.log(`  wrote ${out.replace(root + "/", "")}  (${kb} KB)`);
}

console.log(`\ndone. ${pages.length} cards in public/og/.`);
