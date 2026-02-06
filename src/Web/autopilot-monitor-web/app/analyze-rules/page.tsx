"use client";

import { useEffect, useState, useCallback } from "react";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { API_BASE_URL } from "@/lib/config";

interface RelatedDoc {
  title: string;
  url: string;
}

interface RemediationStep {
  title: string;
  steps: string[];
}

interface ConfidenceFactor {
  signal: string;
  condition: string;
  weight: number;
}

interface RuleCondition {
  signal: string;
  source: string;
  eventType: string;
  dataField: string;
  operator: string;
  value: string;
  required: boolean;
}

interface AnalyzeRule {
  ruleId: string;
  title: string;
  description: string;
  severity: string;
  category: string;
  version: string;
  author: string;
  enabled: boolean;
  isBuiltIn: boolean;
  isCommunity: boolean;
  conditions: RuleCondition[];
  baseConfidence: number;
  confidenceFactors: ConfidenceFactor[];
  confidenceThreshold: number;
  explanation: string;
  remediation: RemediationStep[];
  relatedDocs: RelatedDoc[];
  tags: string[];
  createdAt: string;
  updatedAt: string;
}

const SEVERITY_COLORS: Record<string, { bg: string; text: string; border: string; dot: string }> = {
  critical: { bg: "bg-red-100", text: "text-red-800", border: "border-red-300", dot: "bg-red-500" },
  high: { bg: "bg-orange-100", text: "text-orange-800", border: "border-orange-300", dot: "bg-orange-500" },
  warning: { bg: "bg-yellow-100", text: "text-yellow-800", border: "border-yellow-300", dot: "bg-yellow-500" },
  info: { bg: "bg-blue-100", text: "text-blue-800", border: "border-blue-300", dot: "bg-blue-500" },
};

const CATEGORY_COLORS: Record<string, { bg: string; text: string }> = {
  network: { bg: "bg-blue-100", text: "text-blue-700" },
  identity: { bg: "bg-purple-100", text: "text-purple-700" },
  apps: { bg: "bg-orange-100", text: "text-orange-700" },
  device: { bg: "bg-gray-100", text: "text-gray-700" },
  esp: { bg: "bg-teal-100", text: "text-teal-700" },
  enrollment: { bg: "bg-indigo-100", text: "text-indigo-700" },
};

function getSeverityColor(severity: string) {
  return SEVERITY_COLORS[severity.toLowerCase()] || SEVERITY_COLORS.info;
}

function getCategoryColor(category: string) {
  return CATEGORY_COLORS[category.toLowerCase()] || { bg: "bg-gray-100", text: "text-gray-700" };
}

export default function AnalyzeRulesPage() {
  const router = useRouter();
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();

  const [rules, setRules] = useState<AnalyzeRule[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  // Filter state
  const [searchQuery, setSearchQuery] = useState("");
  const [severityFilter, setSeverityFilter] = useState("all");
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [typeFilter, setTypeFilter] = useState("all");

  // Expanded card state
  const [expandedRuleId, setExpandedRuleId] = useState<string | null>(null);

  // Toggling / deleting state
  const [togglingRuleId, setTogglingRuleId] = useState<string | null>(null);
  const [deletingRuleId, setDeletingRuleId] = useState<string | null>(null);

  const showSuccess = useCallback((message: string) => {
    setSuccessMessage(message);
    setTimeout(() => setSuccessMessage(null), 3000);
  }, []);

  const showError = useCallback((message: string) => {
    setError(message);
    setTimeout(() => setError(null), 3000);
  }, []);

  // Fetch rules
  const fetchRules = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error("Failed to get access token");
      }

      const response = await fetch(`${API_BASE_URL}/api/analyze-rules`, {
        headers: {
          Authorization: `Bearer ${token}`,
        },
      });

      if (!response.ok) {
        throw new Error(`Failed to load analyze rules: ${response.statusText}`);
      }

      const data = await response.json();
      if (data.success && Array.isArray(data.rules)) {
        setRules(data.rules);
      } else {
        throw new Error("Unexpected response format");
      }
    } catch (err) {
      console.error("Error fetching analyze rules:", err);
      setError(err instanceof Error ? err.message : "Failed to load analyze rules");
    } finally {
      setLoading(false);
    }
  }, [getAccessToken]);

  useEffect(() => {
    fetchRules();
  }, [fetchRules]);

  // Toggle rule enabled/disabled
  const handleToggleRule = async (rule: AnalyzeRule) => {
    try {
      setTogglingRuleId(rule.ruleId);
      setError(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error("Failed to get access token");
      }

      const response = await fetch(`${API_BASE_URL}/api/analyze-rules/${encodeURIComponent(rule.ruleId)}`, {
        method: "PUT",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${token}`,
        },
        body: JSON.stringify({ ...rule, enabled: !rule.enabled }),
      });

      if (!response.ok) {
        throw new Error(`Failed to update rule: ${response.statusText}`);
      }

      setRules((prev) =>
        prev.map((r) => (r.ruleId === rule.ruleId ? { ...r, enabled: !r.enabled } : r))
      );
      showSuccess(`Rule "${rule.title}" ${!rule.enabled ? "enabled" : "disabled"} successfully!`);
    } catch (err) {
      console.error("Error toggling rule:", err);
      showError(err instanceof Error ? err.message : "Failed to toggle rule");
    } finally {
      setTogglingRuleId(null);
    }
  };

  // Delete custom rule
  const handleDeleteRule = async (rule: AnalyzeRule) => {
    if (!confirm(`Are you sure you want to delete the custom rule "${rule.title}"?`)) {
      return;
    }

    try {
      setDeletingRuleId(rule.ruleId);
      setError(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error("Failed to get access token");
      }

      const response = await fetch(`${API_BASE_URL}/api/analyze-rules/${encodeURIComponent(rule.ruleId)}`, {
        method: "DELETE",
        headers: {
          Authorization: `Bearer ${token}`,
        },
      });

      if (!response.ok) {
        throw new Error(`Failed to delete rule: ${response.statusText}`);
      }

      setRules((prev) => prev.filter((r) => r.ruleId !== rule.ruleId));
      if (expandedRuleId === rule.ruleId) {
        setExpandedRuleId(null);
      }
      showSuccess(`Rule "${rule.title}" deleted successfully!`);
    } catch (err) {
      console.error("Error deleting rule:", err);
      showError(err instanceof Error ? err.message : "Failed to delete rule");
    } finally {
      setDeletingRuleId(null);
    }
  };

  // Filter rules
  const filteredRules = rules.filter((rule) => {
    const matchesSearch =
      searchQuery === "" ||
      rule.title.toLowerCase().includes(searchQuery.toLowerCase()) ||
      rule.ruleId.toLowerCase().includes(searchQuery.toLowerCase());

    const matchesSeverity =
      severityFilter === "all" || rule.severity.toLowerCase() === severityFilter.toLowerCase();

    const matchesCategory =
      categoryFilter === "all" || rule.category.toLowerCase() === categoryFilter.toLowerCase();

    const matchesType =
      typeFilter === "all" ||
      (typeFilter === "builtin" && rule.isBuiltIn) ||
      (typeFilter === "custom" && !rule.isBuiltIn);

    return matchesSearch && matchesSeverity && matchesCategory && matchesType;
  });

  // Summary stats
  const totalRules = rules.length;
  const activeRules = rules.filter((r) => r.enabled).length;
  const criticalCount = rules.filter((r) => r.severity.toLowerCase() === "critical").length;
  const highCount = rules.filter((r) => r.severity.toLowerCase() === "high").length;
  const warningCount = rules.filter((r) => r.severity.toLowerCase() === "warning").length;
  const infoCount = rules.filter((r) => r.severity.toLowerCase() === "info").length;

  // Get unique categories from rules for filter dropdown
  const uniqueCategories = Array.from(new Set(rules.map((r) => r.category.toLowerCase())));

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100">
        {/* Header */}
        <header className="bg-white shadow-sm border-b border-gray-200">
          <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-4">
            <div className="flex items-center justify-between">
              <div className="flex items-center space-x-4">
                <button
                  onClick={() => router.push("/")}
                  className="flex items-center space-x-2 text-gray-600 hover:text-gray-900 transition-colors"
                >
                  <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 19l-7-7m0 0l7-7m-7 7h18" />
                  </svg>
                  <span>Back to Dashboard</span>
                </button>
              </div>
              <div>
                <h1 className="text-2xl font-bold text-gray-900">Analyze Rules</h1>
                <p className="text-sm text-gray-500">Tenant: {tenantId}</p>
              </div>
            </div>
          </div>
        </header>

        {/* Main Content */}
        <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          {/* Success Message */}
          {successMessage && (
            <div className="mb-6 bg-green-50 border border-green-200 rounded-lg p-4 flex items-center space-x-3">
              <svg className="w-5 h-5 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              <span className="text-green-800 font-medium">{successMessage}</span>
            </div>
          )}

          {/* Error Message */}
          {error && (
            <div className="mb-6 bg-red-50 border border-red-200 rounded-lg p-4 flex items-center space-x-3">
              <svg className="w-5 h-5 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              <span className="text-red-800">{error}</span>
            </div>
          )}

          {loading ? (
            <div className="bg-white rounded-lg shadow p-8 text-center">
              <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-indigo-600 mx-auto"></div>
              <p className="mt-4 text-gray-600">Loading analyze rules...</p>
            </div>
          ) : (
            <div className="space-y-6">
              {/* Summary Stats */}
              <div className="grid grid-cols-2 md:grid-cols-6 gap-4">
                <div className="bg-white rounded-lg shadow p-4">
                  <p className="text-sm text-gray-500">Total</p>
                  <p className="text-2xl font-bold text-gray-900">{totalRules}</p>
                </div>
                <div className="bg-white rounded-lg shadow p-4">
                  <p className="text-sm text-gray-500">Active</p>
                  <p className="text-2xl font-bold text-green-600">{activeRules}</p>
                </div>
                <div className="bg-white rounded-lg shadow p-4 border-l-4 border-red-400">
                  <p className="text-sm text-gray-500">Critical</p>
                  <p className="text-2xl font-bold text-red-600">{criticalCount}</p>
                </div>
                <div className="bg-white rounded-lg shadow p-4 border-l-4 border-orange-400">
                  <p className="text-sm text-gray-500">High</p>
                  <p className="text-2xl font-bold text-orange-600">{highCount}</p>
                </div>
                <div className="bg-white rounded-lg shadow p-4 border-l-4 border-yellow-400">
                  <p className="text-sm text-gray-500">Warning</p>
                  <p className="text-2xl font-bold text-yellow-600">{warningCount}</p>
                </div>
                <div className="bg-white rounded-lg shadow p-4 border-l-4 border-blue-400">
                  <p className="text-sm text-gray-500">Info</p>
                  <p className="text-2xl font-bold text-blue-600">{infoCount}</p>
                </div>
              </div>

              {/* Filter Bar */}
              <div className="bg-white rounded-lg shadow p-4">
                <div className="flex flex-col md:flex-row md:items-center md:space-x-4 space-y-3 md:space-y-0">
                  {/* Search */}
                  <div className="flex-1 relative">
                    <svg className="absolute left-3 top-1/2 transform -translate-y-1/2 w-5 h-5 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                    </svg>
                    <input
                      type="text"
                      placeholder="Search by title or rule ID..."
                      value={searchQuery}
                      onChange={(e) => setSearchQuery(e.target.value)}
                      className="w-full pl-10 pr-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                    />
                    {searchQuery && (
                      <button
                        onClick={() => setSearchQuery("")}
                        className="absolute right-3 top-1/2 transform -translate-y-1/2 text-gray-400 hover:text-gray-600 transition-colors"
                        title="Clear search"
                      >
                        <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                        </svg>
                      </button>
                    )}
                  </div>

                  {/* Severity Filter */}
                  <div>
                    <select
                      value={severityFilter}
                      onChange={(e) => setSeverityFilter(e.target.value)}
                      className="w-full md:w-auto px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                    >
                      <option value="all">All Severities</option>
                      <option value="critical">Critical</option>
                      <option value="high">High</option>
                      <option value="warning">Warning</option>
                      <option value="info">Info</option>
                    </select>
                  </div>

                  {/* Category Filter */}
                  <div>
                    <select
                      value={categoryFilter}
                      onChange={(e) => setCategoryFilter(e.target.value)}
                      className="w-full md:w-auto px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                    >
                      <option value="all">All Categories</option>
                      {uniqueCategories.map((cat) => (
                        <option key={cat} value={cat}>
                          {cat.charAt(0).toUpperCase() + cat.slice(1)}
                        </option>
                      ))}
                    </select>
                  </div>

                  {/* Type Filter */}
                  <div>
                    <select
                      value={typeFilter}
                      onChange={(e) => setTypeFilter(e.target.value)}
                      className="w-full md:w-auto px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                    >
                      <option value="all">All Types</option>
                      <option value="builtin">Built-in</option>
                      <option value="custom">Custom</option>
                    </select>
                  </div>
                </div>

                {/* Filter summary */}
                <div className="mt-3 text-sm text-gray-500">
                  Showing {filteredRules.length} of {totalRules} rule{totalRules !== 1 ? "s" : ""}
                </div>
              </div>

              {/* Rules List */}
              {filteredRules.length === 0 ? (
                <div className="bg-white rounded-lg shadow p-8 text-center">
                  <svg className="w-12 h-12 text-gray-400 mx-auto mb-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9.172 16.172a4 4 0 015.656 0M9 10h.01M15 10h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  <p className="text-gray-500">
                    {rules.length === 0
                      ? "No analyze rules found."
                      : "No rules match your current filters."}
                  </p>
                </div>
              ) : (
                <div className="space-y-3">
                  {filteredRules.map((rule) => {
                    const isExpanded = expandedRuleId === rule.ruleId;
                    const sevColor = getSeverityColor(rule.severity);
                    const catColor = getCategoryColor(rule.category);

                    return (
                      <div
                        key={rule.ruleId}
                        className={`bg-white rounded-lg shadow border transition-all ${
                          isExpanded ? "border-indigo-300 ring-1 ring-indigo-200" : "border-gray-200 hover:border-gray-300"
                        }`}
                      >
                        {/* Collapsed Header */}
                        <div
                          className="p-4 cursor-pointer select-none"
                          onClick={() => setExpandedRuleId(isExpanded ? null : rule.ruleId)}
                        >
                          <div className="flex items-center space-x-4">
                            {/* Enable/Disable Toggle */}
                            <button
                              onClick={(e) => {
                                e.stopPropagation();
                                handleToggleRule(rule);
                              }}
                              disabled={togglingRuleId === rule.ruleId}
                              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${
                                togglingRuleId === rule.ruleId
                                  ? "opacity-50 cursor-not-allowed"
                                  : "cursor-pointer"
                              } ${rule.enabled ? "bg-green-500" : "bg-gray-300"}`}
                              title={rule.enabled ? "Disable rule" : "Enable rule"}
                            >
                              <span
                                className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                                  rule.enabled ? "translate-x-6" : "translate-x-1"
                                }`}
                              />
                            </button>

                            {/* Severity Badge */}
                            <span
                              className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold ${sevColor.bg} ${sevColor.text} flex-shrink-0`}
                            >
                              <span className={`w-1.5 h-1.5 rounded-full ${sevColor.dot} mr-1.5`}></span>
                              {rule.severity.charAt(0).toUpperCase() + rule.severity.slice(1)}
                            </span>

                            {/* Rule ID */}
                            <span className="text-xs font-mono text-gray-400 flex-shrink-0 hidden sm:inline">
                              {rule.ruleId}
                            </span>

                            {/* Title */}
                            <div className="flex-1 min-w-0">
                              <h3 className="text-sm font-semibold text-gray-900 truncate">
                                {rule.title}
                              </h3>
                            </div>

                            {/* Category Badge */}
                            <span
                              className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${catColor.bg} ${catColor.text} flex-shrink-0`}
                            >
                              {rule.category.charAt(0).toUpperCase() + rule.category.slice(1)}
                            </span>

                            {/* Type Badge */}
                            <span
                              className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium flex-shrink-0 ${
                                rule.isBuiltIn
                                  ? "bg-gray-100 text-gray-600"
                                  : rule.isCommunity
                                  ? "bg-green-100 text-green-700"
                                  : "bg-indigo-100 text-indigo-700"
                              }`}
                            >
                              {rule.isBuiltIn ? "Built-in" : rule.isCommunity ? "Community" : "Custom"}
                            </span>

                            {/* Confidence Threshold */}
                            <span className="text-xs text-gray-500 flex-shrink-0 hidden md:inline" title="Confidence Threshold">
                              Threshold: {rule.confidenceThreshold}%
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

                        {/* Expanded Details */}
                        {isExpanded && (
                          <div className="border-t border-gray-200 p-6 space-y-6">
                            {/* Meta Info Row */}
                            <div className="flex flex-wrap items-center gap-4 text-sm text-gray-500">
                              <span>
                                <span className="font-medium text-gray-700">Version:</span> {rule.version}
                              </span>
                              <span>
                                <span className="font-medium text-gray-700">Author:</span> {rule.author}
                              </span>
                              <span>
                                <span className="font-medium text-gray-700">Created:</span>{" "}
                                {new Date(rule.createdAt).toLocaleDateString()}
                              </span>
                              <span>
                                <span className="font-medium text-gray-700">Updated:</span>{" "}
                                {new Date(rule.updatedAt).toLocaleDateString()}
                              </span>
                              <span className="text-xs font-mono text-gray-400 sm:hidden">
                                {rule.ruleId}
                              </span>
                            </div>

                            {/* Tags */}
                            {rule.tags.length > 0 && (
                              <div className="flex flex-wrap gap-2">
                                {rule.tags.map((tag, idx) => (
                                  <span
                                    key={idx}
                                    className="inline-flex items-center px-2 py-0.5 rounded text-xs bg-gray-100 text-gray-600"
                                  >
                                    #{tag}
                                  </span>
                                ))}
                              </div>
                            )}

                            {/* Description */}
                            <div>
                              <h4 className="text-sm font-semibold text-gray-700 mb-1">Description</h4>
                              <p className="text-sm text-gray-600 leading-relaxed">{rule.description}</p>
                            </div>

                            {/* Conditions */}
                            {rule.conditions.length > 0 && (
                              <div>
                                <h4 className="text-sm font-semibold text-gray-700 mb-2">
                                  Conditions ({rule.conditions.length})
                                </h4>
                                <div className="space-y-2">
                                  {rule.conditions.map((condition, idx) => (
                                    <div
                                      key={idx}
                                      className="bg-gray-50 border border-gray-200 rounded-lg p-3 text-sm"
                                    >
                                      <div className="flex flex-wrap items-center gap-2 mb-1">
                                        <span className="font-medium text-gray-800">{condition.signal}</span>
                                        {condition.required && (
                                          <span className="text-xs px-1.5 py-0.5 rounded bg-red-100 text-red-700 font-medium">
                                            Required
                                          </span>
                                        )}
                                      </div>
                                      <div className="text-gray-500 space-y-0.5">
                                        <p>
                                          <span className="text-gray-600 font-medium">Source:</span>{" "}
                                          {condition.source}
                                          {condition.eventType && (
                                            <span>
                                              {" "}
                                              | <span className="text-gray-600 font-medium">Event Type:</span>{" "}
                                              {condition.eventType}
                                            </span>
                                          )}
                                          {condition.dataField && (
                                            <span>
                                              {" "}
                                              | <span className="text-gray-600 font-medium">Field:</span>{" "}
                                              <code className="bg-gray-200 px-1 rounded text-xs">{condition.dataField}</code>
                                            </span>
                                          )}
                                        </p>
                                        <p>
                                          <span className="text-gray-600 font-medium">Operator:</span>{" "}
                                          <code className="bg-gray-200 px-1 rounded text-xs">{condition.operator}</code>{" "}
                                          <span className="text-gray-600 font-medium">Value:</span>{" "}
                                          <code className="bg-gray-200 px-1 rounded text-xs">{condition.value}</code>
                                        </p>
                                      </div>
                                    </div>
                                  ))}
                                </div>
                              </div>
                            )}

                            {/* Confidence Scoring */}
                            <div>
                              <h4 className="text-sm font-semibold text-gray-700 mb-2">Confidence Scoring</h4>
                              <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 space-y-3">
                                <div className="flex items-center space-x-6 text-sm">
                                  <span>
                                    <span className="text-gray-600 font-medium">Base Confidence:</span>{" "}
                                    <span className="font-semibold text-gray-900">{rule.baseConfidence}%</span>
                                  </span>
                                  <span>
                                    <span className="text-gray-600 font-medium">Threshold:</span>{" "}
                                    <span className="font-semibold text-gray-900">{rule.confidenceThreshold}%</span>
                                  </span>
                                </div>
                                {rule.confidenceFactors.length > 0 && (
                                  <div>
                                    <p className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-2">
                                      Confidence Factors
                                    </p>
                                    <div className="space-y-1">
                                      {rule.confidenceFactors.map((factor, idx) => (
                                        <div
                                          key={idx}
                                          className="flex items-center justify-between text-sm bg-white border border-gray-100 rounded px-3 py-1.5"
                                        >
                                          <div>
                                            <span className="font-medium text-gray-700">{factor.signal}</span>
                                            <span className="text-gray-400 mx-2">-</span>
                                            <span className="text-gray-500">{factor.condition}</span>
                                          </div>
                                          <span
                                            className={`font-semibold ${
                                              factor.weight > 0 ? "text-green-600" : "text-red-600"
                                            }`}
                                          >
                                            {factor.weight > 0 ? "+" : ""}
                                            {factor.weight}%
                                          </span>
                                        </div>
                                      ))}
                                    </div>
                                  </div>
                                )}
                              </div>
                            </div>

                            {/* Explanation */}
                            {rule.explanation && (
                              <div>
                                <h4 className="text-sm font-semibold text-gray-700 mb-1">Explanation</h4>
                                <p className="text-sm text-gray-600 leading-relaxed bg-blue-50 border border-blue-200 rounded-lg p-3">
                                  {rule.explanation}
                                </p>
                              </div>
                            )}

                            {/* Remediation Steps */}
                            {rule.remediation.length > 0 && (
                              <div>
                                <h4 className="text-sm font-semibold text-gray-700 mb-2">Remediation Steps</h4>
                                <div className="space-y-3">
                                  {rule.remediation.map((rem, idx) => (
                                    <div
                                      key={idx}
                                      className="bg-green-50 border border-green-200 rounded-lg p-4"
                                    >
                                      <h5 className="text-sm font-semibold text-green-800 mb-2">
                                        {rem.title}
                                      </h5>
                                      <ol className="list-decimal list-inside space-y-1">
                                        {rem.steps.map((step, sIdx) => (
                                          <li key={sIdx} className="text-sm text-green-700">
                                            {step}
                                          </li>
                                        ))}
                                      </ol>
                                    </div>
                                  ))}
                                </div>
                              </div>
                            )}

                            {/* Related Docs */}
                            {rule.relatedDocs.length > 0 && (
                              <div>
                                <h4 className="text-sm font-semibold text-gray-700 mb-2">Related Documentation</h4>
                                <div className="flex flex-wrap gap-2">
                                  {rule.relatedDocs.map((doc, idx) => (
                                    <a
                                      key={idx}
                                      href={doc.url}
                                      target="_blank"
                                      rel="noopener noreferrer"
                                      className="inline-flex items-center space-x-1.5 px-3 py-1.5 bg-indigo-50 border border-indigo-200 rounded-lg text-sm text-indigo-700 hover:bg-indigo-100 transition-colors"
                                    >
                                      <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" />
                                      </svg>
                                      <span>{doc.title}</span>
                                    </a>
                                  ))}
                                </div>
                              </div>
                            )}

                            {/* Actions */}
                            <div className="flex items-center justify-end space-x-3 pt-4 border-t border-gray-200">
                              {!rule.isBuiltIn && (
                                <button
                                  onClick={() => handleDeleteRule(rule)}
                                  disabled={deletingRuleId === rule.ruleId}
                                  className="px-4 py-2 text-sm bg-red-600 text-white rounded-lg hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
                                >
                                  {deletingRuleId === rule.ruleId ? (
                                    <>
                                      <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                                      <span>Deleting...</span>
                                    </>
                                  ) : (
                                    <>
                                      <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                                      </svg>
                                      <span>Delete Rule</span>
                                    </>
                                  )}
                                </button>
                              )}
                            </div>
                          </div>
                        )}
                      </div>
                    );
                  })}
                </div>
              )}
            </div>
          )}
        </main>
      </div>
    </ProtectedRoute>
  );
}
