import { defineConfig } from "@playwright/test";

// Opt-in disposable stack. The browser must trust the local CA.
export default defineConfig({
  testDir: "tests/products-live", workers: 1, retries: 0, timeout: 180_000,
  outputDir: "../../artifacts/p1-12/live-results", reporter: "list",
  use: { baseURL: "https://admin.localhost:9444", browserName: "chromium", trace: "off", screenshot: "off", video: "off" }
});
