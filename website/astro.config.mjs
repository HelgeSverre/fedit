import { defineConfig } from "astro/config";
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
    enabled: false,
  },
});
