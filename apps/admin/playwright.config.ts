import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "tests/browser", fullyParallel: false, workers: 1, retries: 0,
  timeout: 45_000, expect: { timeout: 12_000 },
  outputDir: "../../artifacts/p0-12-browser", reporter: "list",
  use: { baseURL: "http://127.0.0.1:3101", browserName: "chromium", trace: "off", screenshot: "off" },
  webServer: { command: "corepack pnpm exec next dev --hostname 127.0.0.1 --port 3101", env: { ADMIN_TEST_DIST_DIR: ".next-p0-12-test" }, url: "http://127.0.0.1:3101", reuseExistingServer: false, timeout: 120_000 }
});
