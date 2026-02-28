"use client";

import { useEffect, useState, useCallback } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { API_BASE_URL } from "@/lib/config";
import { downloadAsJson, stripInternalFields } from "@/lib/rulePageHelpers";
import { StatCard } from "@/components/rules/StatCard";
import { RuleFilterBar } from "@/components/rules/RuleFilterBar";
import { EmptyState } from "@/components/rules/EmptyState";
import { FormJsonToggle, JsonModeToggleButtons } from "@/components/rules/FormJsonToggle";

interface ImeLogPattern {
  patternId: string;
  category: string;
  pattern: string;
  action: string;
  parameters?: Record<string, string>;
  description?: string;
  enabled: boolean;
  isBuiltIn: boolean;
}

interface PatternForm {
  category: string;
  pattern: string;
  action: string;
  parameters: Record<string, string>;
  description: string;
  enabled: boolean;
}

const CATEGORIES = ["always", "currentPhase", "otherPhases"] as const;
const ACTIONS = [
  "setCurrentApp", "updateStateInstalled", "updateStateDownloading",
  "updateStateInstalling", "updateStateSkipped", "updateStateError",
  "updateStatePostponed", "espPhaseDetected", "imeStarted",
  "policiesDiscovered", "ignoreCompletedApp", "imeAgentVersion",
  "espTrackStatus", "updateName", "updateWin32AppState",
  "cancelStuckAndSetCurrent", "imeSessionChange", "imeImpersonation",
  "enrollmentCompleted",
] as const;

const CATEGORY_COLORS: Record<string, { bg: string; text: string }> = {
  always: { bg: "bg-emerald-100", text: "text-emerald-700" },
  currentPhase: { bg: "bg-blue-100", text: "text-blue-700" },
  otherPhases: { bg: "bg-purple-100", text: "text-purple-700" },
};

const CATEGORY_LABELS: Record<string, string> = {
  always: "Always",
  currentPhase: "Current Phase",
  otherPhases: "Other Phases",
};

const ACTION_LABELS: Record<string, string> = {
  setCurrentApp: "Set Current App",
  updateStateInstalled: "State → Installed",
  updateStateDownloading: "State → Downloading",
  updateStateInstalling: "State → Installing",
  updateStateSkipped: "State → Skipped",
  updateStateError: "State → Error",
  updateStatePostponed: "State → Postponed",
  espPhaseDetected: "ESP Phase Detected",
  imeStarted: "IME Started",
  policiesDiscovered: "Policies Discovered",
  ignoreCompletedApp: "Ignore Completed App",
  imeAgentVersion: "IME Agent Version",
  espTrackStatus: "ESP Track Status",
  updateName: "Update Name",
  updateWin32AppState: "Win32 App State",
  cancelStuckAndSetCurrent: "Cancel Stuck & Set Current",
  imeSessionChange: "Session Change",
  imeImpersonation: "IME Impersonation",
  enrollmentCompleted: "Enrollment Completed",
};

const EMPTY_FORM: PatternForm = {
  category: "always",
  pattern: "",
  action: "setCurrentApp",
  parameters: {},
  description: "",
  enabled: true,
};


export default function ImeLogPatternsPage() {
  const router = useRouter();
  const { tenantId } = useTenant();
  const { getAccessToken, user } = useAuth();
  const isGalacticAdmin = user?.isGalacticAdmin ?? false;
  const [galacticAdminMode, setGalacticAdminMode] = useState(false);

  // Data
  const [patterns, setPatterns] = useState<ImeLogPattern[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  // Filters
  const [searchQuery, setSearchQuery] = useState("");
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [actionFilter, setActionFilter] = useState("all");

  // Expand/Edit
  const [expandedPatternId, setExpandedPatternId] = useState<string | null>(null);
  const [editingPatternId, setEditingPatternId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<PatternForm>({ ...EMPTY_FORM });
  const [saving, setSaving] = useState(false);

  // JSON mode
  const [jsonModeEdit, setJsonModeEdit] = useState(false);
  const [jsonText, setJsonText] = useState("");
  const [jsonError, setJsonError] = useState<string | null>(null);

  // Toggle
  const [togglingPattern, setTogglingPattern] = useState<string | null>(null);

  // Parameter editing
  const [newParamKey, setNewParamKey] = useState("");
  const [newParamValue, setNewParamValue] = useState("");

  const showSuccess = useCallback((message: string) => {
    setSuccessMessage(message);
    setTimeout(() => setSuccessMessage(null), 3000);
  }, []);

  const showError = useCallback((message: string) => {
    setError(message);
    setTimeout(() => setError(null), 5000);
  }, []);

  // Check galactic admin mode from localStorage
  useEffect(() => {
    const mode = localStorage.getItem("galacticAdminMode") === "true";
    setGalacticAdminMode(mode);
    const handleStorage = () => setGalacticAdminMode(localStorage.getItem("galacticAdminMode") === "true");
    window.addEventListener("storage", handleStorage);
    return () => window.removeEventListener("storage", handleStorage);
  }, []);

  // Fetch patterns
  const fetchPatterns = useCallback(async () => {
    if (!tenantId) return;
    try {
      setLoading(true);
      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");

      const response = await fetch(`${API_BASE_URL}/api/rules/ime-log-patterns`, {
        headers: { Authorization: `Bearer ${token}` },
      });

      if (!response.ok) {
        throw new Error(`Failed to fetch patterns: ${response.statusText}`);
      }

      const data = await response.json();
      if (data.success) {
        setPatterns(data.patterns || []);
      } else {
        throw new Error(data.message || "Failed to fetch patterns");
      }
    } catch (err) {
      console.error("Error fetching patterns:", err);
      showError(err instanceof Error ? err.message : "Failed to fetch patterns");
    } finally {
      setLoading(false);
    }
  }, [tenantId, getAccessToken, showError]);

  useEffect(() => {
    fetchPatterns();
  }, [fetchPatterns]);

  // Toggle enable/disable (Galactic Admin only)
  const handleTogglePattern = async (pattern: ImeLogPattern) => {
    if (!isGalacticAdmin || !galacticAdminMode) return;

    try {
      setTogglingPattern(pattern.patternId);
      setError(null);

      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");

      const response = await fetch(
        `${API_BASE_URL}/api/rules/ime-log-patterns/${encodeURIComponent(pattern.patternId)}?global=true`,
        {
          method: "PUT",
          headers: {
            "Content-Type": "application/json",
            Authorization: `Bearer ${token}`,
          },
          body: JSON.stringify({ ...pattern, enabled: !pattern.enabled }),
        }
      );

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || `Failed to update pattern: ${response.statusText}`);
      }

      setPatterns((prev) =>
        prev.map((p) => (p.patternId === pattern.patternId ? { ...p, enabled: !p.enabled } : p))
      );

      if (pattern.category === "always" && pattern.enabled) {
        showSuccess(`Pattern "${pattern.patternId}" disabled. Warning: This pattern is in the "always" category and may be critical for enrollment tracking.`);
      } else {
        showSuccess(`Pattern "${pattern.patternId}" ${pattern.enabled ? "disabled" : "enabled"} successfully!`);
      }
    } catch (err) {
      console.error("Error toggling pattern:", err);
      showError(err instanceof Error ? err.message : "Failed to update pattern");
    } finally {
      setTogglingPattern(null);
    }
  };

  // Start editing (Galactic Admin only)
  const startEditing = (pattern: ImeLogPattern) => {
    setEditingPatternId(pattern.patternId);
    setEditForm({
      category: pattern.category,
      pattern: pattern.pattern,
      action: pattern.action,
      parameters: pattern.parameters ? { ...pattern.parameters } : {},
      description: pattern.description || "",
      enabled: pattern.enabled,
    });
    setJsonModeEdit(false);
    setJsonError(null);
    setNewParamKey("");
    setNewParamValue("");
  };

  // Save edit (Galactic Admin only)
  const handleSaveEdit = async (patternId: string) => {
    try {
      setSaving(true);
      setError(null);

      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");

      const payload: Partial<ImeLogPattern> = {
        patternId,
        category: editForm.category,
        pattern: editForm.pattern,
        action: editForm.action,
        parameters: Object.keys(editForm.parameters).length > 0 ? editForm.parameters : undefined,
        description: editForm.description || undefined,
        enabled: editForm.enabled,
      };

      const response = await fetch(
        `${API_BASE_URL}/api/rules/ime-log-patterns/${encodeURIComponent(patternId)}?global=true`,
        {
          method: "PUT",
          headers: {
            "Content-Type": "application/json",
            Authorization: `Bearer ${token}`,
          },
          body: JSON.stringify(payload),
        }
      );

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || `Failed to update pattern: ${response.statusText}`);
      }

      setPatterns((prev) =>
        prev.map((p) =>
          p.patternId === patternId
            ? { ...p, ...payload, parameters: payload.parameters || {} }
            : p
        )
      );
      setEditingPatternId(null);
      showSuccess(`Pattern "${patternId}" updated successfully!`);
    } catch (err) {
      console.error("Error saving pattern:", err);
      showError(err instanceof Error ? err.message : "Failed to save pattern");
    } finally {
      setSaving(false);
    }
  };

  // Export helpers
  const handleExportSingle = (pattern: ImeLogPattern) => {
    const cleaned = stripInternalFields(pattern);
    downloadAsJson({ "$schema": "../schema/ime-log-pattern.schema.json", ...cleaned }, `${pattern.patternId}.json`);
  };

  const handleExportAll = () => {
    const cleaned = filteredPatterns.map(stripInternalFields);
    downloadAsJson(cleaned, "ime-log-patterns-export.json");
  };

  // Filtering
  const filteredPatterns = patterns
    .filter((p) => {
      if (categoryFilter !== "all" && p.category !== categoryFilter) return false;
      if (actionFilter !== "all" && p.action !== actionFilter) return false;
      if (searchQuery) {
        const q = searchQuery.toLowerCase();
        return (
          p.patternId.toLowerCase().includes(q) ||
          p.pattern.toLowerCase().includes(q) ||
          p.action.toLowerCase().includes(q) ||
          (p.description || "").toLowerCase().includes(q)
        );
      }
      return true;
    })
    .sort((a, b) => {
      const catOrder = ["always", "currentPhase", "otherPhases"];
      const catDiff = catOrder.indexOf(a.category) - catOrder.indexOf(b.category);
      if (catDiff !== 0) return catDiff;
      return a.patternId.localeCompare(b.patternId);
    });

  // Stats
  const totalPatterns = patterns.length;
  const activePatterns = patterns.filter((p) => p.enabled).length;
  const alwaysCount = patterns.filter((p) => p.category === "always").length;
  const currentPhaseCount = patterns.filter((p) => p.category === "currentPhase").length;
  const otherPhasesCount = patterns.filter((p) => p.category === "otherPhases").length;

  // Unique actions in data for filter dropdown
  const uniqueActions = [...new Set(patterns.map((p) => p.action))].sort();

  const canEdit = isGalacticAdmin && galacticAdminMode;

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        {/* Header */}
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <button
              onClick={() => router.push("/dashboard")}
              className="text-sm text-gray-600 hover:text-gray-900 mb-2 flex items-center"
            >
              &larr; Back to Dashboard
            </button>
            <div className="flex items-center justify-between">
              <div>
                <h1 className="text-3xl font-bold text-gray-900">IME Log Patterns</h1>
                <p className="text-sm text-gray-600 mt-1">
                  Regex patterns used by the agent to parse Intune Management Extension logs during enrollment tracking.
                  {!canEdit && " Read-only view — editing requires Galactic Admin privileges."}
                </p>
              </div>
              <Link
                href="/docs#ime-log-patterns"
                className="text-sm text-indigo-600 hover:text-indigo-800 flex items-center gap-1"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                Documentation
              </Link>
            </div>
          </div>
        </header>

        {/* Main Content */}
        <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          {loading ? (
            <div className="bg-white rounded-lg shadow p-8 text-center">
              <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-indigo-600 mx-auto"></div>
              <p className="mt-4 text-gray-600">Loading IME log patterns...</p>
            </div>
          ) : (
            <div className="space-y-6">
              {/* Success Message */}
              {successMessage && (
                <div className="bg-green-50 border border-green-200 rounded-lg p-4 flex items-center space-x-3">
                  <svg className="w-5 h-5 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  <span className="text-green-800 font-medium">{successMessage}</span>
                </div>
              )}

              {/* Error Message */}
              {error && (
                <div className="bg-red-50 border border-red-200 rounded-lg p-4 flex items-center space-x-3">
                  <svg className="w-5 h-5 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  <span className="text-red-800">{error}</span>
                </div>
              )}

              {/* Summary Stats */}
              <div className="grid grid-cols-2 sm:grid-cols-5 gap-4">
                <StatCard label="Total Patterns" value={totalPatterns} />
                <StatCard label="Active" value={activePatterns} valueColor="text-emerald-600" />
                <StatCard label="Always" value={alwaysCount} valueColor="text-emerald-600" />
                <StatCard label="Current Phase" value={currentPhaseCount} valueColor="text-blue-600" />
                <StatCard label="Other Phases" value={otherPhasesCount} valueColor="text-purple-600" />
              </div>

              {/* Filter Bar + Export */}
              <RuleFilterBar
                searchQuery={searchQuery}
                onSearchChange={setSearchQuery}
                searchPlaceholder="Search by pattern ID, regex, action..."
                filters={[
                  {
                    label: "Category",
                    value: categoryFilter,
                    onChange: setCategoryFilter,
                    options: [
                      { value: "all", label: "All Categories" },
                      { value: "always", label: "Always" },
                      { value: "currentPhase", label: "Current Phase" },
                      { value: "otherPhases", label: "Other Phases" },
                    ],
                  },
                  {
                    label: "Action",
                    value: actionFilter,
                    onChange: setActionFilter,
                    options: [
                      { value: "all", label: "All Actions" },
                      ...uniqueActions.map((a) => ({ value: a, label: ACTION_LABELS[a] || a })),
                    ],
                  },
                ]}
                onExportAll={handleExportAll}
              />

              {/* Patterns List */}
              {filteredPatterns.length === 0 ? (
                <EmptyState
                  message="Try adjusting your search or filter criteria."
                  onClearFilters={() => { setSearchQuery(""); setCategoryFilter("all"); setActionFilter("all"); }}
                  showClearButton={!!(searchQuery || categoryFilter !== "all" || actionFilter !== "all")}
                />
              ) : (
                filteredPatterns.map((pattern) => {
                  const isExpanded = expandedPatternId === pattern.patternId;
                  const isEditing = editingPatternId === pattern.patternId;
                  const catColor = CATEGORY_COLORS[pattern.category] || { bg: "bg-gray-100", text: "text-gray-700" };

                  return (
                    <div
                      key={pattern.patternId}
                      className={`bg-white rounded-lg shadow border transition-all ${
                        isExpanded ? "border-indigo-300 ring-1 ring-indigo-200" : "border-gray-200 hover:border-gray-300"
                      } ${!pattern.enabled ? "opacity-60" : ""}`}
                    >
                      {/* Collapsed Header */}
                      <div
                        className="p-4 cursor-pointer select-none"
                        onClick={() => {
                          if (isEditing) return;
                          setExpandedPatternId(isExpanded ? null : pattern.patternId);
                          if (isExpanded && editingPatternId === pattern.patternId) {
                            setEditingPatternId(null);
                          }
                        }}
                      >
                        <div className="flex items-center space-x-4">
                          {/* Enable/Disable Toggle (Galactic Admin only) */}
                          {canEdit ? (
                            <button
                              onClick={(e) => {
                                e.stopPropagation();
                                handleTogglePattern(pattern);
                              }}
                              disabled={togglingPattern === pattern.patternId}
                              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${
                                togglingPattern === pattern.patternId
                                  ? "opacity-50 cursor-not-allowed"
                                  : "cursor-pointer"
                              } ${pattern.enabled ? "bg-emerald-500" : "bg-gray-300"}`}
                              title={pattern.enabled ? "Disable pattern" : "Enable pattern"}
                            >
                              <span
                                className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                                  pattern.enabled ? "translate-x-6" : "translate-x-1"
                                }`}
                              />
                            </button>
                          ) : (
                            <span
                              className={`inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full ${
                                pattern.enabled ? "bg-emerald-500" : "bg-gray-300"
                              }`}
                              title={pattern.enabled ? "Enabled" : "Disabled"}
                            >
                              <span
                                className={`inline-block h-4 w-4 transform rounded-full bg-white ${
                                  pattern.enabled ? "translate-x-6" : "translate-x-1"
                                }`}
                              />
                            </span>
                          )}

                          {/* Pattern ID */}
                          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-mono font-medium bg-gray-100 text-gray-600 border border-gray-200 flex-shrink-0 hidden sm:inline-flex">
                            {pattern.patternId}
                          </span>

                          {/* Description or Pattern */}
                          <div className="flex-1 min-w-0">
                            <h3 className="text-sm font-semibold text-gray-900 truncate">
                              {pattern.description || pattern.patternId}
                            </h3>
                          </div>

                          {/* Category Badge */}
                          <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${catColor.bg} ${catColor.text} flex-shrink-0`}>
                            {CATEGORY_LABELS[pattern.category] || pattern.category}
                          </span>

                          {/* Action Badge */}
                          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-50 text-gray-600 flex-shrink-0 hidden md:inline-flex">
                            {ACTION_LABELS[pattern.action] || pattern.action}
                          </span>

                          {/* Built-in Badge */}
                          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-blue-50 text-blue-600 border border-blue-200 flex-shrink-0">
                            Built-in
                          </span>

                          {/* Expand/Collapse Arrow */}
                          <svg
                            className={`w-5 h-5 text-gray-400 transition-transform flex-shrink-0 ${
                              isExpanded ? "rotate-180" : ""
                            }`}
                            fill="none"
                            stroke="currentColor"
                            viewBox="0 0 24 24"
                          >
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                          </svg>
                        </div>
                      </div>

                      {/* Expanded Details (Read-Only) */}
                      {isExpanded && !isEditing && (
                        <div className="border-t border-gray-200 p-6 space-y-6">
                          {/* Description */}
                          {pattern.description && (
                            <div className="text-sm text-gray-700">
                              {pattern.description}
                            </div>
                          )}

                          {/* Pattern (Regex) */}
                          <div>
                            <h4 className="text-sm font-semibold text-gray-700 mb-2">Regex Pattern</h4>
                            <div className="bg-gray-50 border border-gray-200 rounded-lg p-4">
                              <code className="text-sm font-mono text-gray-800 break-all whitespace-pre-wrap">
                                {pattern.pattern}
                              </code>
                            </div>
                          </div>

                          {/* Action + Category Details */}
                          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                            <div>
                              <h4 className="text-sm font-semibold text-gray-700 mb-2">Action</h4>
                              <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 text-sm">
                                <div className="flex items-center gap-2">
                                  <svg className="w-4 h-4 text-indigo-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
                                  </svg>
                                  <span className="font-semibold text-gray-900">{ACTION_LABELS[pattern.action] || pattern.action}</span>
                                  <code className="px-1.5 py-0.5 bg-gray-200 rounded text-xs font-mono text-gray-600">{pattern.action}</code>
                                </div>
                              </div>
                            </div>
                            <div>
                              <h4 className="text-sm font-semibold text-gray-700 mb-2">Category</h4>
                              <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 text-sm">
                                <div className="flex items-center gap-2">
                                  <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${catColor.bg} ${catColor.text}`}>
                                    {CATEGORY_LABELS[pattern.category] || pattern.category}
                                  </span>
                                  {pattern.category === "always" && (
                                    <span className="text-xs text-gray-500">Evaluated on every log line</span>
                                  )}
                                  {pattern.category === "currentPhase" && (
                                    <span className="text-xs text-gray-500">Only during the active ESP phase</span>
                                  )}
                                  {pattern.category === "otherPhases" && (
                                    <span className="text-xs text-gray-500">During non-active phases</span>
                                  )}
                                </div>
                              </div>
                            </div>
                          </div>

                          {/* Parameters */}
                          {pattern.parameters && Object.keys(pattern.parameters).length > 0 && (
                            <div>
                              <h4 className="text-sm font-semibold text-gray-700 mb-2">Parameters</h4>
                              <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 text-sm">
                                <div className="space-y-1">
                                  {Object.entries(pattern.parameters).map(([key, value]) => (
                                    <div key={key} className="flex items-start gap-2">
                                      <code className="px-1.5 py-0.5 bg-indigo-100 rounded text-xs font-mono text-indigo-700">{key}</code>
                                      <span className="text-gray-400">=</span>
                                      <code className="px-1.5 py-0.5 bg-gray-200 rounded text-xs font-mono text-gray-700 break-all">{value}</code>
                                    </div>
                                  ))}
                                </div>
                              </div>
                            </div>
                          )}

                          {/* Actions */}
                          <div className="flex items-center justify-end space-x-3 pt-4 border-t border-gray-200">
                            <button
                              onClick={() => handleExportSingle(pattern)}
                              className="px-4 py-2 text-sm bg-gray-100 text-gray-700 rounded-lg hover:bg-gray-200 transition-colors flex items-center space-x-2"
                              title="Export pattern as JSON"
                            >
                              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                              </svg>
                              <span>Export</span>
                            </button>
                            {canEdit && (
                              <button
                                onClick={() => startEditing(pattern)}
                                className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 transition-colors flex items-center space-x-2"
                                title="Edit pattern (Galactic Admin)"
                              >
                                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                                </svg>
                                <span>Edit (Global)</span>
                              </button>
                            )}
                          </div>
                        </div>
                      )}

                      {/* Edit Form (Galactic Admin only) */}
                      {isExpanded && isEditing && canEdit && (
                        <div className="border-t border-gray-200 p-6">
                          <div className="flex items-center justify-between mb-4">
                            <div className="flex items-center space-x-2">
                              <svg className="w-5 h-5 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                              </svg>
                              <h4 className="text-sm font-semibold text-gray-900">
                                Editing: {pattern.patternId}
                                <span className="ml-2 inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-700 border border-amber-200">
                                  Global Edit
                                </span>
                              </h4>
                            </div>
                            {/* JSON Mode Toggle */}
                            <JsonModeToggleButtons
                              jsonMode={jsonModeEdit}
                              onToggleMode={(mode) => {
                                if (mode) { setJsonText(JSON.stringify(editForm, null, 2)); }
                                setJsonModeEdit(mode);
                                setJsonError(null);
                              }}
                            />
                          </div>

                          <div className="mb-4 bg-amber-50 border border-amber-200 rounded-lg p-3 text-sm text-amber-800">
                            <strong>Galactic Admin:</strong> Changes will apply globally to all tenants.
                          </div>

                          {editForm.category === "always" && (
                            <div className="mb-4 bg-red-50 border border-red-200 rounded-lg p-3 text-sm text-red-800">
                              <strong>Warning:</strong> This pattern is in the &quot;always&quot; category and is critical for enrollment tracking. Changes may affect all enrollment sessions.
                            </div>
                          )}

                          <FormJsonToggle
                            jsonMode={jsonModeEdit}
                            onToggleMode={(mode) => {
                              if (mode) { setJsonText(JSON.stringify(editForm, null, 2)); }
                              setJsonModeEdit(mode);
                              setJsonError(null);
                            }}
                            jsonText={jsonText}
                            onJsonTextChange={(text) => setJsonText(text)}
                            jsonError={jsonError}
                            onApplyJson={() => {
                              try {
                                const parsed = JSON.parse(jsonText) as PatternForm;
                                if (!parsed.pattern) throw new Error("JSON must include pattern");
                                setEditForm({ ...editForm, ...parsed });
                                setJsonModeEdit(false);
                                setJsonError(null);
                              } catch (e) {
                                setJsonError(e instanceof Error ? e.message : "Invalid JSON");
                              }
                            }}
                            textareaRows={15}
                            description="Edit the pattern as JSON."
                          >
                            <div className="space-y-4">
                              {/* Category */}
                              <div>
                                <label className="block text-sm font-medium text-gray-700 mb-1">Category</label>
                                <select
                                  value={editForm.category}
                                  onChange={(e) => setEditForm({ ...editForm, category: e.target.value })}
                                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500"
                                >
                                  {CATEGORIES.map((c) => (
                                    <option key={c} value={c}>{CATEGORY_LABELS[c]}</option>
                                  ))}
                                </select>
                              </div>

                              {/* Regex Pattern */}
                              <div>
                                <label className="block text-sm font-medium text-gray-700 mb-1">Regex Pattern</label>
                                <textarea
                                  value={editForm.pattern}
                                  onChange={(e) => setEditForm({ ...editForm, pattern: e.target.value })}
                                  rows={3}
                                  className="w-full font-mono text-sm px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500"
                                  spellCheck={false}
                                  placeholder="e.g., EMS Agent Started"
                                />
                                <p className="mt-1 text-xs text-gray-500">C# regex syntax. Capture groups are used by some actions to extract values.</p>
                              </div>

                              {/* Action */}
                              <div>
                                <label className="block text-sm font-medium text-gray-700 mb-1">Action</label>
                                <select
                                  value={editForm.action}
                                  onChange={(e) => setEditForm({ ...editForm, action: e.target.value })}
                                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500"
                                >
                                  {ACTIONS.map((a) => (
                                    <option key={a} value={a}>{ACTION_LABELS[a]} ({a})</option>
                                  ))}
                                </select>
                                <p className="mt-1 text-xs text-gray-500">Actions are hardcoded in the agent. Only use actions the agent supports.</p>
                              </div>

                              {/* Description */}
                              <div>
                                <label className="block text-sm font-medium text-gray-700 mb-1">Description</label>
                                <textarea
                                  value={editForm.description}
                                  onChange={(e) => setEditForm({ ...editForm, description: e.target.value })}
                                  rows={2}
                                  className="w-full text-sm px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500"
                                  placeholder="What this pattern detects and why it matters..."
                                />
                              </div>

                              {/* Enabled */}
                              <div className="flex items-center gap-2">
                                <input
                                  type="checkbox"
                                  id="edit-enabled"
                                  checked={editForm.enabled}
                                  onChange={(e) => setEditForm({ ...editForm, enabled: e.target.checked })}
                                  className="h-4 w-4 text-indigo-600 border-gray-300 rounded focus:ring-indigo-500"
                                />
                                <label htmlFor="edit-enabled" className="text-sm font-medium text-gray-700">Enabled</label>
                              </div>

                              {/* Parameters */}
                              <div>
                                <label className="block text-sm font-medium text-gray-700 mb-1">Parameters</label>
                                {Object.keys(editForm.parameters).length > 0 && (
                                  <div className="space-y-2 mb-2">
                                    {Object.entries(editForm.parameters).map(([key, value]) => (
                                      <div key={key} className="flex items-center gap-2">
                                        <code className="px-2 py-1 bg-indigo-50 rounded text-xs font-mono text-indigo-700 min-w-[80px]">{key}</code>
                                        <span className="text-gray-400">=</span>
                                        <input
                                          type="text"
                                          value={value}
                                          onChange={(e) => {
                                            const updated = { ...editForm.parameters };
                                            updated[key] = e.target.value;
                                            setEditForm({ ...editForm, parameters: updated });
                                          }}
                                          className="flex-1 text-sm px-2 py-1 border border-gray-300 rounded text-gray-900 focus:ring-1 focus:ring-indigo-500"
                                        />
                                        <button
                                          onClick={() => {
                                            const updated = { ...editForm.parameters };
                                            delete updated[key];
                                            setEditForm({ ...editForm, parameters: updated });
                                          }}
                                          className="text-red-400 hover:text-red-600 p-1"
                                          title="Remove parameter"
                                        >
                                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                                          </svg>
                                        </button>
                                      </div>
                                    ))}
                                  </div>
                                )}
                                <div className="flex items-center gap-2">
                                  <input
                                    type="text"
                                    value={newParamKey}
                                    onChange={(e) => setNewParamKey(e.target.value)}
                                    placeholder="Key"
                                    className="w-32 text-sm px-2 py-1 border border-gray-300 rounded text-gray-900 focus:ring-1 focus:ring-indigo-500"
                                  />
                                  <input
                                    type="text"
                                    value={newParamValue}
                                    onChange={(e) => setNewParamValue(e.target.value)}
                                    placeholder="Value"
                                    className="flex-1 text-sm px-2 py-1 border border-gray-300 rounded text-gray-900 focus:ring-1 focus:ring-indigo-500"
                                  />
                                  <button
                                    onClick={() => {
                                      if (newParamKey.trim()) {
                                        setEditForm({
                                          ...editForm,
                                          parameters: { ...editForm.parameters, [newParamKey.trim()]: newParamValue },
                                        });
                                        setNewParamKey("");
                                        setNewParamValue("");
                                      }
                                    }}
                                    disabled={!newParamKey.trim()}
                                    className="px-3 py-1 text-xs bg-indigo-600 text-white rounded hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed"
                                  >
                                    Add
                                  </button>
                                </div>
                              </div>
                            </div>
                          </FormJsonToggle>

                          {/* Save/Cancel buttons */}
                          <div className="flex items-center justify-end space-x-3 mt-6 pt-4 border-t border-gray-200">
                            <button
                              onClick={() => {
                                setEditingPatternId(null);
                                setJsonModeEdit(false);
                                setJsonError(null);
                              }}
                              className="px-4 py-2 text-sm bg-gray-100 text-gray-700 rounded-lg hover:bg-gray-200 transition-colors"
                            >
                              Cancel
                            </button>
                            <button
                              onClick={() => handleSaveEdit(pattern.patternId)}
                              disabled={saving}
                              className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
                            >
                              {saving ? (
                                <>
                                  <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                                  <span>Saving...</span>
                                </>
                              ) : (
                                <>
                                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                                  </svg>
                                  <span>Save Changes</span>
                                </>
                              )}
                            </button>
                          </div>
                        </div>
                      )}
                    </div>
                  );
                })
              )}
            </div>
          )}
        </main>
      </div>
    </ProtectedRoute>
  );
}
