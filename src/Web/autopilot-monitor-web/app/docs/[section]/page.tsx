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

  return <SectionContent />;
}
