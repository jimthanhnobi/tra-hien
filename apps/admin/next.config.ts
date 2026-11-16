import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  transpilePackages: ["@teapremium/api-client"],
  async rewrites() {
    const apiUrl = process.env.API_INTERNAL_URL;
    return apiUrl
      ? [{ source: "/api/:path*", destination: `${apiUrl}/api/:path*` }]
      : [];
  }
};

export default nextConfig;
