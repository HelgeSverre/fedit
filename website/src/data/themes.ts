/**
 * Theme palettes for the website previews.
 *
 * Source of truth: the bundled F# themes in `src/Fedit/Themes.fs` (`Themes.all`).
 * The data is generated — DO NOT hand-edit the values. Regenerate with
 * `just website::gen-themes` (which runs `fedit themes --json`) after changing
 * Themes.fs; `themes.json` is committed so site builds don't need the binary.
 *
 * A `null` surface means the theme keeps the terminal's default for that region
 * (the dark themes leave editor/gutter/sidebar backgrounds untouched). The
 * preview component falls back to a neutral canvas for those.
 */
import themesJson from "./themes.json";

export interface ThemeSyntax {
  keyword: string | null;
  string: string | null;
  comment: string | null;
  function: string | null;
  type: string | null;
}

export interface Theme {
  name: string;
  description: string;
  isDefault: boolean;
  appearance: "light" | "dark";
  accent: string;
  surfaceFg: string | null;
  surfaceBg: string | null;
  chromeFg: string | null;
  chromeBg: string | null;
  promptFg: string | null;
  promptBg: string | null;
  lineNumberFg: string | null;
  lineNumberBg: string | null;
  activeLineFg: string | null;
  activeLineBg: string | null;
  currentLine: string | null;
  currentLineBg: string | null;
  statusFg: string | null;
  statusBg: string | null;
  selectionFg: string | null;
  selectedBg: string | null;
  syntax: ThemeSyntax;
}

export const themes: Theme[] = themesJson as unknown as Theme[];
