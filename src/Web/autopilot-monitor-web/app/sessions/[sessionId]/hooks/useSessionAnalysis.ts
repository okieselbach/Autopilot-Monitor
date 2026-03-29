"use client";
import { useState, useCallback } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import { RuleResult } from "@/types";

const GUID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export function useSessionAnalysis(
  sessionId: string,
  sessionTenantId: string | null,
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>
) {
  const [analysisResults, setAnalysisResults] = useState<RuleResult[]>([]);
  const [loadingAnalysis, setLoadingAnalysis] = useState(false);
  const [vulnerabilityReport, setVulnerabilityReport] = useState<unknown>(null);

  const fetchAnalysisResults = useCallback(async (reanalyze = false) => {
    if (!sessionTenantId || !GUID_REGEX.test(sessionTenantId)) return;
    try {
      setLoadingAnalysis(true);

      const response = await authenticatedFetch(
        api.sessions.analysis(sessionId, sessionTenantId, reanalyze || undefined),
        getAccessToken
      );
      if (response.ok) {
        const data = await response.json();
        if (data.results) {
          setAnalysisResults(data.results.sort((a: RuleResult, b: RuleResult) => b.confidenceScore - a.confidenceScore));
        }
      }
    } catch (error) {
      console.error("Failed to fetch analysis results:", error);
    } finally {
      setLoadingAnalysis(false);
    }
  }, [sessionId, sessionTenantId, getAccessToken]);

  const fetchVulnerabilityReport = useCallback(async (rescan = false) => {
    if (!sessionTenantId || !GUID_REGEX.test(sessionTenantId)) return;
    try {
      const response = await authenticatedFetch(
        api.sessions.vulnerabilityReport(sessionId, sessionTenantId, rescan || undefined),
        getAccessToken
      );
      if (response.ok) {
        const data = await response.json();
        setVulnerabilityReport(data.report ?? null);
      }
    } catch (error) {
      console.error("Failed to fetch vulnerability report:", error);
    }
  }, [sessionId, sessionTenantId, getAccessToken]);

  return {
    analysisResults,
    loadingAnalysis,
    vulnerabilityReport,
    setVulnerabilityReport,
    fetchAnalysisResults,
    fetchVulnerabilityReport,
  };
}
