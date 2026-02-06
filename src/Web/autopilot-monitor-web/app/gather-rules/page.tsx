"use client";

import { useEffect, useState, useCallback } from "react";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { API_BASE_URL } from "@/lib/config";

interface GatherRule {
  ruleId: string;
  title: string;
  description: string;
  category: string;
  version: string;
  author: string;
  enabled: boolean;
  isBuiltIn: boolean;
  isCommunity: boolean;
  collectorType: string;
  target: string;
  parameters: Record<string, string>;
  trigger: string;
  intervalSeconds: number | null;
  triggerPhase: string | null;
  triggerEventType: string | null;
  outputEventType: string;
  outputSeverity: string;
  tags: string[];
  createdAt: string;
  updatedAt: string;
}

interface NewRuleForm {
  ruleId: string;
  title: string;
  description: string;
  category: string;
  collectorType: string;
  target: string;
  trigger: string;
  intervalSeconds: number;
  triggerPhase: string;
  triggerEventType: string;
  outputEventType: string;
  outputSeverity: string;
}

const CATEGORIES = ["network", "identity", "apps", "device", "esp", "enrollment"] as const;
const COLLECTOR_TYPES = ["registry", "eventlog", "wmi", "file", "command"] as const;
const TRIGGERS = ["startup", "phase_change", "interval", "on_event"] as const;
const SEVERITIES = ["info", "warning", "error", "critical"] as const;

const CATEGORY_COLORS: Record<string, { bg: string; text: string }> = {
  network: { bg: "bg-blue-100", text: "text-blue-700" },
  identity: { bg: "bg-purple-100", text: "text-purple-700" },
  apps: { bg: "bg-orange-100", text: "text-orange-700" },
  device: { bg: "bg-gray-100", text: "text-gray-700" },
  esp: { bg: "bg-teal-100", text: "text-teal-700" },
  enrollment: { bg: "bg-indigo-100", text: "text-indigo-700" },
};

const EMPTY_FORM: NewRuleForm = {
  ruleId: "",
  title: "",
  description: "",
  category: "device",
  collectorType: "registry",
  target: "",
  trigger: "startup",
  intervalSeconds: 60,
  triggerPhase: "",
  triggerEventType: "",
  outputEventType: "",
  outputSeverity: "info",
};

export default function GatherRulesPage() {
  const router = useRouter();
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();

  const [rules, setRules] = useState<GatherRule[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  // Filter state
  const [searchQuery, setSearchQuery] = useState("");
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [typeFilter, setTypeFilter] = useState("all");

  // Create form state
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [newRule, setNewRule] = useState<NewRuleForm>({ ...EMPTY_FORM });
  const [creating, setCreating] = useState(false);

  // Toggling / deleting state
  const [togglingRule, setTogglingRule] = useState<string | null>(null);
  const [deletingRule, setDeletingRule] = useState<string | null>(null);

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
    if (!tenantId) return;

    try {
      setLoading(true);
      setError(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error("Failed to get access token");
      }

      const response = await fetch(`${API_BASE_URL}/api/gather-rules`, {
        headers: {
          Authorization: `Bearer ${token}`,
        },
      });

      if (!response.ok) {
        throw new Error(`Failed to load gather rules: ${response.statusText}`);
      }

      const data = await response.json();
      if (data.success && Array.isArray(data.rules)) {
        setRules(data.rules);
      } else {
        throw new Error("Unexpected response format");
      }
    } catch (err) {
      console.error("Error fetching gather rules:", err);
      setError(err instanceof Error ? err.message : "Failed to load gather rules");
    } finally {
      setLoading(false);
    }
  }, [tenantId, getAccessToken]);

  useEffect(() => {
    fetchRules();
  }, [fetchRules]);

  // Toggle rule enabled/disabled
  const handleToggleRule = async (rule: GatherRule) => {
    try {
      setTogglingRule(rule.ruleId);
      setError(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error("Failed to get access token");
      }

      const response = await fetch(`${API_BASE_URL}/api/gather-rules/${encodeURIComponent(rule.ruleId)}`, {
        method: "PUT",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${token}`,
        },
        body: JSON.stringify({ enabled: !rule.enabled }),
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.error || `Failed to update rule: ${response.statusText}`);
      }

      setRules((prev) =>
        prev.map((r) => (r.ruleId === rule.ruleId ? { ...r, enabled: !r.enabled } : r))
      );
      showSuccess(`Rule "${rule.title}" ${rule.enabled ? "disabled" : "enabled"} successfully!`);
    } catch (err) {
      console.error("Error toggling rule:", err);
      showError(err instanceof Error ? err.message : "Failed to update rule");
    } finally {
      setTogglingRule(null);
    }
  };

  // Delete custom rule
  const handleDeleteRule = async (rule: GatherRule) => {
    if (!confirm(`Are you sure you want to delete the rule "${rule.title}"? This action cannot be undone.`)) {
      return;
    }

    try {
      setDeletingRule(rule.ruleId);
      setError(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error("Failed to get access token");
      }

      const response = await fetch(`${API_BASE_URL}/api/gather-rules/${encodeURIComponent(rule.ruleId)}`, {
        method: "DELETE",
        headers: {
          Authorization: `Bearer ${token}`,
        },
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.error || `Failed to delete rule: ${response.statusText}`);
      }

      setRules((prev) => prev.filter((r) => r.ruleId !== rule.ruleId));
      showSuccess(`Rule "${rule.title}" deleted successfully!`);
    } catch (err) {
      console.error("Error deleting rule:", err);
      showError(err instanceof Error ? err.message : "Failed to delete rule");
    } finally {
      setDeletingRule(null);
    }
  };

  // Create custom rule
  const handleCreateRule = async () => {
    if (!newRule.ruleId.trim() || !newRule.title.trim() || !newRule.target.trim() || !newRule.outputEventType.trim()) {
      showError("Rule ID, Title, Target, and Output Event Type are required.");
      return;
    }

    try {
      setCreating(true);
      setError(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error("Failed to get access token");
      }

      const payload: Record<string, unknown> = {
        ruleId: newRule.ruleId.trim(),
        title: newRule.title.trim(),
        description: newRule.description.trim(),
        category: newRule.category,
        collectorType: newRule.collectorType,
        target: newRule.target.trim(),
        trigger: newRule.trigger,
        outputEventType: newRule.outputEventType.trim(),
        outputSeverity: newRule.outputSeverity,
      };

      if (newRule.trigger === "interval") {
        payload.intervalSeconds = newRule.intervalSeconds;
      }
      if (newRule.trigger === "phase_change") {
        payload.triggerPhase = newRule.triggerPhase.trim();
      }
      if (newRule.trigger === "on_event") {
        payload.triggerEventType = newRule.triggerEventType.trim();
      }

      const response = await fetch(`${API_BASE_URL}/api/gather-rules`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${token}`,
        },
        body: JSON.stringify(payload),
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.error || `Failed to create rule: ${response.statusText}`);
      }

      showSuccess(`Rule "${newRule.title}" created successfully!`);
      setNewRule({ ...EMPTY_FORM });
      setShowCreateForm(false);
      await fetchRules();
    } catch (err) {
      console.error("Error creating rule:", err);
      showError(err instanceof Error ? err.message : "Failed to create rule");
    } finally {
      setCreating(false);
    }
  };

  // Filtered rules
  const filteredRules = rules.filter((rule) => {
    const matchesSearch =
      searchQuery === "" ||
      rule.title.toLowerCase().includes(searchQuery.toLowerCase()) ||
      rule.ruleId.toLowerCase().includes(searchQuery.toLowerCase());

    const matchesCategory = categoryFilter === "all" || rule.category === categoryFilter;

    const matchesType =
      typeFilter === "all" ||
      (typeFilter === "builtin" && rule.isBuiltIn) ||
      (typeFilter === "custom" && !rule.isBuiltIn);

    return matchesSearch && matchesCategory && matchesType;
  });

  // Summary stats
  const totalRules = rules.length;
  const activeRules = rules.filter((r) => r.enabled).length;
  const builtInCount = rules.filter((r) => r.isBuiltIn).length;
  const customCount = rules.filter((r) => !r.isBuiltIn).length;

  const getCategoryBadge = (category: string) => {
    const colors = CATEGORY_COLORS[category] || { bg: "bg-gray-100", text: "text-gray-700" };
    return (
      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${colors.bg} ${colors.text}`}>
        {category}
      </span>
    );
  };

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
                <h1 className="text-2xl font-bold text-gray-900">Gather Rules</h1>
                <p className="text-sm text-gray-500">Manage data collection rules for device enrollment</p>
              </div>
            </div>
          </div>
        </header>

        {/* Main Content */}
        <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          {loading ? (
            <div className="bg-white rounded-lg shadow p-8 text-center">
              <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-indigo-600 mx-auto"></div>
              <p className="mt-4 text-gray-600">Loading gather rules...</p>
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
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
                <div className="bg-white rounded-lg shadow p-4">
                  <div className="text-sm font-medium text-gray-500">Total Rules</div>
                  <div className="mt-1 text-2xl font-bold text-gray-900">{totalRules}</div>
                </div>
                <div className="bg-white rounded-lg shadow p-4">
                  <div className="text-sm font-medium text-gray-500">Active Rules</div>
                  <div className="mt-1 text-2xl font-bold text-emerald-600">{activeRules}</div>
                </div>
                <div className="bg-white rounded-lg shadow p-4">
                  <div className="text-sm font-medium text-gray-500">Built-in</div>
                  <div className="mt-1 text-2xl font-bold text-blue-600">{builtInCount}</div>
                </div>
                <div className="bg-white rounded-lg shadow p-4">
                  <div className="text-sm font-medium text-gray-500">Custom</div>
                  <div className="mt-1 text-2xl font-bold text-purple-600">{customCount}</div>
                </div>
              </div>

              {/* Filter Bar + Create Button */}
              <div className="bg-white rounded-lg shadow p-4">
                <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
                  <div className="flex flex-col sm:flex-row gap-3 flex-1">
                    {/* Search */}
                    <div className="relative flex-1 max-w-md">
                      <input
                        type="text"
                        value={searchQuery}
                        onChange={(e) => setSearchQuery(e.target.value)}
                        placeholder="Search by title or rule ID..."
                        autoComplete="off"
                        className="w-full px-4 py-2 pl-10 pr-10 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                      />
                      <svg className="absolute left-3 top-2.5 w-5 h-5 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                      </svg>
                      {searchQuery && (
                        <button
                          onClick={() => setSearchQuery("")}
                          className="absolute right-3 top-2.5 text-gray-400 hover:text-gray-600 transition-colors"
                          title="Clear search"
                        >
                          <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                          </svg>
                        </button>
                      )}
                    </div>

                    {/* Category Filter */}
                    <select
                      value={categoryFilter}
                      onChange={(e) => setCategoryFilter(e.target.value)}
                      className="px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                    >
                      <option value="all">All Categories</option>
                      <option value="network">Network</option>
                      <option value="identity">Identity</option>
                      <option value="apps">Apps</option>
                      <option value="device">Device</option>
                      <option value="esp">ESP</option>
                      <option value="enrollment">Enrollment</option>
                    </select>

                    {/* Type Filter */}
                    <select
                      value={typeFilter}
                      onChange={(e) => setTypeFilter(e.target.value)}
                      className="px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                    >
                      <option value="all">All Types</option>
                      <option value="builtin">Built-in</option>
                      <option value="custom">Custom</option>
                    </select>
                  </div>

                  {/* Create Button */}
                  <button
                    onClick={() => {
                      setShowCreateForm(!showCreateForm);
                      if (showCreateForm) {
                        setNewRule({ ...EMPTY_FORM });
                      }
                    }}
                    className="px-4 py-2 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 transition-colors flex items-center space-x-2 text-sm font-medium whitespace-nowrap"
                  >
                    {showCreateForm ? (
                      <>
                        <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                        </svg>
                        <span>Cancel</span>
                      </>
                    ) : (
                      <>
                        <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
                        </svg>
                        <span>Create Custom Rule</span>
                      </>
                    )}
                  </button>
                </div>
              </div>

              {/* Create Custom Rule Form */}
              {showCreateForm && (
                <div className="bg-white rounded-lg shadow">
                  <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-indigo-50 to-purple-50">
                    <div className="flex items-center space-x-2">
                      <svg className="w-6 h-6 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
                      </svg>
                      <div>
                        <h2 className="text-xl font-semibold text-gray-900">Create Custom Rule</h2>
                        <p className="text-sm text-gray-500 mt-1">Define a new data collection rule for enrolled devices</p>
                      </div>
                    </div>
                  </div>
                  <div className="p-6 space-y-5">
                    {/* Row 1: Rule ID, Title */}
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">
                          Rule ID <span className="text-red-500">*</span>
                        </label>
                        <input
                          type="text"
                          value={newRule.ruleId}
                          onChange={(e) => setNewRule({ ...newRule, ruleId: e.target.value })}
                          placeholder="e.g., custom-network-check"
                          autoComplete="off"
                          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                        />
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">
                          Title <span className="text-red-500">*</span>
                        </label>
                        <input
                          type="text"
                          value={newRule.title}
                          onChange={(e) => setNewRule({ ...newRule, title: e.target.value })}
                          placeholder="e.g., Custom Network Check"
                          autoComplete="off"
                          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                        />
                      </div>
                    </div>

                    {/* Description */}
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-1">Description</label>
                      <textarea
                        value={newRule.description}
                        onChange={(e) => setNewRule({ ...newRule, description: e.target.value })}
                        placeholder="Describe what this rule collects and why..."
                        rows={2}
                        className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors resize-none"
                      />
                    </div>

                    {/* Row 2: Category, Collector Type */}
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">Category</label>
                        <select
                          value={newRule.category}
                          onChange={(e) => setNewRule({ ...newRule, category: e.target.value })}
                          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                        >
                          {CATEGORIES.map((cat) => (
                            <option key={cat} value={cat}>
                              {cat.charAt(0).toUpperCase() + cat.slice(1)}
                            </option>
                          ))}
                        </select>
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">Collector Type</label>
                        <select
                          value={newRule.collectorType}
                          onChange={(e) => setNewRule({ ...newRule, collectorType: e.target.value })}
                          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                        >
                          {COLLECTOR_TYPES.map((ct) => (
                            <option key={ct} value={ct}>
                              {ct.charAt(0).toUpperCase() + ct.slice(1)}
                            </option>
                          ))}
                        </select>
                      </div>
                    </div>

                    {/* Target */}
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-1">
                        Target <span className="text-red-500">*</span>
                      </label>
                      <input
                        type="text"
                        value={newRule.target}
                        onChange={(e) => setNewRule({ ...newRule, target: e.target.value })}
                        placeholder="e.g., HKLM\SOFTWARE\Microsoft\... or Win32_OperatingSystem"
                        autoComplete="off"
                        className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                      />
                      <p className="text-xs text-gray-400 mt-1">Registry path, WMI class, event log name, file path, or command depending on collector type</p>
                    </div>

                    {/* Trigger */}
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-1">Trigger</label>
                      <select
                        value={newRule.trigger}
                        onChange={(e) => setNewRule({ ...newRule, trigger: e.target.value })}
                        className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                      >
                        {TRIGGERS.map((t) => (
                          <option key={t} value={t}>
                            {t === "phase_change"
                              ? "Phase Change"
                              : t === "on_event"
                              ? "On Event"
                              : t.charAt(0).toUpperCase() + t.slice(1)}
                          </option>
                        ))}
                      </select>
                    </div>

                    {/* Conditional Trigger Fields */}
                    {newRule.trigger === "interval" && (
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">Interval (seconds)</label>
                        <input
                          type="number"
                          min={5}
                          max={3600}
                          value={newRule.intervalSeconds}
                          onChange={(e) => setNewRule({ ...newRule, intervalSeconds: parseInt(e.target.value) || 60 })}
                          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                        />
                        <p className="text-xs text-gray-400 mt-1">How often to run this rule (5 - 3600 seconds)</p>
                      </div>
                    )}

                    {newRule.trigger === "phase_change" && (
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">Trigger Phase</label>
                        <input
                          type="text"
                          value={newRule.triggerPhase}
                          onChange={(e) => setNewRule({ ...newRule, triggerPhase: e.target.value })}
                          placeholder="e.g., DeviceESP, AccountESP, DevicePreparation"
                          autoComplete="off"
                          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                        />
                      </div>
                    )}

                    {newRule.trigger === "on_event" && (
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">Trigger Event Type</label>
                        <input
                          type="text"
                          value={newRule.triggerEventType}
                          onChange={(e) => setNewRule({ ...newRule, triggerEventType: e.target.value })}
                          placeholder="e.g., NetworkChange, PolicyApplied"
                          autoComplete="off"
                          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                        />
                      </div>
                    )}

                    {/* Row 3: Output Event Type, Output Severity */}
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">
                          Output Event Type <span className="text-red-500">*</span>
                        </label>
                        <input
                          type="text"
                          value={newRule.outputEventType}
                          onChange={(e) => setNewRule({ ...newRule, outputEventType: e.target.value })}
                          placeholder="e.g., CustomNetworkStatus"
                          autoComplete="off"
                          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                        />
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">Output Severity</label>
                        <select
                          value={newRule.outputSeverity}
                          onChange={(e) => setNewRule({ ...newRule, outputSeverity: e.target.value })}
                          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                        >
                          {SEVERITIES.map((s) => (
                            <option key={s} value={s}>
                              {s.charAt(0).toUpperCase() + s.slice(1)}
                            </option>
                          ))}
                        </select>
                      </div>
                    </div>

                    {/* Action Buttons */}
                    <div className="flex items-center justify-end space-x-3 pt-4 border-t border-gray-200">
                      <button
                        onClick={() => {
                          setShowCreateForm(false);
                          setNewRule({ ...EMPTY_FORM });
                        }}
                        disabled={creating}
                        className="px-6 py-2 border border-gray-300 rounded-lg text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium"
                      >
                        Cancel
                      </button>
                      <button
                        onClick={handleCreateRule}
                        disabled={creating}
                        className="px-6 py-2 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2 text-sm font-medium"
                      >
                        {creating ? (
                          <>
                            <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                            <span>Creating...</span>
                          </>
                        ) : (
                          <span>Save Rule</span>
                        )}
                      </button>
                    </div>
                  </div>
                </div>
              )}

              {/* Rules List */}
              <div className="bg-white rounded-lg shadow">
                <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-blue-50 to-indigo-50">
                  <div className="flex items-center space-x-2">
                    <svg className="w-6 h-6 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" />
                    </svg>
                    <div>
                      <h2 className="text-xl font-semibold text-gray-900">Rules</h2>
                      <p className="text-sm text-gray-500 mt-1">
                        {filteredRules.length} rule{filteredRules.length !== 1 ? "s" : ""} found
                        {(searchQuery || categoryFilter !== "all" || typeFilter !== "all") && " (filtered)"}
                      </p>
                    </div>
                  </div>
                </div>

                {filteredRules.length === 0 ? (
                  <div className="p-8 text-center">
                    <svg className="w-12 h-12 text-gray-300 mx-auto mb-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" />
                    </svg>
                    <p className="text-gray-500">No rules match your filters.</p>
                    {(searchQuery || categoryFilter !== "all" || typeFilter !== "all") && (
                      <button
                        onClick={() => {
                          setSearchQuery("");
                          setCategoryFilter("all");
                          setTypeFilter("all");
                        }}
                        className="mt-2 text-sm text-indigo-600 hover:text-indigo-800 transition-colors"
                      >
                        Clear all filters
                      </button>
                    )}
                  </div>
                ) : (
                  <div className="divide-y divide-gray-200">
                    {filteredRules.map((rule) => (
                      <div
                        key={rule.ruleId}
                        className={`p-4 hover:bg-gray-50 transition-colors ${!rule.enabled ? "opacity-60" : ""}`}
                      >
                        <div className="flex items-start justify-between gap-4">
                          {/* Left: Toggle + Info */}
                          <div className="flex items-start space-x-4 flex-1 min-w-0">
                            {/* Toggle */}
                            <div className="pt-1 flex-shrink-0">
                              <button
                                onClick={() => handleToggleRule(rule)}
                                disabled={togglingRule === rule.ruleId}
                                className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${
                                  rule.enabled ? "bg-emerald-500" : "bg-gray-300"
                                }`}
                                title={rule.enabled ? "Disable rule" : "Enable rule"}
                              >
                                <span
                                  className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                                    rule.enabled ? "translate-x-6" : "translate-x-1"
                                  }`}
                                />
                              </button>
                            </div>

                            {/* Info */}
                            <div className="flex-1 min-w-0">
                              <div className="flex items-center flex-wrap gap-2">
                                <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-mono font-medium bg-gray-100 text-gray-600 border border-gray-200">
                                  {rule.ruleId}
                                </span>
                                {getCategoryBadge(rule.category)}
                                {rule.isBuiltIn && (
                                  <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-50 text-blue-600 border border-blue-200">
                                    Built-in
                                  </span>
                                )}
                                {rule.isCommunity && (
                                  <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-amber-50 text-amber-600 border border-amber-200">
                                    Community
                                  </span>
                                )}
                              </div>
                              <h3 className="mt-1 text-sm font-semibold text-gray-900 truncate">{rule.title}</h3>
                              {rule.description && (
                                <p className="mt-0.5 text-sm text-gray-500 line-clamp-2">{rule.description}</p>
                              )}
                              <div className="mt-2 flex items-center flex-wrap gap-x-4 gap-y-1 text-xs text-gray-500">
                                <span className="flex items-center space-x-1">
                                  <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 7v10c0 2.21 3.582 4 8 4s8-1.79 8-4V7M4 7c0 2.21 3.582 4 8 4s8-1.79 8-4M4 7c0-2.21 3.582-4 8-4s8 1.79 8 4" />
                                  </svg>
                                  <span>{rule.collectorType}</span>
                                </span>
                                <span className="flex items-center space-x-1">
                                  <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
                                  </svg>
                                  <span>
                                    {rule.trigger === "phase_change"
                                      ? "Phase Change"
                                      : rule.trigger === "on_event"
                                      ? "On Event"
                                      : rule.trigger.charAt(0).toUpperCase() + rule.trigger.slice(1)}
                                    {rule.trigger === "interval" && rule.intervalSeconds
                                      ? ` (${rule.intervalSeconds}s)`
                                      : ""}
                                  </span>
                                </span>
                                <span className="flex items-center space-x-1">
                                  <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z" />
                                  </svg>
                                  <span>{rule.author}</span>
                                </span>
                              </div>
                            </div>
                          </div>

                          {/* Right: Delete button for custom rules */}
                          {!rule.isBuiltIn && (
                            <div className="flex-shrink-0">
                              <button
                                onClick={() => handleDeleteRule(rule)}
                                disabled={deletingRule === rule.ruleId}
                                className="px-3 py-1.5 text-sm bg-red-600 text-white rounded-lg hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-1"
                                title="Delete custom rule"
                              >
                                {deletingRule === rule.ruleId ? (
                                  <>
                                    <div className="animate-spin rounded-full h-3.5 w-3.5 border-b-2 border-white"></div>
                                    <span>Deleting...</span>
                                  </>
                                ) : (
                                  <>
                                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                                    </svg>
                                    <span>Delete</span>
                                  </>
                                )}
                              </button>
                            </div>
                          )}
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            </div>
          )}
        </main>
      </div>
    </ProtectedRoute>
  );
}
