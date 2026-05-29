import { defineConfig, fontProviders } from "astro/config";
import sitemap from "@astrojs/sitemap";

import inspectClip from "astro-inspect-clip";

// https://astro.build/config
export default defineConfig({
  site: "https://fedit.dev",
  integrations: [sitemap(), inspectClip()],
  build: {
    inlineStylesheets: "auto",
  },
  devToolbar: {
    enabled: true,
  },
  experimental: {
    chromeDevtoolsWorkspace: true,
    rustCompiler: true,
  },
  fonts: [
    {
      provider: fontProviders.fontsource(),
      name: "JetBrains Mono",
      cssVariable: "--font-jetbrains-mono",
      weights: [400, 500, 700],
      styles: ["normal"],
      subsets: ["latin"],
    },
    {
      provider: fontProviders.local(),
      name: "Departure Mono",
      cssVariable: "--font-departure-mono",
      options: {
        variants: [
          {
            src: [
              "./node_modules/@proj-airi/font-departure-mono/dist/files/DepartureMono-Regular.woff2",
            ],
            weight: "normal",
            style: "normal",
          },
        ],
      },
    },
  ],
});
