import type { MetadataRoute } from "next";

export default function robots(): MetadataRoute.Robots {
  return {
    rules: [
      {
        userAgent: "*",
        allow: ["/", "/about", "/docs", "/roadmap", "/privacy", "/terms"],
        disallow: [
          "/dashboard",
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
    sitemap: "https://www.autopilotmonitor.com/sitemap.xml",
  };
}
