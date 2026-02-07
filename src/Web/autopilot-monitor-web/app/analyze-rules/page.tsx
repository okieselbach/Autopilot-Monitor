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
  trigger: string;
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

interface RuleForm {
  ruleId: string;
  title: string;
  description: string;
  severity: string;
  category: string;
  trigger: string;
  explanation: string;
  baseConfidence: number;
  confidenceThreshold: number;
  conditions: RuleCondition[];
  confidenceFactors: ConfidenceFactor[];
  remediation: RemediationStep[];
  relatedDocs: RelatedDoc[];
}

const CATEGORIES = ["network", "identity", "apps", "device", "esp", "enrollment"] as const;
const SEVERITIES = ["info", "warning", "high", "critical"] as const;
const TRIGGERS = ["single", "correlation"] as const;
const OPERATORS = ["equals", "contains", "regex", "gt", "lt", "gte", "lte", "exists", "count_gte"] as const;
const SOURCES = ["event_type", "event_data", "phase_duration", "event_count"] as const;

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

const EMPTY_CONDITION: RuleCondition = {
  signal: "",
  source: "event_type",
  eventType: "",
  dataField: "",
  operator: "contains",
  value: "",
  required: true,
};

const EMPTY_FACTOR: ConfidenceFactor = {
  signal: "",
  condition: "",
  weight: 10,
};

const EMPTY_FORM: RuleForm = {
  ruleId: "",
  title: "",
  description: "",
  severity: "warning",
  category: "device",
  trigger: "single",
  explanation: "",
  baseConfidence: 50,
  confidenceThreshold: 40,
  conditions: [{ ...EMPTY_CONDITION }],
  confidenceFactors: [],
  remediation: [],
  relatedDocs: [],
};

function ruleToForm(rule: AnalyzeRule): RuleForm {
  return {
    ruleId: rule.ruleId,
    title: rule.title,
    description: rule.description || "",
    severity: rule.severity,
    category: rule.category,
    trigger: rule.trigger || "single",
    explanation: rule.explanation || "",
    baseConfidence: rule.baseConfidence,
    confidenceThreshold: rule.confidenceThreshold,
    conditions: rule.conditions.length > 0 ? rule.conditions.map(c => ({ ...c })) : [{ ...EMPTY_CONDITION }],
    confidenceFactors: rule.confidenceFactors.map(f => ({ ...f })),
    remediation: rule.remediation.map(r => ({ title: r.title, steps: [...r.steps] })),
    relatedDocs: rule.relatedDocs.map(d => ({ ...d })),
  };
}

export default function AnalyzeRulesPage() {
  const router = useRouter();
  const { tenantId } = useTenant();
  const { getAccessToken, user } = useAuth();
  const isGalacticAdmin = user?.isGalacticAdmin ?? false;

  const [rules, setRules] = useState<AnalyzeRule[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

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
    if (!confirm(`Are you sure you want to delete the rule "${rule.title}"? This action cannot be undone.`)) {
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
      if (expandedRuleId === rule.ruleId) setExpandedRuleId(null);
      showSuccess(`Rule "${rule.title}" deleted successfully!`);
    } catch (err) {
      console.error("Error deleting rule:", err);
      showError(err instanceof Error ? err.message : "Failed to delete rule");
    } finally {
      setDeletingRuleId(null);
    }
  };

  // Create custom rule
  const handleCreateRule = async () => {
    if (!newRule.ruleId.trim() || !newRule.title.trim()) {
      showError("Rule ID and Title are required.");
      return;
    }

    try {
      setCreating(true);
      setError(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error("Failed to get access token");
      }

      const payload = {
        ruleId: newRule.ruleId.trim(),
        title: newRule.title.trim(),
        description: newRule.description.trim(),
        severity: newRule.severity,
        category: newRule.category,
        trigger: newRule.trigger,
        explanation: newRule.explanation.trim(),
        baseConfidence: newRule.baseConfidence,
        confidenceThreshold: newRule.confidenceThreshold,
        conditions: newRule.conditions.filter(c => c.signal.trim()),
        confidenceFactors: newRule.confidenceFactors.filter(f => f.signal.trim()),
        remediation: newRule.remediation.filter(r => r.title.trim()),
        relatedDocs: newRule.relatedDocs.filter(d => d.title.trim() && d.url.trim()),
      };

      const response = await fetch(`${API_BASE_URL}/api/analyze-rules`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${token}`,
        },
        body: JSON.stringify(payload),
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.error || errorData.message || `Failed to create rule: ${response.statusText}`);
      }

      showSuccess(`Rule "${newRule.title}" created successfully!`);
      setNewRule({ ...EMPTY_FORM, conditions: [{ ...EMPTY_CONDITION }] });
      setShowCreateForm(false);
      await fetchRules();
    } catch (err) {
      console.error("Error creating rule:", err);
      showError(err instanceof Error ? err.message : "Failed to create rule");
    } finally {
      setCreating(false);
    }
  };

  // Start editing
  const startEditing = (rule: AnalyzeRule) => {
    setEditingRuleId(rule.ruleId);
    setEditForm(ruleToForm(rule));
  };

  // Save edited rule
  const handleSaveEdit = async (rule: AnalyzeRule) => {
    if (!editForm.title.trim()) {
      showError("Title is required.");
      return;
    }

    try {
      setSaving(true);
      setError(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error("Failed to get access token");
      }

      const payload = {
        ...rule,
        title: editForm.title.trim(),
        description: editForm.description.trim(),
        severity: editForm.severity,
        category: editForm.category,
        trigger: editForm.trigger,
        explanation: editForm.explanation.trim(),
        baseConfidence: editForm.baseConfidence,
        confidenceThreshold: editForm.confidenceThreshold,
        conditions: editForm.conditions.filter(c => c.signal.trim()),
        confidenceFactors: editForm.confidenceFactors.filter(f => f.signal.trim()),
        remediation: editForm.remediation.filter(r => r.title.trim()),
        relatedDocs: editForm.relatedDocs.filter(d => d.title.trim() && d.url.trim()),
      };

      const isGlobalEdit = rule.isBuiltIn && isGalacticAdmin;
      const url = `${API_BASE_URL}/api/analyze-rules/${encodeURIComponent(rule.ruleId)}${isGlobalEdit ? "?global=true" : ""}`;

      const response = await fetch(url, {
        method: "PUT",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${token}`,
        },
        body: JSON.stringify(payload),
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.error || errorData.message || `Failed to update rule: ${response.statusText}`);
      }

      setEditingRuleId(null);
      showSuccess(`Rule "${editForm.title}" updated successfully${isGlobalEdit ? " (global)" : ""}!`);
      await fetchRules();
    } catch (err) {
      console.error("Error saving rule:", err);
      showError(err instanceof Error ? err.message : "Failed to save rule");
    } finally {
      setSaving(false);
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

  const uniqueCategories = Array.from(new Set(rules.map((r) => r.category.toLowerCase())));

  // Shared form fields renderer
  const renderFormFields = (
    form: RuleForm,
    setForm: (f: RuleForm) => void,
    showRuleId: boolean
  ) => (
    <div className="space-y-5">
      {/* Basic Fields */}
      <div className={`grid grid-cols-1 ${showRuleId ? "sm:grid-cols-2" : ""} gap-4`}>
        {showRuleId && (
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Rule ID <span className="text-red-500">*</span></label>
            <input type="text" value={form.ruleId} onChange={(e) => setForm({ ...form, ruleId: e.target.value })} placeholder="e.g., ANALYZE-CUSTOM-001" autoComplete="off" className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500" />
          </div>
        )}
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Title <span className="text-red-500">*</span></label>
          <input type="text" value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} placeholder="e.g., Proxy Authentication Failure" autoComplete="off" className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500" />
        </div>
      </div>

      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Description</label>
        <textarea value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} placeholder="Describe what this rule detects..." rows={2} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 resize-none" />
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Severity</label>
          <select value={form.severity} onChange={(e) => setForm({ ...form, severity: e.target.value })} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500">
            {SEVERITIES.map((s) => (<option key={s} value={s}>{s.charAt(0).toUpperCase() + s.slice(1)}</option>))}
          </select>
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Category</label>
          <select value={form.category} onChange={(e) => setForm({ ...form, category: e.target.value })} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500">
            {CATEGORIES.map((cat) => (<option key={cat} value={cat}>{cat.charAt(0).toUpperCase() + cat.slice(1)}</option>))}
          </select>
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Trigger Type</label>
          <select value={form.trigger} onChange={(e) => setForm({ ...form, trigger: e.target.value })} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500">
            {TRIGGERS.map((t) => (<option key={t} value={t}>{t.charAt(0).toUpperCase() + t.slice(1)}</option>))}
          </select>
        </div>
      </div>

      {/* Conditions */}
      <div>
        <div className="flex items-center justify-between mb-2">
          <label className="block text-sm font-semibold text-gray-700">Conditions</label>
          <button type="button" onClick={() => setForm({ ...form, conditions: [...form.conditions, { ...EMPTY_CONDITION }] })} className="text-xs text-indigo-600 hover:text-indigo-800 font-medium">+ Add Condition</button>
        </div>
        <div className="space-y-3">
          {form.conditions.map((cond, idx) => (
            <div key={idx} className="bg-gray-50 border border-gray-200 rounded-lg p-3 space-y-2">
              <div className="flex items-center justify-between">
                <span className="text-xs font-medium text-gray-500">Condition {idx + 1}</span>
                {form.conditions.length > 1 && (
                  <button type="button" onClick={() => setForm({ ...form, conditions: form.conditions.filter((_, i) => i !== idx) })} className="text-xs text-red-500 hover:text-red-700">Remove</button>
                )}
              </div>
              <div className="grid grid-cols-1 sm:grid-cols-3 gap-2">
                <input type="text" value={cond.signal} onChange={(e) => { const c = [...form.conditions]; c[idx] = { ...c[idx], signal: e.target.value }; setForm({ ...form, conditions: c }); }} placeholder="Signal name" autoComplete="off" className="px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                <select value={cond.source} onChange={(e) => { const c = [...form.conditions]; c[idx] = { ...c[idx], source: e.target.value }; setForm({ ...form, conditions: c }); }} className="px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 bg-white focus:outline-none focus:ring-1 focus:ring-indigo-500">
                  {SOURCES.map((s) => (<option key={s} value={s}>{s}</option>))}
                </select>
                <input type="text" value={cond.eventType} onChange={(e) => { const c = [...form.conditions]; c[idx] = { ...c[idx], eventType: e.target.value }; setForm({ ...form, conditions: c }); }} placeholder="Event type" autoComplete="off" className="px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
              </div>
              <div className="grid grid-cols-1 sm:grid-cols-4 gap-2">
                <input type="text" value={cond.dataField} onChange={(e) => { const c = [...form.conditions]; c[idx] = { ...c[idx], dataField: e.target.value }; setForm({ ...form, conditions: c }); }} placeholder="Data field" autoComplete="off" className="px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                <select value={cond.operator} onChange={(e) => { const c = [...form.conditions]; c[idx] = { ...c[idx], operator: e.target.value }; setForm({ ...form, conditions: c }); }} className="px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 bg-white focus:outline-none focus:ring-1 focus:ring-indigo-500">
                  {OPERATORS.map((o) => (<option key={o} value={o}>{o}</option>))}
                </select>
                <input type="text" value={cond.value} onChange={(e) => { const c = [...form.conditions]; c[idx] = { ...c[idx], value: e.target.value }; setForm({ ...form, conditions: c }); }} placeholder="Value" autoComplete="off" className="px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                <label className="flex items-center space-x-2 text-sm text-gray-700">
                  <input type="checkbox" checked={cond.required} onChange={(e) => { const c = [...form.conditions]; c[idx] = { ...c[idx], required: e.target.checked }; setForm({ ...form, conditions: c }); }} className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500" />
                  <span>Required</span>
                </label>
              </div>
            </div>
          ))}
        </div>
      </div>

      {/* Confidence Scoring */}
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Base Confidence (%)</label>
          <input type="number" min={0} max={100} value={form.baseConfidence} onChange={(e) => setForm({ ...form, baseConfidence: parseInt(e.target.value) || 0 })} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500" />
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Confidence Threshold (%)</label>
          <input type="number" min={0} max={100} value={form.confidenceThreshold} onChange={(e) => setForm({ ...form, confidenceThreshold: parseInt(e.target.value) || 0 })} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500" />
        </div>
      </div>

      {/* Confidence Factors */}
      <div>
        <div className="flex items-center justify-between mb-2">
          <label className="block text-sm font-semibold text-gray-700">Confidence Factors</label>
          <button type="button" onClick={() => setForm({ ...form, confidenceFactors: [...form.confidenceFactors, { ...EMPTY_FACTOR }] })} className="text-xs text-indigo-600 hover:text-indigo-800 font-medium">+ Add Factor</button>
        </div>
        {form.confidenceFactors.length === 0 ? (
          <p className="text-xs text-gray-400 italic">No confidence factors. Click &quot;+ Add Factor&quot; to add one.</p>
        ) : (
          <div className="space-y-2">
            {form.confidenceFactors.map((factor, idx) => (
              <div key={idx} className="flex items-center gap-2">
                <input type="text" value={factor.signal} onChange={(e) => { const f = [...form.confidenceFactors]; f[idx] = { ...f[idx], signal: e.target.value }; setForm({ ...form, confidenceFactors: f }); }} placeholder="Signal" autoComplete="off" className="flex-1 px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                <input type="text" value={factor.condition} onChange={(e) => { const f = [...form.confidenceFactors]; f[idx] = { ...f[idx], condition: e.target.value }; setForm({ ...form, confidenceFactors: f }); }} placeholder="Condition" autoComplete="off" className="flex-1 px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                <input type="number" value={factor.weight} onChange={(e) => { const f = [...form.confidenceFactors]; f[idx] = { ...f[idx], weight: parseInt(e.target.value) || 0 }; setForm({ ...form, confidenceFactors: f }); }} className="w-20 px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                <span className="text-xs text-gray-500">%</span>
                <button type="button" onClick={() => setForm({ ...form, confidenceFactors: form.confidenceFactors.filter((_, i) => i !== idx) })} className="text-red-400 hover:text-red-600">
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" /></svg>
                </button>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Explanation */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Explanation</label>
        <textarea value={form.explanation} onChange={(e) => setForm({ ...form, explanation: e.target.value })} placeholder="Detailed explanation shown when this rule fires..." rows={3} className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 resize-none" />
      </div>

      {/* Remediation Steps */}
      <div>
        <div className="flex items-center justify-between mb-2">
          <label className="block text-sm font-semibold text-gray-700">Remediation Steps</label>
          <button type="button" onClick={() => setForm({ ...form, remediation: [...form.remediation, { title: "", steps: [""] }] })} className="text-xs text-indigo-600 hover:text-indigo-800 font-medium">+ Add Section</button>
        </div>
        {form.remediation.length === 0 ? (
          <p className="text-xs text-gray-400 italic">No remediation steps. Click &quot;+ Add Section&quot; to add one.</p>
        ) : (
          <div className="space-y-3">
            {form.remediation.map((rem, rIdx) => (
              <div key={rIdx} className="bg-green-50 border border-green-200 rounded-lg p-3 space-y-2">
                <div className="flex items-center justify-between">
                  <input type="text" value={rem.title} onChange={(e) => { const r = [...form.remediation]; r[rIdx] = { ...r[rIdx], title: e.target.value }; setForm({ ...form, remediation: r }); }} placeholder="Section title" autoComplete="off" className="flex-1 px-3 py-1.5 border border-green-300 rounded text-sm text-gray-900 placeholder-gray-400 bg-white focus:outline-none focus:ring-1 focus:ring-green-500" />
                  <button type="button" onClick={() => setForm({ ...form, remediation: form.remediation.filter((_, i) => i !== rIdx) })} className="ml-2 text-red-400 hover:text-red-600 text-xs">Remove</button>
                </div>
                {rem.steps.map((step, sIdx) => (
                  <div key={sIdx} className="flex items-center gap-2">
                    <span className="text-xs text-gray-500 w-5 text-right">{sIdx + 1}.</span>
                    <input type="text" value={step} onChange={(e) => { const r = [...form.remediation]; const steps = [...r[rIdx].steps]; steps[sIdx] = e.target.value; r[rIdx] = { ...r[rIdx], steps }; setForm({ ...form, remediation: r }); }} placeholder="Step description" autoComplete="off" className="flex-1 px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-green-500" />
                    {rem.steps.length > 1 && (
                      <button type="button" onClick={() => { const r = [...form.remediation]; r[rIdx] = { ...r[rIdx], steps: r[rIdx].steps.filter((_, i) => i !== sIdx) }; setForm({ ...form, remediation: r }); }} className="text-red-400 hover:text-red-600">
                        <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" /></svg>
                      </button>
                    )}
                  </div>
                ))}
                <button type="button" onClick={() => { const r = [...form.remediation]; r[rIdx] = { ...r[rIdx], steps: [...r[rIdx].steps, ""] }; setForm({ ...form, remediation: r }); }} className="text-xs text-green-600 hover:text-green-800 font-medium">+ Add Step</button>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Related Docs */}
      <div>
        <div className="flex items-center justify-between mb-2">
          <label className="block text-sm font-semibold text-gray-700">Related Documentation</label>
          <button type="button" onClick={() => setForm({ ...form, relatedDocs: [...form.relatedDocs, { title: "", url: "" }] })} className="text-xs text-indigo-600 hover:text-indigo-800 font-medium">+ Add Link</button>
        </div>
        {form.relatedDocs.length === 0 ? (
          <p className="text-xs text-gray-400 italic">No related docs. Click &quot;+ Add Link&quot; to add one.</p>
        ) : (
          <div className="space-y-2">
            {form.relatedDocs.map((doc, idx) => (
              <div key={idx} className="flex items-center gap-2">
                <input type="text" value={doc.title} onChange={(e) => { const d = [...form.relatedDocs]; d[idx] = { ...d[idx], title: e.target.value }; setForm({ ...form, relatedDocs: d }); }} placeholder="Link title" autoComplete="off" className="flex-1 px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                <input type="text" value={doc.url} onChange={(e) => { const d = [...form.relatedDocs]; d[idx] = { ...d[idx], url: e.target.value }; setForm({ ...form, relatedDocs: d }); }} placeholder="https://..." autoComplete="off" className="flex-1 px-3 py-1.5 border border-gray-300 rounded text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-indigo-500" />
                <button type="button" onClick={() => setForm({ ...form, relatedDocs: form.relatedDocs.filter((_, i) => i !== idx) })} className="text-red-400 hover:text-red-600">
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" /></svg>
                </button>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100">
        {/* Header */}
        <header className="bg-white shadow-sm border-b border-gray-200">
          <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-4">
            <div className="flex items-center justify-between">
              <div className="flex items-center space-x-4">
                <button onClick={() => router.push("/")} className="flex items-center space-x-2 text-gray-600 hover:text-gray-900 transition-colors">
                  <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 19l-7-7m0 0l7-7m-7 7h18" /></svg>
                  <span>Back to Dashboard</span>
                </button>
              </div>
              <div>
                <h1 className="text-2xl font-bold text-gray-900">Analyze Rules</h1>
                <p className="text-sm text-gray-500">Manage event analysis rules for issue detection</p>
              </div>
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

              {/* Filter Bar + Create Button */}
              <div className="bg-white rounded-lg shadow p-4">
                <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-4">
                  <div className="flex flex-col md:flex-row md:items-center md:space-x-4 space-y-3 md:space-y-0 flex-1">
                    <div className="flex-1 relative">
                      <svg className="absolute left-3 top-1/2 transform -translate-y-1/2 w-5 h-5 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" /></svg>
                      <input type="text" placeholder="Search by title or rule ID..." value={searchQuery} onChange={(e) => setSearchQuery(e.target.value)} className="w-full pl-10 pr-10 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors" />
                      {searchQuery && (
                        <button onClick={() => setSearchQuery("")} className="absolute right-3 top-1/2 transform -translate-y-1/2 text-gray-400 hover:text-gray-600" title="Clear search">
                          <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" /></svg>
                        </button>
                      )}
                    </div>
                    <select value={severityFilter} onChange={(e) => setSeverityFilter(e.target.value)} className="px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500">
                      <option value="all">All Severities</option>
                      <option value="critical">Critical</option>
                      <option value="high">High</option>
                      <option value="warning">Warning</option>
                      <option value="info">Info</option>
                    </select>
                    <select value={categoryFilter} onChange={(e) => setCategoryFilter(e.target.value)} className="px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500">
                      <option value="all">All Categories</option>
                      {uniqueCategories.map((cat) => (<option key={cat} value={cat}>{cat.charAt(0).toUpperCase() + cat.slice(1)}</option>))}
                    </select>
                    <select value={typeFilter} onChange={(e) => setTypeFilter(e.target.value)} className="px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500">
                      <option value="all">All Types</option>
                      <option value="builtin">Built-in</option>
                      <option value="custom">Custom</option>
                    </select>
                  </div>
                  <button onClick={() => { setShowCreateForm(!showCreateForm); if (showCreateForm) setNewRule({ ...EMPTY_FORM, conditions: [{ ...EMPTY_CONDITION }] }); }} className="px-4 py-2 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 transition-colors flex items-center space-x-2 text-sm font-medium whitespace-nowrap">
                    {showCreateForm ? (
                      <><svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" /></svg><span>Cancel</span></>
                    ) : (
                      <><svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" /></svg><span>Create Custom Rule</span></>
                    )}
                  </button>
                </div>
                <div className="mt-3 text-sm text-gray-500">
                  Showing {filteredRules.length} of {totalRules} rule{totalRules !== 1 ? "s" : ""}
                </div>
              </div>

              {/* Create Custom Rule Form */}
              {showCreateForm && (
                <div className="bg-white rounded-lg shadow">
                  <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-indigo-50 to-purple-50">
                    <div className="flex items-center space-x-2">
                      <svg className="w-6 h-6 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6v6m0 0v6m0-6h6m-6 0H6" /></svg>
                      <div>
                        <h2 className="text-xl font-semibold text-gray-900">Create Custom Analyze Rule</h2>
                        <p className="text-sm text-gray-500 mt-1">Define conditions and confidence scoring for issue detection</p>
                      </div>
                    </div>
                  </div>
                  <div className="p-6">
                    {renderFormFields(newRule, setNewRule, true)}
                    <div className="flex items-center justify-end space-x-3 pt-4 mt-5 border-t border-gray-200">
                      <button onClick={() => { setShowCreateForm(false); setNewRule({ ...EMPTY_FORM, conditions: [{ ...EMPTY_CONDITION }] }); }} disabled={creating} className="px-6 py-2 border border-gray-300 rounded-lg text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium">Cancel</button>
                      <button onClick={handleCreateRule} disabled={creating} className="px-6 py-2 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2 text-sm font-medium">
                        {creating ? (<><div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div><span>Creating...</span></>) : (<span>Save Rule</span>)}
                      </button>
                    </div>
                  </div>
                </div>
              )}

              {/* Rules List */}
              {filteredRules.length === 0 ? (
                <div className="bg-white rounded-lg shadow p-8 text-center">
                  <svg className="w-12 h-12 text-gray-400 mx-auto mb-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9.172 16.172a4 4 0 015.656 0M9 10h.01M15 10h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" /></svg>
                  <p className="text-gray-500">{rules.length === 0 ? "No analyze rules found." : "No rules match your current filters."}</p>
                </div>
              ) : (
                <div className="space-y-3">
                  {filteredRules.map((rule) => {
                    const isExpanded = expandedRuleId === rule.ruleId;
                    const isEditing = editingRuleId === rule.ruleId;
                    const sevColor = getSeverityColor(rule.severity);
                    const catColor = getCategoryColor(rule.category);
                    const canEdit = isGalacticAdmin || !rule.isBuiltIn;

                    return (
                      <div
                        key={rule.ruleId}
                        className={`bg-white rounded-lg shadow border transition-all ${
                          isExpanded ? "border-indigo-300 ring-1 ring-indigo-200" : "border-gray-200 hover:border-gray-300"
                        }`}
                      >
                        {/* Collapsed Header */}
                        <div className="p-4 cursor-pointer select-none" onClick={() => { if (isEditing) return; setExpandedRuleId(isExpanded ? null : rule.ruleId); if (isExpanded && editingRuleId === rule.ruleId) setEditingRuleId(null); }}>
                          <div className="flex items-center space-x-4">
                            <button onClick={(e) => { e.stopPropagation(); handleToggleRule(rule); }} disabled={togglingRuleId === rule.ruleId} className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${togglingRuleId === rule.ruleId ? "opacity-50 cursor-not-allowed" : "cursor-pointer"} ${rule.enabled ? "bg-green-500" : "bg-gray-300"}`} title={rule.enabled ? "Disable rule" : "Enable rule"}>
                              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${rule.enabled ? "translate-x-6" : "translate-x-1"}`} />
                            </button>
                            <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold ${sevColor.bg} ${sevColor.text} flex-shrink-0`}>
                              <span className={`w-1.5 h-1.5 rounded-full ${sevColor.dot} mr-1.5`}></span>
                              {rule.severity.charAt(0).toUpperCase() + rule.severity.slice(1)}
                            </span>
                            <span className="text-xs font-mono text-gray-400 flex-shrink-0 hidden sm:inline">{rule.ruleId}</span>
                            <div className="flex-1 min-w-0">
                              <h3 className="text-sm font-semibold text-gray-900 truncate">{rule.title}</h3>
                            </div>
                            <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${catColor.bg} ${catColor.text} flex-shrink-0`}>
                              {rule.category.charAt(0).toUpperCase() + rule.category.slice(1)}
                            </span>
                            <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium flex-shrink-0 ${rule.isBuiltIn ? "bg-blue-50 text-blue-600 border border-blue-200" : rule.isCommunity ? "bg-green-100 text-green-700" : "bg-purple-50 text-purple-600 border border-purple-200"}`}>
                              {rule.isBuiltIn ? "Built-in" : rule.isCommunity ? "Community" : "Custom"}
                            </span>
                            <span className="text-xs text-gray-500 flex-shrink-0 hidden md:inline" title="Confidence Threshold">Threshold: {rule.confidenceThreshold}%</span>
                            <svg className={`w-5 h-5 text-gray-400 transition-transform flex-shrink-0 ${isExpanded ? "rotate-180" : ""}`} fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" /></svg>
                          </div>
                        </div>

                        {/* Expanded Details (read-only) */}
                        {isExpanded && !isEditing && (
                          <div className="border-t border-gray-200 p-6 space-y-6">
                            <div className="flex flex-wrap items-center gap-4 text-sm text-gray-500">
                              <span><span className="font-medium text-gray-700">Version:</span> {rule.version}</span>
                              <span><span className="font-medium text-gray-700">Author:</span> {rule.author}</span>
                              <span><span className="font-medium text-gray-700">Trigger:</span> {(rule.trigger || "single").charAt(0).toUpperCase() + (rule.trigger || "single").slice(1)}</span>
                              <span><span className="font-medium text-gray-700">Created:</span> {new Date(rule.createdAt).toLocaleDateString()}</span>
                              <span><span className="font-medium text-gray-700">Updated:</span> {new Date(rule.updatedAt).toLocaleDateString()}</span>
                              <span className="text-xs font-mono text-gray-400 sm:hidden">{rule.ruleId}</span>
                            </div>

                            {rule.tags && rule.tags.length > 0 && (
                              <div className="flex flex-wrap gap-2">
                                {rule.tags.map((tag, idx) => (<span key={idx} className="inline-flex items-center px-2 py-0.5 rounded text-xs bg-gray-100 text-gray-600">#{tag}</span>))}
                              </div>
                            )}

                            <div>
                              <h4 className="text-sm font-semibold text-gray-700 mb-1">Description</h4>
                              <p className="text-sm text-gray-600 leading-relaxed">{rule.description}</p>
                            </div>

                            {rule.conditions.length > 0 && (
                              <div>
                                <h4 className="text-sm font-semibold text-gray-700 mb-2">Conditions ({rule.conditions.length})</h4>
                                <div className="space-y-2">
                                  {rule.conditions.map((condition, idx) => (
                                    <div key={idx} className="bg-gray-50 border border-gray-200 rounded-lg p-3 text-sm">
                                      <div className="flex flex-wrap items-center gap-2 mb-1">
                                        <span className="font-medium text-gray-800">{condition.signal}</span>
                                        {condition.required && (<span className="text-xs px-1.5 py-0.5 rounded bg-red-100 text-red-700 font-medium">Required</span>)}
                                      </div>
                                      <div className="text-gray-500 space-y-0.5">
                                        <p>
                                          <span className="text-gray-600 font-medium">Source:</span> {condition.source}
                                          {condition.eventType && (<span> | <span className="text-gray-600 font-medium">Event Type:</span> {condition.eventType}</span>)}
                                          {condition.dataField && (<span> | <span className="text-gray-600 font-medium">Field:</span> <code className="bg-gray-200 px-1 rounded text-xs">{condition.dataField}</code></span>)}
                                        </p>
                                        <p>
                                          <span className="text-gray-600 font-medium">Operator:</span> <code className="bg-gray-200 px-1 rounded text-xs">{condition.operator}</code>{" "}
                                          <span className="text-gray-600 font-medium">Value:</span> <code className="bg-gray-200 px-1 rounded text-xs">{condition.value}</code>
                                        </p>
                                      </div>
                                    </div>
                                  ))}
                                </div>
                              </div>
                            )}

                            <div>
                              <h4 className="text-sm font-semibold text-gray-700 mb-2">Confidence Scoring</h4>
                              <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 space-y-3">
                                <div className="flex items-center space-x-6 text-sm">
                                  <span><span className="text-gray-600 font-medium">Base Confidence:</span> <span className="font-semibold text-gray-900">{rule.baseConfidence}%</span></span>
                                  <span><span className="text-gray-600 font-medium">Threshold:</span> <span className="font-semibold text-gray-900">{rule.confidenceThreshold}%</span></span>
                                </div>
                                {rule.confidenceFactors.length > 0 && (
                                  <div>
                                    <p className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-2">Confidence Factors</p>
                                    <div className="space-y-1">
                                      {rule.confidenceFactors.map((factor, idx) => (
                                        <div key={idx} className="flex items-center justify-between text-sm bg-white border border-gray-100 rounded px-3 py-1.5">
                                          <div><span className="font-medium text-gray-700">{factor.signal}</span><span className="text-gray-400 mx-2">-</span><span className="text-gray-500">{factor.condition}</span></div>
                                          <span className={`font-semibold ${factor.weight > 0 ? "text-green-600" : "text-red-600"}`}>{factor.weight > 0 ? "+" : ""}{factor.weight}%</span>
                                        </div>
                                      ))}
                                    </div>
                                  </div>
                                )}
                              </div>
                            </div>

                            {rule.explanation && (
                              <div>
                                <h4 className="text-sm font-semibold text-gray-700 mb-1">Explanation</h4>
                                <p className="text-sm text-gray-600 leading-relaxed bg-blue-50 border border-blue-200 rounded-lg p-3">{rule.explanation}</p>
                              </div>
                            )}

                            {rule.remediation.length > 0 && (
                              <div>
                                <h4 className="text-sm font-semibold text-gray-700 mb-2">Remediation Steps</h4>
                                <div className="space-y-3">
                                  {rule.remediation.map((rem, idx) => (
                                    <div key={idx} className="bg-green-50 border border-green-200 rounded-lg p-4">
                                      <h5 className="text-sm font-semibold text-green-800 mb-2">{rem.title}</h5>
                                      <ol className="list-decimal list-inside space-y-1">
                                        {rem.steps.map((step, sIdx) => (<li key={sIdx} className="text-sm text-green-700">{step}</li>))}
                                      </ol>
                                    </div>
                                  ))}
                                </div>
                              </div>
                            )}

                            {rule.relatedDocs.length > 0 && (
                              <div>
                                <h4 className="text-sm font-semibold text-gray-700 mb-2">Related Documentation</h4>
                                <div className="flex flex-wrap gap-2">
                                  {rule.relatedDocs.map((doc, idx) => (
                                    <a key={idx} href={doc.url} target="_blank" rel="noopener noreferrer" className="inline-flex items-center space-x-1.5 px-3 py-1.5 bg-indigo-50 border border-indigo-200 rounded-lg text-sm text-indigo-700 hover:bg-indigo-100 transition-colors">
                                      <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" /></svg>
                                      <span>{doc.title}</span>
                                    </a>
                                  ))}
                                </div>
                              </div>
                            )}

                            {/* Actions */}
                            <div className="flex items-center justify-end space-x-3 pt-4 border-t border-gray-200">
                              {canEdit && (
                                <button onClick={() => startEditing(rule)} className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 transition-colors flex items-center space-x-2" title={rule.isBuiltIn ? "Edit global rule (Galactic Admin)" : "Edit rule"}>
                                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" /></svg>
                                  <span>{rule.isBuiltIn ? "Edit (Global)" : "Edit"}</span>
                                </button>
                              )}
                              {!rule.isBuiltIn && (
                                <button onClick={() => handleDeleteRule(rule)} disabled={deletingRuleId === rule.ruleId} className="px-4 py-2 text-sm bg-red-600 text-white rounded-lg hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2">
                                  {deletingRuleId === rule.ruleId ? (<><div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div><span>Deleting...</span></>) : (<><svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" /></svg><span>Delete</span></>)}
                                </button>
                              )}
                            </div>
                          </div>
                        )}

                        {/* Edit Form */}
                        {isExpanded && isEditing && (
                          <div className="border-t border-gray-200 p-6">
                            <div className="flex items-center space-x-2 mb-4">
                              <svg className="w-5 h-5 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" /></svg>
                              <h4 className="text-sm font-semibold text-gray-900">
                                Editing: {rule.ruleId}
                                {rule.isBuiltIn && isGalacticAdmin && (
                                  <span className="ml-2 inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-700 border border-amber-200">Global Edit</span>
                                )}
                              </h4>
                            </div>

                            {rule.isBuiltIn && isGalacticAdmin && (
                              <div className="mb-4 bg-amber-50 border border-amber-200 rounded-lg p-3 text-sm text-amber-800">
                                <strong>Galactic Admin:</strong> Changes will apply globally to all tenants that haven&apos;t overridden this rule.
                              </div>
                            )}

                            {renderFormFields(editForm, setEditForm, false)}

                            <div className="flex items-center justify-end space-x-3 pt-4 mt-5 border-t border-gray-200">
                              <button onClick={() => setEditingRuleId(null)} disabled={saving} className="px-6 py-2 border border-gray-300 rounded-lg text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium">Cancel</button>
                              <button onClick={() => handleSaveEdit(rule)} disabled={saving} className="px-6 py-2 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2 text-sm font-medium">
                                {saving ? (<><div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div><span>Saving...</span></>) : (<span>Save Changes</span>)}
                              </button>
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
