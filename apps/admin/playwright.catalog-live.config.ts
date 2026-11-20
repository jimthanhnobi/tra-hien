import { defineConfig } from "@playwright/test";

// Opt-in disposable PG17/API/Admin/TLS stack. Never targets a shared environment by default.
export default defineConfig({
  testDir: "tests/catalog-live", workers: 1, retries: 0, timeout: 180_000,
  outputDir: "../../artifacts/p1-11-integration/live-results", reporter: "list",
  use: { baseURL: "https://admin.localhost:9443", browserName: "chromium", trace: "off", screenshot: "off", video: "off" }
});
