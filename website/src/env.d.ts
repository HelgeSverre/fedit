/// <reference types="astro/client" />

// Alpine.js ships no bundled type declarations and is used only on the
// /plugins page. Declare the minimal surface we touch so `astro check`
// stays strict without pulling a heavy @types dependency.
interface AlpineApi {
  data<T extends object>(name: string, factory: () => T & ThisType<T>): void;
  plugin(plugin: unknown): void;
  start(): void;
}

declare module "alpinejs" {
  const Alpine: AlpineApi;
  export default Alpine;
}

declare module "@alpinejs/focus" {
  const focus: unknown;
  export default focus;
}

interface Window {
  Alpine: AlpineApi;
}
