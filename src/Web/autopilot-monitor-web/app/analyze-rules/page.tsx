"use client";

import { useEffect, useState, useCallback } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { API_BASE_URL } from "@/lib/config";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import { downloadAsJson, stripInternalFields, bumpVersion } from "@/lib/rulePageHelpers";
import { StatCard } from "@/components/rules/StatCard";
import { RuleFilterBar } from "@/components/rules/RuleFilterBar";
import { EmptyState } from "@/components/rules/EmptyState";
import { FormJsonToggle, JsonModeToggleButtons } from "@/components/rules/FormJsonToggle";
import { useAuthenticatedFetch, useNotificationMessages } from "@/hooks";

import { AnalyzeRule, RuleForm, EMPTY_FORM, EMPTY_CONDITION, ruleToForm } from "./types";
import AnalyzeRuleFormFields from "./components/AnalyzeRuleFormFields";
import AnalyzeRuleCard from "./components/AnalyzeRuleCard";

interface TenantInfo {
  tenantId: string;
  domainName: string;
}

export default function AnalyzeRulesPage() {
  const router = useRouter();
  const { tenantId } = useTenant();
  const { user, getAccessToken } = useAuth();

  const { successMessage, error, showSuccess, showError } = useNotificationMessages();

  const { data: rules, loading, execute: fetchRulesExec, setData: setRules } = useAuthenticatedFetch<AnalyzeRule[]>({
    onError: (err) => showError(err.message),
    onTokenExpired: (err) => showError(err.message),
  });

  const { execute: mutate } = useAuthenticatedFetch({
    onError: (err) => showError(err.message),
    onTokenExpired: (err) => showError(err.message),
  });

  // Filter state
  const [searchQuery, setSearchQuery] = useState("");
  const [severityFilter, setSeverityFilter] = useState("all");
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [typeFilter, setTypeFilter] = useState("all");

  // Expanded / editing state
  const [expandedRuleId, setExpandedRuleId] = useState<string | null>(null);
  const [editingRuleId, setEditingRuleId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<RuleForm>({ ...EMPTY_FORM });
  const [saving, setSaving] = useState(false);

  // Create form state
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [newRule, setNewRule] = useState<RuleForm>({ ...EMPTY_FORM });
  const [creating, setCreating] = useState(false);

  // JSON mode (create + edit)
  const [jsonModeCreate, setJsonModeCreate] = useState(false);
  const [jsonModeEdit, setJsonModeEdit] = useState(false);
  const [jsonText, setJsonText] = useState("");
  const [jsonError, setJsonError] = useState<string | null>(null);

  // Toggling / deleting state
  const [togglingRuleId, setTogglingRuleId] = useState<string | null>(null);
  const [deletingRuleId, setDeletingRuleId] = useState<string | null>(null);

  // Galactic admin mode
  const [galacticAdminMode] = useState(() => {
    if (typeof window !== 'undefined') {
      return localStorage.getItem('galacticAdminMode') === 'true';
    }
    return false;
  });
  const [tenants, setTenants] = useState<TenantInfo[]>([]);
  const [selectedTenantId, setSelectedTenantId] = useState<string>('');

  useEffect(() => {
    if (!galacticAdminMode || !user?.isGalacticAdmin) return;
    const fetchTenants = async () => {
      try {
        const response = await authenticatedFetch(`${API_BASE_URL}/api/config/all`, getAccessToken);
        if (response.ok) {
          const data = await response.json();
          const mapped: TenantInfo[] = data.map((t: { tenantId: string; domainName: string }) => ({
            tenantId: t.tenantId,
            domainName: t.domainName || '',
          }));
          mapped.sort((a, b) => {
            const nameA = a.domainName || a.tenantId;
            const nameB = b.domainName || b.tenantId;
            return nameA.localeCompare(nameB);
          });
          setTenants(mapped);
        }
      } catch (err) {
        console.error('Error fetching tenant list:', err);
      }
    };
    fetchTenants();
  }, [galacticAdminMode, user?.isGalacticAdmin, getAccessToken]);

  useEffect(() => {
    if (tenantId && !selectedTenantId) {
      setSelectedTenantId(tenantId);
    }
  }, [tenantId]);

  const isGalacticOverride = galacticAdminMode && user?.isGalacticAdmin && selectedTenantId && selectedTenantId !== tenantId;
  const effectiveTenantId = (galacticAdminMode && user?.isGalacticAdmin && selectedTenantId) ? selectedTenantId : tenantId;
  const isReadOnly = !user?.isTenantAdmin && !user?.isGalacticAdmin;

  const fetchRules = useCallback(async () => {
    if (!effectiveTenantId) return;
    const url = isGalacticOverride
      ? `${API_BASE_URL}/api/galactic/rules/analyze?tenantId=${effectiveTenantId}`
      : `${API_BASE_URL}/api/rules/analyze`;
    await fetchRulesExec(
      url,
      undefined,
      { transform: (d) => { const r = d as { success?: boolean; rules?: AnalyzeRule[] }; return r.success && Array.isArray(r.rules) ? r.rules : []; } }
    );
  }, [effectiveTenantId, isGalacticOverride, fetchRulesExec]);

  useEffect(() => {
    fetchRules();
  }, [fetchRules]);

  // Toggle rule enabled/disabled
  const handleToggleRule = async (rule: AnalyzeRule) => {
    setTogglingRuleId(rule.ruleId);
    const result = await mutate(
      `${API_BASE_URL}/api/rules/analyze/${encodeURIComponent(rule.ruleId)}`,
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...rule, enabled: !rule.enabled }),
      }
    );
    if (result !== null) {
      setRules((prev) =>
        (prev || []).map((r) => (r.ruleId === rule.ruleId ? { ...r, enabled: !r.enabled } : r))
      );
      showSuccess(`Rule "${rule.title}" ${!rule.enabled ? "enabled" : "disabled"} successfully!`);
    }
    setTogglingRuleId(null);
  };

  // Delete custom rule
  const handleDeleteRule = async (rule: AnalyzeRule) => {
    if (!confirm(`Are you sure you want to delete the rule "${rule.title}"? This action cannot be undone.`)) {
      return;
    }

    setDeletingRuleId(rule.ruleId);
    const result = await mutate(
      `${API_BASE_URL}/api/rules/analyze/${encodeURIComponent(rule.ruleId)}`,
      { method: "DELETE" }
    );
    if (result !== null) {
      setRules((prev) => (prev || []).filter((r) => r.ruleId !== rule.ruleId));
      if (expandedRuleId === rule.ruleId) setExpandedRuleId(null);
      showSuccess(`Rule "${rule.title}" deleted successfully!`);
    }
    setDeletingRuleId(null);
  };

  // Create custom rule
  const handleCreateRule = async (formOverride?: RuleForm) => {
    const form = formOverride ?? newRule;
    if (!form.ruleId.trim() || !form.title.trim()) {
      showError("Rule ID and Title are required.");
      return;
    }

    setCreating(true);
    const payload = {
      ruleId: form.ruleId.trim(),
      title: form.title.trim(),
      description: form.description.trim(),
      severity: form.severity,
      category: form.category,
      trigger: form.trigger,
      explanation: form.explanation.trim(),
      baseConfidence: form.baseConfidence,
      confidenceThreshold: form.confidenceThreshold,
      conditions: form.conditions.filter(c => c.signal.trim()),
      confidenceFactors: form.confidenceFactors.filter(f => f.signal.trim()),
      remediation: form.remediation.filter(r => r.title.trim()),
      relatedDocs: form.relatedDocs.filter(d => d.title.trim() && d.url.trim()),
    };

    const result = await mutate(
      `${API_BASE_URL}/api/rules/analyze`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      }
    );
    if (result !== null) {
      showSuccess(`Rule "${form.title}" created successfully!`);
      setNewRule({ ...EMPTY_FORM, conditions: [{ ...EMPTY_CONDITION }] });
      setShowCreateForm(false);
      await fetchRules();
    }
    setCreating(false);
  };

  // Start editing
  const startEditing = (rule: AnalyzeRule) => {
    setEditingRuleId(rule.ruleId);
    setEditForm(ruleToForm(rule));
  };

  // Save edited rule
  const handleSaveEdit = async (rule: AnalyzeRule, formOverride?: RuleForm) => {
    const form = formOverride ?? editForm;
    if (!form.title.trim()) {
      showError("Title is required.");
      return;
    }

    setSaving(true);
    const payload = {
      ...rule,
      title: form.title.trim(),
      description: form.description.trim(),
      severity: form.severity,
      category: form.category,
      trigger: form.trigger,
      explanation: form.explanation.trim(),
      baseConfidence: form.baseConfidence,
      confidenceThreshold: form.confidenceThreshold,
      conditions: form.conditions.filter(c => c.signal.trim()),
      confidenceFactors: form.confidenceFactors.filter(f => f.signal.trim()),
      remediation: form.remediation.filter(r => r.title.trim()),
      relatedDocs: form.relatedDocs.filter(d => d.title.trim() && d.url.trim()),
      author: user?.displayName || user?.upn || rule.author,
      version: bumpVersion(rule.version),
    };

    const result = await mutate(
      `${API_BASE_URL}/api/rules/analyze/${encodeURIComponent(rule.ruleId)}`,
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      }
    );
    if (result !== null) {
      setEditingRuleId(null);
      showSuccess(`Rule "${editForm.title}" updated successfully!`);
      await fetchRules();
    }
    setSaving(false);
  };

  const rulesList = rules || [];

  // Filter rules
  const filteredRules = rulesList.filter((rule) => {
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
      (typeFilter === "builtin" && rule.isBuiltIn && !rule.isCommunity) ||
      (typeFilter === "community" && rule.isCommunity) ||
      (typeFilter === "custom" && !rule.isBuiltIn && !rule.isCommunity);

    return matchesSearch && matchesSeverity && matchesCategory && matchesType;
  });

  // Summary stats
  const totalRules = rulesList.length;
  const activeRules = rulesList.filter((r) => r.enabled).length;
  const criticalCount = rulesList.filter((r) => r.severity.toLowerCase() === "critical").length;
  const highCount = rulesList.filter((r) => r.severity.toLowerCase() === "high").length;
  const warningCount = rulesList.filter((r) => r.severity.toLowerCase() === "warning").length;
  const infoCount = rulesList.filter((r) => r.severity.toLowerCase() === "info").length;

  const uniqueCategories = Array.from(new Set(rulesList.map((r) => r.category.toLowerCase())));

  const handleExportSingle = (rule: AnalyzeRule) => {
    const cleaned = stripInternalFields(rule);
    downloadAsJson({ "$schema": "../schema/analyze-rule.schema.json", ...cleaned }, `${rule.ruleId}.json`);
  };

  const handleExportAll = () => {
    const cleaned = filteredRules.map(stripInternalFields);
    downloadAsJson(cleaned, "analyze-rules-export.json");
  };

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        {galacticAdminMode && user?.isGalacticAdmin && (
          <div className="bg-purple-700 text-white text-sm px-4 py-2 flex items-center justify-center space-x-2">
            <svg className="w-4 h-4 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3.055 11H5a2 2 0 012 2v1a2 2 0 002 2 2 2 0 012 2v2.945M8 3.935V5.5A2.5 2.5 0 0010.5 8h.5a2 2 0 012 2 2 2 0 104 0 2 2 0 012-2h1.064M15 20.488V18a2 2 0 012-2h3.064M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <span className="font-medium">Galactic Admin View</span>
            <span className="text-purple-300">&mdash; access to all tenants</span>
          </div>
        )}
        {/* Header */}
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <button onClick={() => router.push("/dashboard")} className="text-sm text-gray-600 hover:text-gray-900 mb-2 flex items-center">
                  &larr; Back to Dashboard
                </button>
                <div>
                  <div className="flex items-center gap-4">
                    <h1 className="text-3xl font-bold text-gray-900">Analyze Rules</h1>
                    <Link href="/docs/analyze-rules" className="text-sm text-indigo-600 hover:text-indigo-800 flex items-center gap-1 mt-1">
                      <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6.253v13m0-13C10.832 5.477 9.246 5 7.5 5S4.168 5.477 3 6.253v13C4.168 18.477 5.754 18 7.5 18s3.332.477 4.5 1.253m0-13C13.168 5.477 14.754 5 16.5 5c1.747 0 3.332.477 4.5 1.253v13C19.832 18.477 18.247 18 16.5 18c-1.746 0-3.332.477-4.5 1.253" />
                      </svg>
                      Documentation
                    </Link>
                  </div>
                  <p className="text-sm text-gray-600 mt-1">Manage event analysis rules for issue detection</p>
                </div>
              </div>
              {galacticAdminMode && user?.isGalacticAdmin && tenants.length > 0 && (
                <div className="flex items-center gap-3">
                  <label className="text-sm text-gray-500 hidden sm:inline">Tenant:</label>
                  <select
                    value={selectedTenantId}
                    onChange={(e) => setSelectedTenantId(e.target.value)}
                    className="text-sm border border-gray-300 rounded-md px-2 py-1.5 max-w-[220px] sm:max-w-xs"
                  >
                    {tenants.map((t) => (
                      <option key={t.tenantId} value={t.tenantId}>
                        {t.domainName
                          ? `${t.domainName} (${t.tenantId.substring(0, 8)}...)`
                          : t.tenantId}
                      </option>
                    ))}
                  </select>
                </div>
              )}
            </div>
          </div>
        </header>

        {/* Main Content */}
        <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          {/* Success Message */}
          {successMessage && (
            <div className="mb-6 bg-green-50 border border-green-200 rounded-lg p-4 flex items-center space-x-3">
              <svg className="w-5 h-5 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" /></svg>
              <span className="text-green-800 font-medium">{successMessage}</span>
            </div>
          )}

          {/* Error Message */}
          {error && (
            <div className="mb-6 bg-red-50 border border-red-200 rounded-lg p-4 flex items-center space-x-3">
              <svg className="w-5 h-5 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" /></svg>
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
                <StatCard label="Total" value={totalRules} />
                <StatCard label="Active" value={activeRules} valueColor="text-green-600" />
                <StatCard label="Critical" value={criticalCount} borderColor="border-red-400" valueColor="text-red-600" />
                <StatCard label="High" value={highCount} borderColor="border-orange-400" valueColor="text-orange-600" />
                <StatCard label="Warning" value={warningCount} borderColor="border-yellow-400" valueColor="text-yellow-600" />
                <StatCard label="Info" value={infoCount} borderColor="border-blue-400" valueColor="text-blue-600" />
              </div>

              {/* Filter Bar + Create Button */}
              <RuleFilterBar
                searchQuery={searchQuery}
                onSearchChange={setSearchQuery}
                searchPlaceholder="Search by title or rule ID..."
                filters={[
                  {
                    label: "Severity",
                    value: severityFilter,
                    onChange: setSeverityFilter,
                    options: [
                      { value: "all", label: "All Severities" },
                      { value: "critical", label: "Critical" },
                      { value: "high", label: "High" },
                      { value: "warning", label: "Warning" },
                      { value: "info", label: "Info" },
                    ],
                  },
                  {
                    label: "Category",
                    value: categoryFilter,
                    onChange: setCategoryFilter,
                    options: [
                      { value: "all", label: "All Categories" },
                      ...uniqueCategories.map((cat) => ({ value: cat, label: cat.charAt(0).toUpperCase() + cat.slice(1) })),
                    ],
                  },
                  {
                    label: "Type",
                    value: typeFilter,
                    onChange: setTypeFilter,
                    options: [
                      { value: "all", label: "All Types" },
                      { value: "builtin", label: "Built-in" },
                      { value: "community", label: "Community" },
                      { value: "custom", label: "Custom" },
                    ],
                  },
                ]}
                onExportAll={isReadOnly ? undefined : handleExportAll}
                onCreateNew={isReadOnly || isGalacticOverride ? undefined : () => { setShowCreateForm(!showCreateForm); if (showCreateForm) setNewRule({ ...EMPTY_FORM, conditions: [{ ...EMPTY_CONDITION }] }); }}
                createLabel="Create Custom Rule"
                showCreateForm={showCreateForm && !isGalacticOverride && !isReadOnly}
              />

              {/* Create Custom Rule Form */}
              {showCreateForm && (
                <div className="bg-white rounded-lg shadow">
                  <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-indigo-50 to-purple-50">
                    <div className="flex items-center justify-between">
                      <div className="flex items-center space-x-2">
                        <svg className="w-6 h-6 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6v6m0 0v6m0-6h6m-6 0H6" /></svg>
                        <div>
                          <h2 className="text-xl font-semibold text-gray-900">Create Custom Analyze Rule</h2>
                          <p className="text-sm text-gray-500 mt-1">Define conditions and confidence scoring for issue detection</p>
                        </div>
                      </div>
                      {/* JSON Mode Toggle */}
                      <JsonModeToggleButtons
                        jsonMode={jsonModeCreate}
                        onToggleMode={(mode) => {
                          if (mode) { setJsonText(JSON.stringify(newRule, null, 2)); }
                          setJsonModeCreate(mode);
                          setJsonError(null);
                        }}
                      />
                    </div>
                  </div>
                  <div className="p-6">
                    <FormJsonToggle
                      jsonMode={jsonModeCreate}
                      onToggleMode={(mode) => {
                        if (mode) { setJsonText(JSON.stringify(newRule, null, 2)); }
                        setJsonModeCreate(mode);
                        setJsonError(null);
                      }}
                      jsonText={jsonText}
                      onJsonTextChange={(text) => { setJsonText(text); setJsonError(null); }}
                      jsonError={jsonError}
                      onApplyJson={() => {
                        try {
                          const parsed = JSON.parse(jsonText) as RuleForm;
                          if (!parsed.ruleId && !parsed.title) throw new Error("JSON must include at least ruleId and title");
                          setNewRule({ ...EMPTY_FORM, ...parsed });
                          setJsonModeCreate(false);
                          setJsonError(null);
                        } catch (e) {
                          setJsonError(e instanceof Error ? e.message : "Invalid JSON");
                        }
                      }}
                      textareaRows={30}
                      description='Edit the rule as JSON. All fields are supported including <code class="bg-gray-100 px-1 rounded text-xs">event_correlation</code> condition properties.'
                    >
                      <AnalyzeRuleFormFields form={newRule} setForm={setNewRule} showRuleId={true} />
                    </FormJsonToggle>
                    <div className="flex items-center justify-end space-x-3 pt-4 mt-5 border-t border-gray-200">
                      <button onClick={() => { setShowCreateForm(false); setJsonModeCreate(false); setJsonError(null); setNewRule({ ...EMPTY_FORM, conditions: [{ ...EMPTY_CONDITION }] }); }} disabled={creating} className="px-6 py-2 border border-gray-300 rounded-lg text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium">Cancel</button>
                      <button onClick={() => {
                        if (jsonModeCreate) {
                          try {
                            const parsed = JSON.parse(jsonText) as RuleForm;
                            setJsonError(null);
                            handleCreateRule({ ...EMPTY_FORM, ...parsed });
                          } catch (e) {
                            setJsonError(e instanceof Error ? e.message : "Invalid JSON");
                          }
                        } else {
                          handleCreateRule();
                        }
                      }} disabled={creating} className="px-6 py-2 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2 text-sm font-medium">
                        {creating ? (<><div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div><span>Creating...</span></>) : (<span>Save Rule</span>)}
                      </button>
                    </div>
                  </div>
                </div>
              )}

              {/* Rules List */}
              {filteredRules.length === 0 ? (
                <EmptyState
                  message={rulesList.length === 0 ? "No analyze rules found." : "No rules match your current filters."}
                  onClearFilters={() => { setSearchQuery(""); setSeverityFilter("all"); setCategoryFilter("all"); setTypeFilter("all"); }}
                  showClearButton={!!(searchQuery || severityFilter !== "all" || categoryFilter !== "all" || typeFilter !== "all")}
                />
              ) : (
                <div className="space-y-3">
                  {filteredRules.map((rule) => (
                    <AnalyzeRuleCard
                      key={rule.ruleId}
                      rule={rule}
                      isExpanded={expandedRuleId === rule.ruleId}
                      isEditing={editingRuleId === rule.ruleId}
                      editForm={editForm}
                      setEditForm={setEditForm}
                      saving={saving}
                      togglingRuleId={togglingRuleId}
                      deletingRuleId={deletingRuleId}
                      jsonModeEdit={jsonModeEdit}
                      jsonText={jsonText}
                      jsonError={jsonError}
                      onToggle={() => {
                        if (editingRuleId === rule.ruleId) return;
                        setExpandedRuleId(expandedRuleId === rule.ruleId ? null : rule.ruleId);
                        if (expandedRuleId === rule.ruleId && editingRuleId === rule.ruleId) setEditingRuleId(null);
                      }}
                      onToggleEnabled={handleToggleRule}
                      onStartEditing={startEditing}
                      onSaveEdit={handleSaveEdit}
                      onCancelEdit={() => { setEditingRuleId(null); setJsonModeEdit(false); setJsonError(null); }}
                      onDelete={handleDeleteRule}
                      onExport={handleExportSingle}
                      onSetJsonModeEdit={setJsonModeEdit}
                      onSetJsonText={setJsonText}
                      onSetJsonError={setJsonError}
                      readOnly={isReadOnly}
                    />
                  ))}
                </div>
              )}
            </div>
          )}
        </main>
      </div>
    </ProtectedRoute>
  );
}
