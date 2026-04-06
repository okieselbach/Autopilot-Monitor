"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { trackEvent } from "@/lib/appInsights";

// --- Interfaces ---

interface UnmatchedSoftwareEntry {
  softwareName: string;
  publisher: string;
  frequency: number;
  lastSeenAt: string;
  exampleSessionId: string;
  normalizedVendor?: string;
  normalizedVersion?: string;
}

interface AutoResolveResult {
  resolved: Array<{ softwareName: string; cpeUri: string; confidence: number }>;
  failed: Array<{ softwareName: string; reason: string }>;
  totalProcessed: number;
  totalResolved: number;
  totalFailed: number;
}

interface CpeMappingEntry {
  normalizedVendor: string;
  normalizedProduct: string;
  cpeVendor: string;
  cpeProduct: string;
  cpeUri: string;
  category: string;
  displayNamePatterns: string[];
  publisherPatterns: string[];
  excludePatterns: string[];
  source: string;
  createdAt: string;
}

interface SoftwareMappingSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
}

// --- Component ---

export function SoftwareMappingSection({
  getAccessToken,
  setError,
}: SoftwareMappingSectionProps) {
  const [activeTab, setActiveTab] = useState<"unmapped" | "mapped" | "ignored">("unmapped");

  // Unmapped state
  const [loading, setLoading] = useState(false);
  const [entries, setEntries] = useState<UnmatchedSoftwareEntry[]>([]);
  const [total, setTotal] = useState(0);
  const [currentPage, setCurrentPage] = useState(0);
  const pageSize = 20;

  // CPE mapping state (for unmapped tab)
  const [expandedMappingRow, setExpandedMappingRow] = useState<string | null>(null);
  const [cpeInputs, setCpeInputs] = useState<Record<string, string>>({});
  const [savingMapping, setSavingMapping] = useState<string | null>(null);
  const [savedMappings, setSavedMappings] = useState<Set<string>>(new Set());

  // Bulk auto-resolve state
  const [selectedRows, setSelectedRows] = useState<Set<string>>(new Set());
  const [autoResolving, setAutoResolving] = useState(false);
  const [autoResolveProgress, setAutoResolveProgress] = useState<{
    batchIndex: number;
    totalBatches: number;
    resolvedSoFar: number;
    failedSoFar: number;
  } | null>(null);
  const [autoResolveResults, setAutoResolveResults] = useState<AutoResolveResult | null>(null);

  // Mapped state
  const [mappedEntries, setMappedEntries] = useState<CpeMappingEntry[]>([]);
  const [mappedLoading, setMappedLoading] = useState(false);
  const [mappedLoaded, setMappedLoaded] = useState(false);
  const [mappedPage, setMappedPage] = useState(0);
  const [searchQuery, setSearchQuery] = useState("");

  // Mapped edit state
  const [editingRow, setEditingRow] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<{
    cpeUri: string;
    cpeVendor: string;
    cpeProduct: string;
    category: string;
    displayNamePatterns: string;
    publisherPatterns: string;
    excludePatterns: string;
  }>({ cpeUri: "", cpeVendor: "", cpeProduct: "", category: "", displayNamePatterns: "", publisherPatterns: "", excludePatterns: "" });
  const [savingEdit, setSavingEdit] = useState(false);
  const [deletingRow, setDeletingRow] = useState<string | null>(null);

  // Ignored state
  const [ignoredEntries, setIgnoredEntries] = useState<Array<{ softwareName: string; publisher: string; reason: string; ignoredAt: string }>>([]);
  const [ignoredLoading, setIgnoredLoading] = useState(false);
  const [ignoredLoaded, setIgnoredLoaded] = useState(false);
  const [ignoredPage, setIgnoredPage] = useState(0);
  const [ignoringRow, setIgnoringRow] = useState<string | null>(null);
  const [restoringRow, setRestoringRow] = useState<string | null>(null);

  // --- Unmapped fetching ---

  const fetchUnmatchedSoftware = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);

      const response = await authenticatedFetch(
        api.vulnerability.unmatchedSoftware(),
        getAccessToken
      );

      if (!response.ok) {
        throw new Error(`Failed to load unmatched software: ${response.statusText}`);
      }

      const data = await response.json();
      const sorted = (data.software || []).sort(
        (a: UnmatchedSoftwareEntry, b: UnmatchedSoftwareEntry) => b.frequency - a.frequency
      );
      setEntries(sorted);
      setTotal(data.total ?? sorted.length);
      setCurrentPage(0);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while fetching unmatched software");
      } else {
        console.error("Error fetching unmatched software:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to load unmatched software");
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, setError]);

  // --- Mapped fetching ---

  const fetchCpeMappings = useCallback(async () => {
    try {
      setMappedLoading(true);
      setError(null);

      const response = await authenticatedFetch(
        api.vulnerability.cpeMappings(),
        getAccessToken
      );

      if (!response.ok) {
        throw new Error(`Failed to load CPE mappings: ${response.statusText}`);
      }

      const data = await response.json();
      setMappedEntries(data.mappings || []);
      setMappedLoaded(true);
      setMappedPage(0);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while fetching CPE mappings");
      } else {
        console.error("Error fetching CPE mappings:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to load CPE mappings");
    } finally {
      setMappedLoading(false);
    }
  }, [getAccessToken, setError]);

  // Load on mount / tab switch
  useEffect(() => {
    if (activeTab === "unmapped" && entries.length === 0 && !loading) {
      fetchUnmatchedSoftware();
    }
  }, [activeTab, entries.length, loading, fetchUnmatchedSoftware]);

  useEffect(() => {
    if (activeTab === "mapped" && !mappedLoaded && !mappedLoading) {
      fetchCpeMappings();
    }
  }, [activeTab, mappedLoaded, mappedLoading, fetchCpeMappings]);

  // --- Ignored fetching ---

  const fetchIgnoredSoftware = useCallback(async () => {
    try {
      setIgnoredLoading(true);
      setError(null);
      const response = await authenticatedFetch(api.vulnerability.ignoredSoftware(), getAccessToken);
      if (!response.ok) throw new Error(`Failed to load ignored software: ${response.statusText}`);
      const data = await response.json();
      setIgnoredEntries(data.items || []);
      setIgnoredLoaded(true);
      setIgnoredPage(0);
    } catch (err) {
      console.error("Error fetching ignored software:", err);
      setError(err instanceof Error ? err.message : "Failed to load ignored software");
    } finally {
      setIgnoredLoading(false);
    }
  }, [getAccessToken, setError]);

  useEffect(() => {
    if (activeTab === "ignored" && !ignoredLoaded && !ignoredLoading) {
      fetchIgnoredSoftware();
    }
  }, [activeTab, ignoredLoaded, ignoredLoading, fetchIgnoredSoftware]);

  // --- Ignore / Restore handlers ---

  const handleIgnoreSoftware = async (entry: UnmatchedSoftwareEntry) => {
    const key = getRowKey(entry);
    try {
      setIgnoringRow(key);
      setError(null);
      const response = await authenticatedFetch(api.vulnerability.ignoredSoftware(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          items: [{ softwareName: entry.softwareName, publisher: entry.publisher || "", reason: "manual" }],
        }),
      });
      if (!response.ok) throw new Error(`Failed to ignore software: ${response.statusText}`);
      // Remove from unmapped list
      setEntries((prev) => prev.filter((e) => getRowKey(e) !== key));
      setTotal((prev) => Math.max(0, prev - 1));
      setSelectedRows((prev) => { const n = new Set(prev); n.delete(key); return n; });
      setIgnoredLoaded(false); // Invalidate ignored cache
    } catch (err) {
      console.error("Error ignoring software:", err);
      setError(err instanceof Error ? err.message : "Failed to ignore software");
    } finally {
      setIgnoringRow(null);
    }
  };

  const handleBulkIgnoreSelected = async () => {
    const selectedEntries = entries.filter((e) => selectedRows.has(getRowKey(e)));
    if (selectedEntries.length === 0) return;

    try {
      setIgnoringRow("bulk");
      setError(null);
      const response = await authenticatedFetch(api.vulnerability.ignoredSoftware(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          items: selectedEntries.map((e) => ({ softwareName: e.softwareName, publisher: e.publisher || "", reason: "manual" })),
        }),
      });
      if (!response.ok) throw new Error(`Failed to ignore software: ${response.statusText}`);
      const keys = new Set(selectedEntries.map(getRowKey));
      setEntries((prev) => prev.filter((e) => !keys.has(getRowKey(e))));
      setTotal((prev) => Math.max(0, prev - selectedEntries.length));
      setSelectedRows(new Set());
      setIgnoredLoaded(false);
    } catch (err) {
      console.error("Error bulk ignoring software:", err);
      setError(err instanceof Error ? err.message : "Failed to ignore software");
    } finally {
      setIgnoringRow(null);
    }
  };

  const handleRestoreSoftware = async (entry: { softwareName: string; publisher: string }) => {
    const key = `${entry.softwareName}::${entry.publisher}`;
    try {
      setRestoringRow(key);
      setError(null);
      const response = await authenticatedFetch(api.vulnerability.ignoredSoftware(), getAccessToken, {
        method: "DELETE",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ softwareName: entry.softwareName, publisher: entry.publisher || "" }),
      });
      if (!response.ok) throw new Error(`Failed to restore software: ${response.statusText}`);
      setIgnoredEntries((prev) => prev.filter((e) => `${e.softwareName}::${e.publisher}` !== key));
      // Invalidate unmapped cache so it re-fetches with the restored item
      setEntries([]);
      setTotal(0);
    } catch (err) {
      console.error("Error restoring software:", err);
      setError(err instanceof Error ? err.message : "Failed to restore software");
    } finally {
      setRestoringRow(null);
    }
  };

  const handleCheckNvdFromIgnored = async (entry: { softwareName: string; publisher: string }) => {
    const key = `${entry.softwareName}::${entry.publisher}`;
    try {
      setRestoringRow(key);
      setError(null);
      // First, call auto-resolve for this single item
      const resolveResponse = await authenticatedFetch(api.vulnerability.cpeAutoResolve(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          items: [{ softwareName: entry.softwareName, publisher: entry.publisher || "", normalizedVendor: entry.publisher || "" }],
        }),
      });
      if (!resolveResponse.ok) throw new Error(`NVD check failed: ${resolveResponse.statusText}`);
      const data: AutoResolveResult = await resolveResponse.json();

      if (data.totalResolved > 0) {
        // Resolved! Remove from ignore list and refresh
        await authenticatedFetch(api.vulnerability.ignoredSoftware(), getAccessToken, {
          method: "DELETE",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ softwareName: entry.softwareName, publisher: entry.publisher || "" }),
        });
        setIgnoredEntries((prev) => prev.filter((e) => `${e.softwareName}::${e.publisher}` !== key));
        setMappedLoaded(false);
        setError(null);
        trackEvent("ignored_nvd_check_resolved", { softwareName: entry.softwareName, cpeUri: data.resolved[0]?.cpeUri });
      } else {
        setError(`No CPE match found for "${entry.softwareName}" (${data.failed[0]?.reason || "no match"})`);
      }
    } catch (err) {
      console.error("Error checking NVD:", err);
      setError(err instanceof Error ? err.message : "NVD check failed");
    } finally {
      setRestoringRow(null);
    }
  };

  // --- Ignored helpers ---

  const ignoredTotalPages = Math.ceil(ignoredEntries.length / pageSize);
  const paginatedIgnoredEntries = ignoredEntries.slice(ignoredPage * pageSize, (ignoredPage + 1) * pageSize);

  // --- Unmapped helpers ---

  const totalPages = Math.ceil(entries.length / pageSize);
  const paginatedEntries = entries.slice(
    currentPage * pageSize,
    (currentPage + 1) * pageSize
  );

  const formatDate = (dateStr: string): string => {
    try {
      return new Date(dateStr).toLocaleDateString(undefined, {
        year: "numeric",
        month: "short",
        day: "numeric",
      });
    } catch {
      return dateStr;
    }
  };

  const getFrequencyBadgeClasses = (frequency: number): string => {
    if (frequency >= 100) return "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300";
    if (frequency >= 50) return "bg-orange-100 text-orange-800 dark:bg-orange-900/40 dark:text-orange-300";
    if (frequency >= 20) return "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300";
    if (frequency >= 5) return "bg-yellow-100 text-yellow-800 dark:bg-yellow-900/40 dark:text-yellow-300";
    return "bg-gray-100 text-gray-700 dark:bg-gray-700 dark:text-gray-300";
  };

  const getRowKey = (entry: UnmatchedSoftwareEntry): string =>
    `${entry.softwareName}::${entry.publisher}`;

  const toggleMappingRow = (entry: UnmatchedSoftwareEntry) => {
    const key = getRowKey(entry);
    if (expandedMappingRow === key) {
      setExpandedMappingRow(null);
    } else {
      setExpandedMappingRow(key);
      if (!cpeInputs[key]) {
        setCpeInputs((prev) => ({ ...prev, [key]: "" }));
      }
    }
  };

  const handleSaveMapping = async (entry: UnmatchedSoftwareEntry) => {
    const key = getRowKey(entry);
    const cpeUri = (cpeInputs[key] || "").trim();
    if (!cpeUri) return;

    try {
      setSavingMapping(key);
      setError(null);

      const response = await authenticatedFetch(
        api.vulnerability.cpeMapping(),
        getAccessToken,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            normalizedProduct: entry.softwareName,
            normalizedVendor: entry.publisher || "",
            cpeUri: cpeUri,
            displayNamePatterns: [entry.softwareName],
            publisherPatterns: entry.publisher ? [entry.publisher] : [],
          }),
        }
      );

      if (!response.ok) {
        const data = await response.json().catch(() => null);
        throw new Error(data?.message || `Failed to save mapping: ${response.statusText}`);
      }

      setSavedMappings((prev) => new Set(prev).add(key));
      setExpandedMappingRow(null);
      // Invalidate mapped cache so it reloads on tab switch
      setMappedLoaded(false);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving CPE mapping");
      } else {
        console.error("Error saving CPE mapping:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to save CPE mapping");
    } finally {
      setSavingMapping(null);
    }
  };

  // --- Bulk auto-resolve ---

  const toggleRowSelection = (key: string) => {
    setSelectedRows((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  const selectAllOnPage = () => {
    const pageKeys = paginatedEntries
      .filter((e) => !savedMappings.has(getRowKey(e)))
      .map((e) => getRowKey(e));
    setSelectedRows((prev) => {
      const next = new Set(prev);
      pageKeys.forEach((k) => next.add(k));
      return next;
    });
  };

  const deselectAll = () => setSelectedRows(new Set());

  const allOnPageSelected = paginatedEntries.length > 0 &&
    paginatedEntries
      .filter((e) => !savedMappings.has(getRowKey(e)))
      .every((e) => selectedRows.has(getRowKey(e)));

  const handleAutoResolveSelected = async () => {
    const selectedEntries = entries.filter((e) => selectedRows.has(getRowKey(e)));
    if (selectedEntries.length === 0) return;

    setAutoResolving(true);
    setAutoResolveResults(null);
    setAutoResolveProgress(null);

    const BATCH_SIZE = 25;
    const batches: UnmatchedSoftwareEntry[][] = [];
    for (let i = 0; i < selectedEntries.length; i += BATCH_SIZE) {
      batches.push(selectedEntries.slice(i, i + BATCH_SIZE));
    }

    const cumulativeResolved: AutoResolveResult["resolved"] = [];
    const cumulativeFailed: AutoResolveResult["failed"] = [];

    try {
      for (let i = 0; i < batches.length; i++) {
        setAutoResolveProgress({
          batchIndex: i + 1,
          totalBatches: batches.length,
          resolvedSoFar: cumulativeResolved.length,
          failedSoFar: cumulativeFailed.length,
        });

        const response = await authenticatedFetch(
          api.vulnerability.cpeAutoResolve(),
          getAccessToken,
          {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              items: batches[i].map((e) => ({
                softwareName: e.softwareName,
                publisher: e.publisher,
                normalizedVendor: e.normalizedVendor || e.publisher || "",
              })),
            }),
          }
        );

        if (!response.ok) {
          throw new Error(`Batch ${i + 1} failed: ${response.statusText}`);
        }

        const data: AutoResolveResult = await response.json();
        cumulativeResolved.push(...data.resolved);
        cumulativeFailed.push(...data.failed);

        // Mark resolved items
        data.resolved.forEach((r) => {
          const entry = selectedEntries.find((e) => e.softwareName === r.softwareName);
          if (entry) setSavedMappings((prev) => new Set(prev).add(getRowKey(entry)));
        });
      }

      setAutoResolveResults({
        resolved: cumulativeResolved,
        failed: cumulativeFailed,
        totalProcessed: cumulativeResolved.length + cumulativeFailed.length,
        totalResolved: cumulativeResolved.length,
        totalFailed: cumulativeFailed.length,
      });
      setSelectedRows(new Set());
      setMappedLoaded(false);

      trackEvent("bulk_auto_resolve_completed", {
        totalSelected: selectedEntries.length.toString(),
        totalResolved: cumulativeResolved.length.toString(),
        totalFailed: cumulativeFailed.length.toString(),
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Auto-resolve failed");
    } finally {
      setAutoResolving(false);
      setAutoResolveProgress(null);
    }
  };

  // --- Mapped helpers ---

  const getMappedRowKey = (entry: CpeMappingEntry): string =>
    `${entry.source}::${entry.normalizedVendor}::${entry.normalizedProduct}`;

  const filteredMappedEntries = mappedEntries.filter((m) => {
    if (!searchQuery.trim()) return true;
    const q = searchQuery.toLowerCase();
    return (
      m.normalizedProduct.toLowerCase().includes(q) ||
      m.normalizedVendor.toLowerCase().includes(q) ||
      m.cpeUri.toLowerCase().includes(q) ||
      m.cpeVendor.toLowerCase().includes(q) ||
      m.cpeProduct.toLowerCase().includes(q) ||
      m.category.toLowerCase().includes(q) ||
      m.displayNamePatterns.some((p) => p.toLowerCase().includes(q)) ||
      m.publisherPatterns.some((p) => p.toLowerCase().includes(q)) ||
      (m.excludePatterns || []).some((p) => p.toLowerCase().includes(q))
    );
  });

  const mappedTotalPages = Math.ceil(filteredMappedEntries.length / pageSize);
  const paginatedMappedEntries = filteredMappedEntries.slice(
    mappedPage * pageSize,
    (mappedPage + 1) * pageSize
  );

  // Reset page when search changes
  useEffect(() => {
    setMappedPage(0);
  }, [searchQuery]);

  const getSourceBadge = (source: string) => {
    switch (source) {
      case "custom":
        return "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300";
      case "community":
        return "bg-blue-100 text-blue-800 dark:bg-blue-900/40 dark:text-blue-300";
      default:
        return "bg-gray-100 text-gray-700 dark:bg-gray-700 dark:text-gray-300";
    }
  };

  const startEditing = (entry: CpeMappingEntry) => {
    const key = getMappedRowKey(entry);
    setEditingRow(key);
    setEditForm({
      cpeUri: entry.cpeUri,
      cpeVendor: entry.cpeVendor,
      cpeProduct: entry.cpeProduct,
      category: entry.category,
      displayNamePatterns: entry.displayNamePatterns.join(", "),
      publisherPatterns: entry.publisherPatterns.join(", "),
      excludePatterns: (entry.excludePatterns || []).join(", "),
    });
  };

  const handleSaveEdit = async (entry: CpeMappingEntry) => {
    const cpeUri = editForm.cpeUri.trim();
    if (!cpeUri) return;

    try {
      setSavingEdit(true);
      setError(null);

      const displayNamePatterns = editForm.displayNamePatterns
        .split(",")
        .map((s) => s.trim())
        .filter(Boolean);
      const publisherPatterns = editForm.publisherPatterns
        .split(",")
        .map((s) => s.trim())
        .filter(Boolean);
      const excludePatterns = editForm.excludePatterns
        .split(",")
        .map((s) => s.trim())
        .filter(Boolean);

      const response = await authenticatedFetch(
        api.vulnerability.cpeMapping(),
        getAccessToken,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            normalizedProduct: entry.normalizedProduct,
            normalizedVendor: entry.normalizedVendor,
            cpeUri: cpeUri,
            cpeVendor: editForm.cpeVendor.trim(),
            cpeProduct: editForm.cpeProduct.trim(),
            category: editForm.category.trim() || "custom",
            displayNamePatterns: displayNamePatterns.length > 0 ? displayNamePatterns : entry.displayNamePatterns,
            publisherPatterns: publisherPatterns.length > 0 ? publisherPatterns : entry.publisherPatterns,
            excludePatterns: excludePatterns,
          }),
        }
      );

      if (!response.ok) {
        const data = await response.json().catch(() => null);
        throw new Error(data?.message || `Failed to save mapping: ${response.statusText}`);
      }

      setEditingRow(null);
      // Reload to reflect changes
      await fetchCpeMappings();
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving CPE mapping edit");
      } else {
        console.error("Error saving CPE mapping edit:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to save CPE mapping");
    } finally {
      setSavingEdit(false);
    }
  };

  const handleDeleteMapping = async (entry: CpeMappingEntry) => {
    if (!confirm(`Delete mapping for "${entry.normalizedProduct}"? This cannot be undone.`)) return;

    const key = getMappedRowKey(entry);
    try {
      setDeletingRow(key);
      setError(null);

      const response = await authenticatedFetch(
        api.vulnerability.cpeMapping(),
        getAccessToken,
        {
          method: "DELETE",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            normalizedProduct: entry.normalizedProduct,
            normalizedVendor: entry.normalizedVendor,
          }),
        }
      );

      if (!response.ok) {
        const data = await response.json().catch(() => null);
        throw new Error(data?.message || `Failed to delete mapping: ${response.statusText}`);
      }

      setMappedEntries((prev) => prev.filter((m) => getMappedRowKey(m) !== key));
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while deleting CPE mapping");
      } else {
        console.error("Error deleting CPE mapping:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to delete CPE mapping");
    } finally {
      setDeletingRow(null);
    }
  };

  // --- JSON Export ---

  const handleExportJson = () => {
    const dataToExport = filteredMappedEntries;
    const exportData = {
      version: "1.0.0",
      lastUpdated: new Date().toISOString().split("T")[0],
      mappings: dataToExport.map((m) => ({
        normalizedVendor: m.normalizedVendor,
        normalizedProduct: m.normalizedProduct,
        displayNamePatterns: m.displayNamePatterns,
        publisherPatterns: m.publisherPatterns,
        excludePatterns: m.excludePatterns || [],
        cpeVendor: m.cpeVendor,
        cpeProduct: m.cpeProduct,
        cpeUri: m.cpeUri,
        category: m.category,
      })),
    };
    const blob = new Blob([JSON.stringify(exportData, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `cpe-mapping-export-${exportData.lastUpdated}.json`;
    a.click();
    URL.revokeObjectURL(url);
    trackEvent("software_mapping_exported");
  };

  // --- Render ---

  return (
    <div className="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden border border-amber-200 dark:border-amber-800">
      {/* Header */}
      <div className="px-6 py-4 flex items-center gap-3">
        <div className="w-8 h-8 bg-gradient-to-br from-amber-100 to-orange-100 dark:from-amber-900 dark:to-orange-900 rounded-lg flex items-center justify-center">
          <svg className="w-4 h-4 text-amber-600 dark:text-amber-400" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M9 12.75L11.25 15 15 9.75m-3-7.036A11.959 11.959 0 013.598 6 11.99 11.99 0 003 9.749c0 5.592 3.824 10.29 9 11.623 5.176-1.332 9-6.03 9-11.622 0-1.31-.21-2.571-.598-3.751h-.152c-3.196 0-6.1-1.248-8.25-3.285z" />
          </svg>
        </div>
        <div>
          <h2 className="text-lg font-semibold text-gray-900 dark:text-white">
            Software Mapping (Vulnerability Analyzer)
          </h2>
          <p className="text-sm text-gray-500 dark:text-gray-400">
            Manage CPE mappings for software vulnerability correlation. View unmapped software or browse and edit existing mappings.
          </p>
        </div>
      </div>

      {/* Content */}
      <div className="px-6 pb-6 border-t border-gray-200 dark:border-gray-700">
          {/* Tab Toggle */}
          <div className="flex items-center gap-1 mt-4 mb-2 bg-gray-100 dark:bg-gray-700/50 rounded-lg p-1 w-fit">
            <button
              onClick={() => setActiveTab("unmapped")}
              className={`px-4 py-1.5 text-sm font-medium rounded-md transition-colors ${
                activeTab === "unmapped"
                  ? "bg-white dark:bg-gray-600 text-amber-700 dark:text-amber-300 shadow-sm"
                  : "text-gray-600 dark:text-gray-400 hover:text-gray-800 dark:hover:text-gray-200"
              }`}
            >
              Unmapped
              {total > 0 && activeTab !== "unmapped" && (
                <span className="ml-1.5 text-xs bg-amber-100 text-amber-700 dark:bg-amber-900/50 dark:text-amber-400 px-1.5 py-0.5 rounded-full">
                  {total}
                </span>
              )}
            </button>
            <button
              onClick={() => setActiveTab("mapped")}
              className={`px-4 py-1.5 text-sm font-medium rounded-md transition-colors ${
                activeTab === "mapped"
                  ? "bg-white dark:bg-gray-600 text-amber-700 dark:text-amber-300 shadow-sm"
                  : "text-gray-600 dark:text-gray-400 hover:text-gray-800 dark:hover:text-gray-200"
              }`}
            >
              Mapped
              {mappedEntries.length > 0 && activeTab !== "mapped" && (
                <span className="ml-1.5 text-xs bg-gray-200 text-gray-600 dark:bg-gray-600 dark:text-gray-300 px-1.5 py-0.5 rounded-full">
                  {mappedEntries.length}
                </span>
              )}
            </button>
            <button
              onClick={() => setActiveTab("ignored")}
              className={`px-4 py-1.5 text-sm font-medium rounded-md transition-colors ${
                activeTab === "ignored"
                  ? "bg-white dark:bg-gray-600 text-amber-700 dark:text-amber-300 shadow-sm"
                  : "text-gray-600 dark:text-gray-400 hover:text-gray-800 dark:hover:text-gray-200"
              }`}
            >
              Ignored
              {ignoredEntries.length > 0 && activeTab !== "ignored" && (
                <span className="ml-1.5 text-xs bg-gray-200 text-gray-600 dark:bg-gray-600 dark:text-gray-300 px-1.5 py-0.5 rounded-full">
                  {ignoredEntries.length}
                </span>
              )}
            </button>
          </div>

          {/* ==================== UNMAPPED TAB ==================== */}
          {activeTab === "unmapped" && (
            <>
              {loading ? (
                <div className="flex items-center justify-center py-8">
                  <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" />
                  <span className="ml-3 text-sm text-gray-500 dark:text-gray-400">Loading unmatched software...</span>
                </div>
              ) : entries.length === 0 ? (
                <div className="text-center py-8 text-gray-500 dark:text-gray-400">
                  <svg className="w-12 h-12 mx-auto mb-3 text-gray-300 dark:text-gray-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M9 12.75L11.25 15 15 9.75m-3-7.036A11.959 11.959 0 013.598 6 11.99 11.99 0 003 9.749c0 5.592 3.824 10.29 9 11.623 5.176-1.332 9-6.03 9-11.622 0-1.31-.21-2.571-.598-3.751h-.152c-3.196 0-6.1-1.248-8.25-3.285z" />
                  </svg>
                  <p className="text-sm">No unmatched software found. All detected software has been mapped to CPE identifiers.</p>
                </div>
              ) : (
                <>
                  {/* Stats bar */}
                  <div className="flex flex-wrap items-center gap-4 py-4 text-sm">
                    <span className="text-gray-600 dark:text-gray-300">
                      <span className="font-semibold text-amber-600 dark:text-amber-400">{total}</span> unmatched software entries
                    </span>
                    <button
                      onClick={fetchUnmatchedSoftware}
                      disabled={autoResolving}
                      className="ml-auto text-sm text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 flex items-center gap-1.5 disabled:opacity-40"
                    >
                      <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M16.023 9.348h4.992v-.001M2.985 19.644v-4.992m0 0h4.992m-4.993 0l3.181 3.183a8.25 8.25 0 0013.803-3.7M4.031 9.865a8.25 8.25 0 0113.803-3.7l3.181 3.182M20.996 19.632h-4.991" />
                      </svg>
                      Refresh
                    </button>
                  </div>

                  {/* Selection toolbar */}
                  {(selectedRows.size > 0 || autoResolving) && (
                    <div className="flex flex-wrap items-center gap-3 pb-3 text-sm">
                      <span className="text-gray-600 dark:text-gray-300 font-medium">
                        {selectedRows.size} selected
                      </span>
                      <button
                        onClick={allOnPageSelected ? deselectAll : selectAllOnPage}
                        disabled={autoResolving}
                        className="text-xs px-2.5 py-1 rounded-md border border-gray-300 dark:border-gray-600 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-700 disabled:opacity-40 transition-colors"
                      >
                        {allOnPageSelected ? "Deselect All" : "Select All"}
                      </button>
                      {selectedRows.size > 0 && (
                        <button
                          onClick={deselectAll}
                          disabled={autoResolving}
                          className="text-xs px-2.5 py-1 rounded-md border border-gray-300 dark:border-gray-600 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-700 disabled:opacity-40 transition-colors"
                        >
                          Clear
                        </button>
                      )}
                      <button
                        onClick={handleBulkIgnoreSelected}
                        disabled={autoResolving || ignoringRow === "bulk" || selectedRows.size === 0}
                        className="text-xs px-2.5 py-1 rounded-md border border-gray-300 dark:border-gray-600 text-gray-600 dark:text-gray-300 hover:bg-red-50 hover:text-red-600 hover:border-red-300 dark:hover:bg-red-900/20 dark:hover:text-red-400 disabled:opacity-40 transition-colors"
                      >
                        {ignoringRow === "bulk" ? "Ignoring..." : "Ignore Selected"}
                      </button>
                      <button
                        onClick={handleAutoResolveSelected}
                        disabled={autoResolving || selectedRows.size === 0}
                        className="ml-auto text-xs px-3 py-1.5 rounded-md font-medium bg-amber-600 text-white hover:bg-amber-700 disabled:opacity-40 disabled:cursor-not-allowed transition-colors flex items-center gap-1.5"
                      >
                        {autoResolving ? (
                          <>
                            <div className="animate-spin rounded-full h-3 w-3 border-b-2 border-white" />
                            Auto-Mapping...
                          </>
                        ) : (
                          <>
                            <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M9.813 15.904L9 18.75l-.813-2.846a4.5 4.5 0 00-3.09-3.09L2.25 12l2.846-.813a4.5 4.5 0 003.09-3.09L9 5.25l.813 2.846a4.5 4.5 0 003.09 3.09L15.75 12l-2.846.813a4.5 4.5 0 00-3.09 3.09z" />
                            </svg>
                            Auto-Map Selected
                          </>
                        )}
                      </button>
                    </div>
                  )}

                  {/* Auto-resolve progress */}
                  {autoResolving && autoResolveProgress && (
                    <div className="mb-3 px-4 py-3 bg-blue-50 dark:bg-blue-900/20 border border-blue-200 dark:border-blue-800 rounded-md text-sm">
                      <div className="flex items-center gap-2 text-blue-700 dark:text-blue-300">
                        <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-blue-600" />
                        <span>
                          Auto-resolving... Batch {autoResolveProgress.batchIndex}/{autoResolveProgress.totalBatches}
                          {autoResolveProgress.resolvedSoFar > 0 && <> &middot; Resolved: {autoResolveProgress.resolvedSoFar}</>}
                          {autoResolveProgress.failedSoFar > 0 && <> &middot; Failed: {autoResolveProgress.failedSoFar}</>}
                        </span>
                      </div>
                    </div>
                  )}

                  {/* Auto-resolve results */}
                  {autoResolveResults && !autoResolving && (
                    <div className={`mb-3 px-4 py-3 rounded-md text-sm border ${
                      autoResolveResults.totalFailed === 0
                        ? "bg-green-50 dark:bg-green-900/20 border-green-200 dark:border-green-800"
                        : "bg-amber-50 dark:bg-amber-900/20 border-amber-200 dark:border-amber-800"
                    }`}>
                      <div className="flex items-center justify-between">
                        <span className={autoResolveResults.totalFailed === 0 ? "text-green-700 dark:text-green-300" : "text-amber-700 dark:text-amber-300"}>
                          Auto-resolve complete: <strong>{autoResolveResults.totalResolved}</strong> resolved,{" "}
                          <strong>{autoResolveResults.totalFailed}</strong> failed
                        </span>
                        <button
                          onClick={() => setAutoResolveResults(null)}
                          className="text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
                        >
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                          </svg>
                        </button>
                      </div>
                      {autoResolveResults.failed.length > 0 && (
                        <div className="mt-2 text-xs text-gray-600 dark:text-gray-400">
                          <span className="font-medium">Failed items:</span>{" "}
                          {autoResolveResults.failed.map((f) => `${f.softwareName} (${f.reason})`).join(", ")}
                        </div>
                      )}
                    </div>
                  )}

                  {/* Table */}
                  <div className="overflow-x-auto">
                    <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
                      <thead className="bg-gray-50 dark:bg-gray-700/50">
                        <tr>
                          <th className="w-10 px-2 py-3">
                            <input
                              type="checkbox"
                              checked={allOnPageSelected && paginatedEntries.length > 0}
                              onChange={allOnPageSelected ? deselectAll : selectAllOnPage}
                              disabled={autoResolving}
                              className="h-4 w-4 rounded border-gray-300 text-amber-600 focus:ring-amber-500 disabled:opacity-40"
                            />
                          </th>
                          <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Software Name</th>
                          <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Publisher</th>
                          <th className="w-28 px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Frequency</th>
                          <th className="w-28 px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Last Seen</th>
                          <th className="w-24 px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Session</th>
                          <th className="w-40 px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Actions</th>
                        </tr>
                      </thead>
                      <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                        {paginatedEntries.map((entry) => {
                          const key = getRowKey(entry);
                          const isMapped = savedMappings.has(key);
                          const isMappingExpanded = expandedMappingRow === key;
                          const isSaving = savingMapping === key;

                          return (
                            <tr key={key} className="group">
                              <td colSpan={7} className="p-0">
                                <div
                                  className={`hover:bg-amber-50 dark:hover:bg-amber-900/10 transition-colors ${isMappingExpanded ? "bg-amber-50/50 dark:bg-amber-900/5" : ""}`}
                                >
                                  <div className="flex">
                                    <div className="w-10 px-2 py-3 flex items-center justify-center flex-shrink-0">
                                      {!isMapped && (
                                        <input
                                          type="checkbox"
                                          checked={selectedRows.has(key)}
                                          onChange={() => toggleRowSelection(key)}
                                          disabled={autoResolving}
                                          className="h-4 w-4 rounded border-gray-300 text-amber-600 focus:ring-amber-500 disabled:opacity-40"
                                        />
                                      )}
                                    </div>
                                    <div className="px-4 py-3 text-sm text-gray-900 dark:text-gray-100 flex-1 min-w-0 truncate">
                                      {entry.softwareName}
                                    </div>
                                    <div className="px-4 py-3 text-sm text-gray-600 dark:text-gray-400 flex-1 min-w-0 truncate">
                                      {entry.publisher || <span className="text-gray-300 dark:text-gray-600 italic">unknown</span>}
                                    </div>
                                    <div className="px-4 py-3 text-sm flex-shrink-0 w-28">
                                      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold ${getFrequencyBadgeClasses(entry.frequency)}`}>
                                        {entry.frequency} {entry.frequency === 1 ? "session" : "sessions"}
                                      </span>
                                    </div>
                                    <div className="px-4 py-3 text-sm text-gray-600 dark:text-gray-400 whitespace-nowrap flex-shrink-0 w-28">
                                      {formatDate(entry.lastSeenAt)}
                                    </div>
                                    <div className="px-4 py-3 text-sm flex-shrink-0 w-24">
                                      <a
                                        href={`/sessions/${entry.exampleSessionId}`}
                                        className="text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 font-mono text-xs hover:underline"
                                        onClick={(e) => e.stopPropagation()}
                                      >
                                        {entry.exampleSessionId.length > 8
                                          ? `${entry.exampleSessionId.slice(0, 8)}...`
                                          : entry.exampleSessionId}
                                      </a>
                                    </div>
                                    <div className="px-4 py-3 text-sm flex-shrink-0 w-40 flex items-center gap-2">
                                      {isMapped ? (
                                        <span className="inline-flex items-center gap-1 text-xs text-green-600 dark:text-green-400 font-medium">
                                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={2}>
                                            <path strokeLinecap="round" strokeLinejoin="round" d="M4.5 12.75l6 6 9-13.5" />
                                          </svg>
                                          Mapped
                                        </span>
                                      ) : (
                                        <>
                                          <button
                                            onClick={() => toggleMappingRow(entry)}
                                            className={`text-xs px-2.5 py-1 rounded-md font-medium transition-colors ${
                                              isMappingExpanded
                                                ? "bg-amber-200 text-amber-800 dark:bg-amber-800 dark:text-amber-200"
                                                : "bg-amber-100 text-amber-700 hover:bg-amber-200 dark:bg-amber-900/50 dark:text-amber-400 dark:hover:bg-amber-800/50"
                                            }`}
                                          >
                                            Map
                                          </button>
                                          <a
                                            href={`https://nvd.nist.gov/products/cpe/search/results?keyword=${encodeURIComponent(entry.softwareName)}`}
                                            target="_blank"
                                            rel="noopener noreferrer"
                                            className="text-xs text-gray-500 hover:text-amber-600 dark:text-gray-400 dark:hover:text-amber-400 transition-colors"
                                            title="Search NVD for CPE"
                                            onClick={(e) => e.stopPropagation()}
                                          >
                                            <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                                              <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
                                            </svg>
                                          </a>
                                          <button
                                            onClick={() => handleIgnoreSoftware(entry)}
                                            disabled={ignoringRow === key}
                                            className="text-xs text-gray-400 hover:text-red-500 dark:text-gray-500 dark:hover:text-red-400 transition-colors"
                                            title="Ignore — never auto-resolve"
                                          >
                                            {ignoringRow === key ? (
                                              <div className="animate-spin rounded-full h-3.5 w-3.5 border-b-2 border-gray-400" />
                                            ) : (
                                              <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                                                <path strokeLinecap="round" strokeLinejoin="round" d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
                                              </svg>
                                            )}
                                          </button>
                                        </>
                                      )}
                                    </div>
                                  </div>

                                  {/* Inline mapping form */}
                                  {isMappingExpanded && (
                                    <div className="px-4 pb-3 pt-1 border-t border-amber-100 dark:border-amber-900/30 bg-amber-50/80 dark:bg-amber-900/10">
                                      <div className="flex items-center gap-3">
                                        <label className="text-xs text-gray-500 dark:text-gray-400 whitespace-nowrap flex-shrink-0">
                                          CPE URI:
                                        </label>
                                        <input
                                          type="text"
                                          placeholder="cpe:2.3:a:vendor:product:*:*:*:*:*:*:*:*"
                                          value={cpeInputs[key] || ""}
                                          onChange={(e) =>
                                            setCpeInputs((prev) => ({ ...prev, [key]: e.target.value }))
                                          }
                                          onKeyDown={(e) => {
                                            if (e.key === "Enter" && (cpeInputs[key] || "").trim()) {
                                              handleSaveMapping(entry);
                                            }
                                            if (e.key === "Escape") {
                                              setExpandedMappingRow(null);
                                            }
                                          }}
                                          className="flex-1 px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                          autoFocus
                                          disabled={isSaving}
                                        />
                                        <button
                                          onClick={() => handleSaveMapping(entry)}
                                          disabled={isSaving || !(cpeInputs[key] || "").trim()}
                                          className="px-3 py-1.5 text-xs font-medium rounded-md bg-amber-600 text-white hover:bg-amber-700 disabled:opacity-40 disabled:cursor-not-allowed transition-colors flex items-center gap-1.5 flex-shrink-0"
                                        >
                                          {isSaving ? (
                                            <>
                                              <div className="animate-spin rounded-full h-3 w-3 border-b-2 border-white" />
                                              Saving...
                                            </>
                                          ) : (
                                            "Save Mapping"
                                          )}
                                        </button>
                                        <button
                                          onClick={() => setExpandedMappingRow(null)}
                                          disabled={isSaving}
                                          className="px-2 py-1.5 text-xs text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200 transition-colors flex-shrink-0"
                                        >
                                          Cancel
                                        </button>
                                        <a
                                          href={`https://nvd.nist.gov/products/cpe/search/results?keyword=${encodeURIComponent(entry.softwareName)}`}
                                          target="_blank"
                                          rel="noopener noreferrer"
                                          className="text-xs text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 whitespace-nowrap flex items-center gap-1 flex-shrink-0"
                                          onClick={(e) => e.stopPropagation()}
                                        >
                                          Search NVD
                                          <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                                            <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
                                          </svg>
                                        </a>
                                      </div>
                                    </div>
                                  )}
                                </div>
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>

                  {/* Pagination */}
                  {totalPages > 1 && (
                    <div className="flex items-center justify-between border-t border-gray-200 dark:border-gray-700 px-4 py-3 bg-gray-50 dark:bg-gray-700/50 rounded-b-md mt-2">
                      <span className="text-xs text-gray-500 dark:text-gray-400">
                        {currentPage * pageSize + 1}&ndash;{Math.min((currentPage + 1) * pageSize, entries.length)} of {entries.length}
                      </span>
                      <div className="flex items-center gap-2">
                        <button
                          onClick={() => setCurrentPage((p) => p - 1)}
                          disabled={currentPage === 0}
                          className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                        >
                          Previous
                        </button>
                        <span className="text-xs text-gray-500 dark:text-gray-400">
                          {currentPage + 1} / {totalPages}
                        </span>
                        <button
                          onClick={() => setCurrentPage((p) => p + 1)}
                          disabled={(currentPage + 1) * pageSize >= entries.length}
                          className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                        >
                          Next
                        </button>
                      </div>
                    </div>
                  )}
                </>
              )}
            </>
          )}

          {/* ==================== MAPPED TAB ==================== */}
          {activeTab === "mapped" && (
            <>
              {mappedLoading ? (
                <div className="flex items-center justify-center py-8">
                  <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" />
                  <span className="ml-3 text-sm text-gray-500 dark:text-gray-400">Loading CPE mappings...</span>
                </div>
              ) : (
                <>
                  {/* Search bar + Export + Refresh */}
                  <div className="flex flex-wrap items-center gap-3 py-4">
                    <div className="relative flex-1 min-w-[200px] max-w-md">
                      <svg className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M21 21l-5.197-5.197m0 0A7.5 7.5 0 105.196 5.196a7.5 7.5 0 0010.607 10.607z" />
                      </svg>
                      <input
                        type="text"
                        placeholder="Search by product, vendor, CPE URI, category..."
                        value={searchQuery}
                        onChange={(e) => setSearchQuery(e.target.value)}
                        className="w-full pl-9 pr-3 py-2 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                      />
                      {searchQuery && (
                        <button
                          onClick={() => setSearchQuery("")}
                          className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
                        >
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                          </svg>
                        </button>
                      )}
                    </div>
                    <span className="text-sm text-gray-600 dark:text-gray-300">
                      <span className="font-semibold text-amber-600 dark:text-amber-400">{filteredMappedEntries.length}</span>
                      {searchQuery ? ` of ${mappedEntries.length}` : ""} mappings
                    </span>
                    <div className="ml-auto flex items-center gap-2">
                      <button
                        onClick={handleExportJson}
                        disabled={filteredMappedEntries.length === 0}
                        className="text-sm text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 flex items-center gap-1.5 disabled:opacity-40 disabled:cursor-not-allowed"
                      >
                        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M3 16.5v2.25A2.25 2.25 0 005.25 21h13.5A2.25 2.25 0 0021 18.75V16.5M16.5 12L12 16.5m0 0L7.5 12m4.5 4.5V3" />
                        </svg>
                        Export JSON
                      </button>
                      <button
                        onClick={fetchCpeMappings}
                        className="text-sm text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 flex items-center gap-1.5"
                      >
                        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M16.023 9.348h4.992v-.001M2.985 19.644v-4.992m0 0h4.992m-4.993 0l3.181 3.183a8.25 8.25 0 0013.803-3.7M4.031 9.865a8.25 8.25 0 0113.803-3.7l3.181 3.182M20.996 19.632h-4.991" />
                        </svg>
                        Refresh
                      </button>
                    </div>
                  </div>

                  {filteredMappedEntries.length === 0 ? (
                    <div className="text-center py-8 text-gray-500 dark:text-gray-400">
                      <p className="text-sm">
                        {searchQuery
                          ? "No mappings match your search."
                          : "No CPE mappings found. Seed mappings may need to be imported."}
                      </p>
                    </div>
                  ) : (
                    <>
                      {/* Mapped Table */}
                      <div className="overflow-x-auto">
                        <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
                          <thead className="bg-gray-50 dark:bg-gray-700/50">
                            <tr>
                              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Product</th>
                              <th className="w-36 px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Vendor</th>
                              <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">CPE URI</th>
                              <th className="w-28 px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Category</th>
                              <th className="w-24 px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Source</th>
                              <th className="w-32 px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Actions</th>
                            </tr>
                          </thead>
                          <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                            {paginatedMappedEntries.map((entry) => {
                              const key = getMappedRowKey(entry);
                              const isEditing = editingRow === key;

                              return (
                                <tr key={key} className="group">
                                  <td colSpan={6} className="p-0">
                                    <div className={`hover:bg-amber-50/50 dark:hover:bg-amber-900/5 transition-colors ${isEditing ? "bg-amber-50/50 dark:bg-amber-900/5" : ""}`}>
                                      <div className="flex">
                                        <div className="px-4 py-3 text-sm text-gray-900 dark:text-gray-100 flex-1 min-w-0 truncate" title={entry.normalizedProduct}>
                                          {entry.normalizedProduct}
                                        </div>
                                        <div className="px-4 py-3 text-sm text-gray-600 dark:text-gray-400 flex-shrink-0 w-36 truncate" title={entry.normalizedVendor}>
                                          {entry.normalizedVendor || <span className="text-gray-300 dark:text-gray-600 italic">unknown</span>}
                                        </div>
                                        <div className="px-4 py-3 text-sm text-gray-700 dark:text-gray-300 flex-1 min-w-0 font-mono text-xs truncate" title={entry.cpeUri}>
                                          {entry.cpeUri}
                                        </div>
                                        <div className="px-4 py-3 text-sm text-gray-600 dark:text-gray-400 flex-shrink-0 w-28 truncate">
                                          {entry.category || <span className="italic text-gray-300 dark:text-gray-600">-</span>}
                                        </div>
                                        <div className="px-4 py-3 text-sm flex-shrink-0 w-24">
                                          <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${getSourceBadge(entry.source)}`}>
                                            {entry.source}
                                          </span>
                                        </div>
                                        <div className="px-4 py-3 text-sm flex-shrink-0 w-32 flex items-center gap-2">
                                          <button
                                            onClick={() => isEditing ? setEditingRow(null) : startEditing(entry)}
                                            className={`text-xs px-2.5 py-1 rounded-md font-medium transition-colors ${
                                              isEditing
                                                ? "bg-amber-200 text-amber-800 dark:bg-amber-800 dark:text-amber-200"
                                                : "bg-gray-100 text-gray-600 hover:bg-amber-100 hover:text-amber-700 dark:bg-gray-700 dark:text-gray-400 dark:hover:bg-amber-900/50 dark:hover:text-amber-400"
                                            }`}
                                          >
                                            {isEditing ? "Close" : "Edit"}
                                          </button>
                                          {entry.source === "custom" && (
                                            <button
                                              onClick={() => handleDeleteMapping(entry)}
                                              disabled={deletingRow === key}
                                              className="text-xs px-2.5 py-1 rounded-md font-medium transition-colors bg-red-50 text-red-600 hover:bg-red-100 dark:bg-red-900/20 dark:text-red-400 dark:hover:bg-red-900/40 disabled:opacity-40 disabled:cursor-not-allowed"
                                            >
                                              {deletingRow === key ? "..." : "Delete"}
                                            </button>
                                          )}
                                        </div>
                                      </div>

                                      {/* Inline edit form */}
                                      {isEditing && (
                                        <div className="px-4 pb-3 pt-2 border-t border-amber-100 dark:border-amber-900/30 bg-amber-50/80 dark:bg-amber-900/10">
                                          <div className="grid grid-cols-2 gap-3 mb-3">
                                            <div>
                                              <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">CPE URI</label>
                                              <input
                                                type="text"
                                                value={editForm.cpeUri}
                                                onChange={(e) => setEditForm((f) => ({ ...f, cpeUri: e.target.value }))}
                                                className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                                disabled={savingEdit}
                                              />
                                            </div>
                                            <div>
                                              <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">Category</label>
                                              <input
                                                type="text"
                                                value={editForm.category}
                                                onChange={(e) => setEditForm((f) => ({ ...f, category: e.target.value }))}
                                                className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                                disabled={savingEdit}
                                              />
                                            </div>
                                            <div>
                                              <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">CPE Vendor</label>
                                              <input
                                                type="text"
                                                value={editForm.cpeVendor}
                                                onChange={(e) => setEditForm((f) => ({ ...f, cpeVendor: e.target.value }))}
                                                className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                                disabled={savingEdit}
                                              />
                                            </div>
                                            <div>
                                              <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">CPE Product</label>
                                              <input
                                                type="text"
                                                value={editForm.cpeProduct}
                                                onChange={(e) => setEditForm((f) => ({ ...f, cpeProduct: e.target.value }))}
                                                className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                                disabled={savingEdit}
                                              />
                                            </div>
                                            <div>
                                              <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">Display Name Patterns (comma-separated)</label>
                                              <input
                                                type="text"
                                                value={editForm.displayNamePatterns}
                                                onChange={(e) => setEditForm((f) => ({ ...f, displayNamePatterns: e.target.value }))}
                                                className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                                disabled={savingEdit}
                                              />
                                            </div>
                                            <div>
                                              <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">Publisher Patterns (comma-separated)</label>
                                              <input
                                                type="text"
                                                value={editForm.publisherPatterns}
                                                onChange={(e) => setEditForm((f) => ({ ...f, publisherPatterns: e.target.value }))}
                                                className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                                disabled={savingEdit}
                                              />
                                            </div>
                                            <div className="col-span-2">
                                              <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">Exclude Patterns (comma-separated)</label>
                                              <input
                                                type="text"
                                                value={editForm.excludePatterns}
                                                onChange={(e) => setEditForm((f) => ({ ...f, excludePatterns: e.target.value }))}
                                                placeholder="e.g. edge update, webview helper"
                                                className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                                disabled={savingEdit}
                                              />
                                            </div>
                                          </div>
                                          <div className="flex items-center gap-3">
                                            <button
                                              onClick={() => handleSaveEdit(entry)}
                                              disabled={savingEdit || !editForm.cpeUri.trim()}
                                              className="px-3 py-1.5 text-xs font-medium rounded-md bg-amber-600 text-white hover:bg-amber-700 disabled:opacity-40 disabled:cursor-not-allowed transition-colors flex items-center gap-1.5"
                                            >
                                              {savingEdit ? (
                                                <>
                                                  <div className="animate-spin rounded-full h-3 w-3 border-b-2 border-white" />
                                                  Saving...
                                                </>
                                              ) : (
                                                "Save Changes"
                                              )}
                                            </button>
                                            <button
                                              onClick={() => setEditingRow(null)}
                                              disabled={savingEdit}
                                              className="px-2 py-1.5 text-xs text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200 transition-colors"
                                            >
                                              Cancel
                                            </button>
                                            {entry.source === "seed" && (
                                              <span className="text-xs text-gray-400 dark:text-gray-500 italic ml-auto">
                                                Editing a seed entry will create a custom override
                                              </span>
                                            )}
                                          </div>
                                        </div>
                                      )}
                                    </div>
                                  </td>
                                </tr>
                              );
                            })}
                          </tbody>
                        </table>
                      </div>

                      {/* Mapped Pagination */}
                      {mappedTotalPages > 1 && (
                        <div className="flex items-center justify-between border-t border-gray-200 dark:border-gray-700 px-4 py-3 bg-gray-50 dark:bg-gray-700/50 rounded-b-md mt-2">
                          <span className="text-xs text-gray-500 dark:text-gray-400">
                            {mappedPage * pageSize + 1}&ndash;{Math.min((mappedPage + 1) * pageSize, filteredMappedEntries.length)} of {filteredMappedEntries.length}
                          </span>
                          <div className="flex items-center gap-2">
                            <button
                              onClick={() => setMappedPage((p) => p - 1)}
                              disabled={mappedPage === 0}
                              className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                            >
                              Previous
                            </button>
                            <span className="text-xs text-gray-500 dark:text-gray-400">
                              {mappedPage + 1} / {mappedTotalPages}
                            </span>
                            <button
                              onClick={() => setMappedPage((p) => p + 1)}
                              disabled={(mappedPage + 1) * pageSize >= filteredMappedEntries.length}
                              className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                            >
                              Next
                            </button>
                          </div>
                        </div>
                      )}
                    </>
                  )}
                </>
              )}
            </>
          )}

          {/* ==================== IGNORED TAB ==================== */}
          {activeTab === "ignored" && (
            <>
              {ignoredLoading ? (
                <div className="flex items-center justify-center py-8">
                  <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" />
                  <span className="ml-3 text-sm text-gray-500 dark:text-gray-400">Loading ignored software...</span>
                </div>
              ) : ignoredEntries.length === 0 ? (
                <div className="text-center py-8 text-gray-500 dark:text-gray-400">
                  <svg className="w-12 h-12 mx-auto mb-3 text-gray-300 dark:text-gray-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
                  </svg>
                  <p className="text-sm">No ignored software. Items you ignore from the Unmapped tab will appear here.</p>
                </div>
              ) : (
                <>
                  {/* Stats bar */}
                  <div className="flex flex-wrap items-center gap-4 py-4 text-sm">
                    <span className="text-gray-600 dark:text-gray-300">
                      <span className="font-semibold text-gray-700 dark:text-gray-200">{ignoredEntries.length}</span> ignored software entries
                    </span>
                    <button
                      onClick={fetchIgnoredSoftware}
                      className="ml-auto text-sm text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 flex items-center gap-1.5"
                    >
                      <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M16.023 9.348h4.992v-.001M2.985 19.644v-4.992m0 0h4.992m-4.993 0l3.181 3.183a8.25 8.25 0 0013.803-3.7M4.031 9.865a8.25 8.25 0 0113.803-3.7l3.181 3.182M20.996 19.632h-4.991" />
                      </svg>
                      Refresh
                    </button>
                  </div>

                  {/* Table */}
                  <div className="overflow-x-auto">
                    <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
                      <thead className="bg-gray-50 dark:bg-gray-700/50">
                        <tr>
                          <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Software Name</th>
                          <th className="w-36 px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Publisher</th>
                          <th className="w-28 px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Ignored At</th>
                          <th className="w-48 px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Actions</th>
                        </tr>
                      </thead>
                      <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                        {paginatedIgnoredEntries.map((entry) => {
                          const key = `${entry.softwareName}::${entry.publisher}`;
                          const isRestoring = restoringRow === key;

                          return (
                            <tr key={key} className="hover:bg-gray-50 dark:hover:bg-gray-700/30 transition-colors">
                              <td className="px-4 py-3 text-sm text-gray-900 dark:text-gray-100">
                                {entry.softwareName}
                              </td>
                              <td className="px-4 py-3 text-sm text-gray-600 dark:text-gray-400 truncate">
                                {entry.publisher || <span className="text-gray-300 dark:text-gray-600 italic">unknown</span>}
                              </td>
                              <td className="px-4 py-3 text-sm text-gray-600 dark:text-gray-400 whitespace-nowrap">
                                {formatDate(entry.ignoredAt)}
                              </td>
                              <td className="px-4 py-3 text-sm">
                                <div className="flex items-center gap-2">
                                  <button
                                    onClick={() => handleRestoreSoftware(entry)}
                                    disabled={isRestoring}
                                    className="text-xs px-2.5 py-1 rounded-md font-medium bg-gray-100 text-gray-600 hover:bg-amber-100 hover:text-amber-700 dark:bg-gray-700 dark:text-gray-400 dark:hover:bg-amber-900/50 dark:hover:text-amber-400 disabled:opacity-40 transition-colors"
                                  >
                                    {isRestoring ? "..." : "Restore"}
                                  </button>
                                  <button
                                    onClick={() => handleCheckNvdFromIgnored(entry)}
                                    disabled={isRestoring}
                                    className="text-xs px-2.5 py-1 rounded-md font-medium bg-blue-50 text-blue-600 hover:bg-blue-100 dark:bg-blue-900/20 dark:text-blue-400 dark:hover:bg-blue-900/40 disabled:opacity-40 transition-colors"
                                  >
                                    {isRestoring ? "..." : "Check NVD"}
                                  </button>
                                  <a
                                    href={`https://nvd.nist.gov/products/cpe/search/results?keyword=${encodeURIComponent(entry.softwareName)}`}
                                    target="_blank"
                                    rel="noopener noreferrer"
                                    className="text-xs text-gray-500 hover:text-amber-600 dark:text-gray-400 dark:hover:text-amber-400 transition-colors"
                                    title="Search NVD manually"
                                  >
                                    <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                                      <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
                                    </svg>
                                  </a>
                                </div>
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>

                  {/* Ignored Pagination */}
                  {ignoredTotalPages > 1 && (
                    <div className="flex items-center justify-between border-t border-gray-200 dark:border-gray-700 px-4 py-3 bg-gray-50 dark:bg-gray-700/50 rounded-b-md mt-2">
                      <span className="text-xs text-gray-500 dark:text-gray-400">
                        {ignoredPage * pageSize + 1}&ndash;{Math.min((ignoredPage + 1) * pageSize, ignoredEntries.length)} of {ignoredEntries.length}
                      </span>
                      <div className="flex items-center gap-2">
                        <button
                          onClick={() => setIgnoredPage((p) => p - 1)}
                          disabled={ignoredPage === 0}
                          className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                        >
                          Previous
                        </button>
                        <span className="text-xs text-gray-500 dark:text-gray-400">
                          {ignoredPage + 1} / {ignoredTotalPages}
                        </span>
                        <button
                          onClick={() => setIgnoredPage((p) => p + 1)}
                          disabled={(ignoredPage + 1) * pageSize >= ignoredEntries.length}
                          className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                        >
                          Next
                        </button>
                      </div>
                    </div>
                  )}
                </>
              )}
            </>
          )}
        </div>
    </div>
  );
}
