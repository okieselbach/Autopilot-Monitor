import type { MetadataRoute } from "next";
import { NAV_SECTIONS } from "./docs/docsNavSections";
import { PAGE_LASTMOD } from "../lib/page-lastmod.generated";

const BASE_URL = "https://www.autopilotmonitor.com";

function lastmod(urlPath: string): Date {
  const iso = PAGE_LASTMOD[urlPath];
  return iso ? new Date(iso) : new Date();
}

export default function sitemap(): MetadataRoute.Sitemap {
  const docsSections: MetadataRoute.Sitemap = NAV_SECTIONS.map((section) => ({
    url: `${BASE_URL}/docs/${section.id}`,
    lastModified: lastmod(`/docs/${section.id}`),
    changeFrequency: "weekly",
    priority: 0.8,
  }));

  return [
    {
      url: `${BASE_URL}/`,
      lastModified: lastmod("/"),
      changeFrequency: "monthly",
      priority: 1,
    },
    {
      url: `${BASE_URL}/docs`,
      lastModified: lastmod("/docs"),
      changeFrequency: "weekly",
      priority: 0.9,
    },
    ...docsSections,
    {
      url: `${BASE_URL}/about`,
      lastModified: lastmod("/about"),
      changeFrequency: "monthly",
      priority: 0.7,
    },
    {
      url: `${BASE_URL}/roadmap`,
      lastModified: lastmod("/roadmap"),
      changeFrequency: "weekly",
      priority: 0.7,
    },
    {
      url: `${BASE_URL}/changelog`,
      lastModified: lastmod("/changelog"),
      changeFrequency: "monthly",
      priority: 0.5,
    },
    {
      url: `${BASE_URL}/privacy`,
      lastModified: lastmod("/privacy"),
      changeFrequency: "yearly",
      priority: 0.3,
    },
    {
      url: `${BASE_URL}/terms`,
      lastModified: lastmod("/terms"),
      changeFrequency: "yearly",
      priority: 0.3,
    },
  ];
}
