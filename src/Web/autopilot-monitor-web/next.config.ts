import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  reactStrictMode: true,
  async redirects() {
    return [
      // Permanent redirect for old /landing URL — all SEO equity flows to /
      { source: "/landing", destination: "/", permanent: true },
      // Docs index redirects to default section
      { source: "/docs", destination: "/docs/overview", permanent: false },
    ];
  },
};

export default nextConfig;
