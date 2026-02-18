import type { MetadataRoute } from "next";

export default function robots(): MetadataRoute.Robots {
  return {
    rules: [
      {
        userAgent: "*",
        allow: ["/landing", "/about", "/docs", "/roadmap", "/privacy", "/terms"],
        disallow: [
          "/",
          "/fleet-health",
          "/health-check",
          "/usage-metrics",
          "/audit",
          "/platform-usage-metrics",
          "/progress",
          "/gather-rules",
          "/analyze-rules",
          "/sessions/",
          "/diagnosis/",
          "/admin-configuration",
          "/settings",
          "/preview",
        ],
      },
    ],
    sitemap: "https://autopilotmonitor.com/sitemap.xml",
  };
}
