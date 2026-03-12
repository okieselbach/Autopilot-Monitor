import type { MetadataRoute } from "next";

export default function robots(): MetadataRoute.Robots {
  return {
    rules: [
      {
        userAgent: "*",
        allow: ["/", "/about", "/docs", "/roadmap", "/changelog", "/privacy", "/terms"],
        disallow: [
          "/dashboard",
          "/fleet-health",
          "/health-check",
          "/usage-metrics",
          "/audit",
          "/platform-usage-metrics",
          "/platform-metrics",
          "/progress",
          "/gather-rules",
          "/analyze-rules",
          "/ime-log-patterns",
          "/geographic-performance",
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
