import { notFound } from "next/navigation";
import type { Metadata } from "next";
import { NAV_SECTIONS, type SectionId } from "../docsNavSections";
import { SectionPrivatePreview } from "../sections/SectionPrivatePreview";
import { SectionGeneral } from "../sections/SectionGeneral";
import { SectionOverview } from "../sections/SectionOverview";
import { SectionSetup } from "../sections/SectionSetup";
import { SectionAgent } from "../sections/SectionAgent";
import { SectionAgentSetup } from "../sections/SectionAgentSetup";
import { SectionSettings } from "../sections/SectionSettings";
import { SectionGatherRules } from "../sections/SectionGatherRules";
import { SectionAnalyzeRules } from "../sections/SectionAnalyzeRules";
import { SectionImeLogPatterns } from "../sections/SectionImeLogPatterns";

const SECTION_COMPONENTS: Record<SectionId, React.ComponentType> = {
  "private-preview": SectionPrivatePreview,
  "general":         SectionGeneral,
  "overview":        SectionOverview,
  "setup":           SectionSetup,
  "agent":           SectionAgent,
  "agent-setup":     SectionAgentSetup,
  "settings":        SectionSettings,
  "gather-rules":    SectionGatherRules,
  "analyze-rules":   SectionAnalyzeRules,
  "ime-log-patterns": SectionImeLogPatterns,
};

export function generateStaticParams() {
  return NAV_SECTIONS.map((s) => ({ section: s.id }));
}

type Props = { params: Promise<{ section: string }> };

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { section } = await params;
  const nav = NAV_SECTIONS.find((s) => s.id === section);
  if (!nav) return {};

  return {
    title: nav.label,
    description: nav.description,
    openGraph: {
      title: `${nav.label} – Autopilot Monitor Docs`,
      description: nav.description,
      url: `https://www.autopilotmonitor.com/docs/${section}`,
    },
    twitter: {
      title: `${nav.label} – Autopilot Monitor Docs`,
      description: nav.description,
    },
    alternates: {
      canonical: `https://www.autopilotmonitor.com/docs/${section}`,
    },
  };
}

export default async function DocsSectionPage({ params }: Props) {
  const { section } = await params;
  const SectionContent = SECTION_COMPONENTS[section as SectionId];
  if (!SectionContent) notFound();

  const nav = NAV_SECTIONS.find((s) => s.id === section);
  const breadcrumbJsonLd = {
    "@context": "https://schema.org",
    "@type": "BreadcrumbList",
    itemListElement: [
      { "@type": "ListItem", position: 1, name: "Home", item: "https://www.autopilotmonitor.com" },
      { "@type": "ListItem", position: 2, name: "Docs", item: "https://www.autopilotmonitor.com/docs" },
      ...(nav
        ? [{ "@type": "ListItem", position: 3, name: nav.label, item: `https://www.autopilotmonitor.com/docs/${section}` }]
        : []),
    ],
  };

  return (
    <>
      <script
        type="application/ld+json"
        dangerouslySetInnerHTML={{ __html: JSON.stringify(breadcrumbJsonLd) }}
      />
      <SectionContent />
    </>
  );
}
