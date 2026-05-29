/**
 * Source: ../../../brand/themes/*.json
 * Implementation: ../../../src/Fedit/Themes.fs
 *
 * Keep in sync with the F# Themes module. Order is the same as the in-app
 * tab-completion order.
 */
export interface Theme {
  name: string;
  description: string;
  hex: string;
  ansi256: number;
  isDefault?: boolean;
}

export const themes: Theme[] = [
  {
    name: "green",
    description: "Phosphor green — brand default",
    hex: "#00B86B",
    ansi256: 35,
    isDefault: true,
  },
  { name: "blue", description: "Electric blue — high contrast", hex: "#1F6FEB", ansi256: 33 },
  { name: "orange", description: "Burnt orange — warm, retro", hex: "#D2691E", ansi256: 166 },
  { name: "cyan", description: "Cool cyan accent", hex: "#5FD7FF", ansi256: 81 },
  { name: "teal", description: "Cyan-green hybrid", hex: "#5FD7D7", ansi256: 80 },
  { name: "yellow", description: "Warm yellow (dark text)", hex: "#FFD700", ansi256: 220 },
  { name: "red", description: "Crimson accent", hex: "#FF5F5F", ansi256: 203 },
  { name: "graphite", description: "Blue-grey high readability", hex: "#8CB4FF", ansi256: 111 },
  { name: "evergreen", description: "Soft forest green", hex: "#A7C080", ansi256: 143 },
  { name: "mono-amber", description: "Deep amber phosphor", hex: "#FFAF00", ansi256: 214 },
];
