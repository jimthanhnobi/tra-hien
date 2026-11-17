import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  transpilePackages: ["@teapremium/api-client"],
  distDir: process.env.ADMIN_TEST_DIST_DIR ?? ".next",
  allowedDevOrigins: ["admin.localhost"],
  async headers() {
    return [{ source: "/:path*", headers: [
      { key: "Cache-Control", value: "private, no-store, max-age=0" },
      { key: "X-Robots-Tag", value: "noindex, nofollow" },
      { key: "Referrer-Policy", value: "no-referrer" },
      { key: "X-Content-Type-Options", value: "nosniff" },
      { key: "X-Frame-Options", value: "DENY" }
    ] }];
  }
};

export default nextConfig;
