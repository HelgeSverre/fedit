import { defineConfig } from "astro/config";

// https://astro.build/config
export default defineConfig({
  site: "https://fedit.dev",
  build: {
    inlineStylesheets: "auto",
  },
  devToolbar: {
    enabled: false,
  },
});
