"use client";

import { useEffect, useState, useCallback } from "react";
import Link from "next/link";
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
  valueName: string;          // registry
  eventId: string;            // eventlog
  messageFilter: string;      // eventlog
  maxEntries: string;         // eventlog
  source: string;             // eventlog
  readContent: boolean;       // file
  logPattern: string;         // logparser
  trackPosition: boolean;     // logparser
  maxLines: string;           // logparser
  trigger: string;
  intervalSeconds: number;
  triggerPhase: string;
  triggerEventType: string;
  outputEventType: string;
  outputSeverity: string;
}

const CATEGORIES = ["network", "identity", "apps", "device", "esp", "enrollment"] as const;
const COLLECTOR_TYPES = ["registry", "eventlog", "wmi", "file", "command_allowlisted", "logparser"] as const;
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

const COLLECTOR_TYPE_LABELS: Record<string, string> = {
  registry: "Registry",
  eventlog: "Event Log",
  wmi: "WMI Query",
  file: "File",
  command_allowlisted: "Command (Allowlisted)",
  command: "Command (Allowlisted)", // legacy alias
  logparser: "Log Parser",
};

const TARGET_PLACEHOLDERS: Record<string, string> = {
  registry: "e.g., HKLM\\SOFTWARE\\Microsoft\\Enrollments",
  eventlog: "e.g., Microsoft-Windows-Shell-Core/Operational",
  wmi: "e.g., SELECT * FROM Win32_BIOS",
  file: "e.g., C:\\Windows\\Panther\\UnattendGC\\setupact.log",
  command_allowlisted: "e.g., Get-Tpm or dsregcmd /status",
  logparser: "e.g., %ProgramData%\\Microsoft\\IntuneManagementExtension\\Logs\\AppWorkload.log",
};

const TARGET_HINTS: Record<string, string> = {
  registry: "Full registry path including hive (HKLM, HKCU). The agent reads values from this key.",
  eventlog: "Event log name — supports operational/analytic logs like Microsoft-Windows-Shell-Core/Operational.",
  wmi: "Full WQL query (SELECT * FROM ...). Must use an allowed WMI class.",
  file: "File path. Environment variables like %ProgramData% are supported. Must be within allowed directories.",
  command_allowlisted: "Exact command string from the agent's allowlist. Custom commands are not permitted.",
  logparser: "Path to a CMTrace-format log file. Environment variables are expanded. Requires a regex pattern in parameters.",
};

const EMPTY_FORM: NewRuleForm = {
  ruleId: "",
  title: "",
  description: "",
  category: "device",
  collectorType: "registry",
  target: "",
  valueName: "",
  eventId: "",
  messageFilter: "",
  maxEntries: "",
  source: "",
  readContent: false,
  logPattern: "",
  trackPosition: true,
  maxLines: "",
  trigger: "startup",
  intervalSeconds: 60,
  triggerPhase: "",
  triggerEventType: "",
  outputEventType: "",
  outputSeverity: "info",
};

function downloadAsJson(data: unknown, filename: string) {
  const json = JSON.stringify(data, null, 2);
  const blob = new Blob([json], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

function stripInternalFields(rule: GatherRule) {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const { isBuiltIn, isCommunity, createdAt, updatedAt, ...rest } = rule;
  return rest;
}

function formatTrigger(trigger: string) {
  switch (trigger) {
    case "phase_change": return "Phase Change";
    case "on_event": return "On Event";
    default: return trigger.charAt(0).toUpperCase() + trigger.slice(1);
  }
}

export default function GatherRulesPage() {
  const router = useRouter();
  const { tenantId } = useTenant();
  const { getAccessToken, user } = useAuth();

  const [rules, setRules] = useState<GatherRule[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  // Filter state
  const [searchQuery, setSearchQuery] = useState("");
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [typeFilter, setTypeFilter] = useState("all");

  // Expanded / editing state
  const [expandedRuleId, setExpandedRuleId] = useState<string | null>(null);
  const [editingRuleId, setEditingRuleId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<NewRuleForm>({ ...EMPTY_FORM });
  const [saving, setSaving] = useState(false);

  // Create form state
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [newRule, setNewRule] = useState<NewRuleForm>({ ...EMPTY_FORM });
  const [creating, setCreating] = useState(false);

  // JSON mode (create + edit)
  const [jsonModeCreate, setJsonModeCreate] = useState(false);
  const [jsonModeEdit, setJsonModeEdit] = useState(false);
  const [jsonText, setJsonText] = useState("");
  const [jsonError, setJsonError] = useState<string | null>(null);

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

      const response = await fetch(`${API_BASE_URL}/api/rules/gather`, {
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

      const response = await fetch(`${API_BASE_URL}/api/rules/gather/${encodeURIComponent(rule.ruleId)}`, {
        method: "PUT",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${token}`,
        },
        body: JSON.stringify({ ...rule, enabled: !rule.enabled }),
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

  // Delete rule (custom rules only)
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

      const url = `${API_BASE_URL}/api/rules/gather/${encodeURIComponent(rule.ruleId)}`;
      const response = await fetch(url, {
        method: "DELETE",
        headers: {
          Authorization: `Bearer ${token}`,
        },
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || errorData.error || `Failed to delete rule: ${response.statusText}`);
      }

      setRules((prev) => prev.filter((r) => r.ruleId !== rule.ruleId));
      if (expandedRuleId === rule.ruleId) setExpandedRuleId(null);
      showSuccess(`Rule "${rule.title}" deleted.`);
    } catch (err) {
      console.error("Error deleting rule:", err);
      showError(err instanceof Error ? err.message : "Failed to delete rule");
    } finally {
      setDeletingRule(null);
    }
  };

  const buildParameters = (form: NewRuleForm): Record<string, string> => {
    const params: Record<string, string> = {};
    if (form.collectorType === "registry" && form.valueName.trim()) {
      params.valueName = form.valueName.trim();
    }
    if (form.collectorType === "eventlog") {
      if (form.eventId.trim()) params.eventId = form.eventId.trim();
      if (form.messageFilter.trim()) params.messageFilter = form.messageFilter.trim();
      if (form.maxEntries.trim()) params.maxEntries = form.maxEntries.trim();
      if (form.source.trim()) params.source = form.source.trim();
    }
    if (form.collectorType === "file") {
      if (form.readContent) params.readContent = "true";
    }
    if (form.collectorType === "logparser") {
      if (form.logPattern.trim()) params.pattern = form.logPattern.trim();
      params.trackPosition = form.trackPosition ? "true" : "false";
      if (form.maxLines.trim()) params.maxLines = form.maxLines.trim();
    }
    return params;
  };

  // Create custom rule
  const handleCreateRule = async (overrideForm?: NewRuleForm) => {
    const formData = overrideForm || newRule;
    if (!formData.ruleId.trim() || !formData.title.trim() || !formData.target.trim() || !formData.outputEventType.trim()) {
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
        ruleId: formData.ruleId.trim(),
        title: formData.title.trim(),
        description: formData.description.trim(),
        category: formData.category,
        collectorType: formData.collectorType,
        target: formData.target.trim(),
        trigger: formData.trigger,
        outputEventType: formData.outputEventType.trim(),
        outputSeverity: formData.outputSeverity,
        parameters: buildParameters(formData),
      };

      if (formData.trigger === "interval") {
        payload.intervalSeconds = formData.intervalSeconds;
      }
      if (formData.trigger === "phase_change") {
        payload.triggerPhase = formData.triggerPhase.trim();
      }
      if (formData.trigger === "on_event") {
        payload.triggerEventType = formData.triggerEventType.trim();
      }

      const response = await fetch(`${API_BASE_URL}/api/rules/gather`, {
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

      showSuccess(`Rule "${formData.title}" created successfully!`);
      setNewRule({ ...EMPTY_FORM });
      setShowCreateForm(false);
      setJsonModeCreate(false);
      setJsonError(null);
      await fetchRules();
    } catch (err) {
      console.error("Error creating rule:", err);
      showError(err instanceof Error ? err.message : "Failed to create rule");
    } finally {
      setCreating(false);
    }
  };

  // Start editing a rule (Galactic Admin for built-in, or custom rules)
  const startEditing = (rule: GatherRule) => {
    setEditingRuleId(rule.ruleId);
    setEditForm({
      ruleId: rule.ruleId,
      title: rule.title,
      description: rule.description || "",
      category: rule.category,
      collectorType: rule.collectorType,
      target: rule.target,
      trigger: rule.trigger,
      intervalSeconds: rule.intervalSeconds || 60,
      triggerPhase: rule.triggerPhase || "",
      triggerEventType: rule.triggerEventType || "",
      outputEventType: rule.outputEventType,
      outputSeverity: rule.outputSeverity,
      valueName: rule.parameters?.valueName || "",
      eventId: rule.parameters?.eventId || "",
      messageFilter: rule.parameters?.messageFilter || "",
      maxEntries: rule.parameters?.maxEntries || "",
      source: rule.parameters?.source || "",
      readContent: rule.parameters?.readContent === "true",
      logPattern: rule.parameters?.pattern || "",
      trackPosition: rule.parameters?.trackPosition !== "false",
      maxLines: rule.parameters?.maxLines || "",
    });
  };

  // Save edited rule
  const handleSaveEdit = async (rule: GatherRule, overrideForm?: NewRuleForm) => {
    const formData = overrideForm || editForm;
    if (!formData.title.trim() || !formData.target.trim() || !formData.outputEventType.trim()) {
      showError("Title, Target, and Output Event Type are required.");
      return;
    }

    try {
      setSaving(true);
      setError(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error("Failed to get access token");
      }

      // Increment version: "1.0" → "1.1", "1.9" → "1.10", "2" → "2.1", unknown → "1.1"
      const bumpVersion = (v: string): string => {
        const parts = (v ?? "1.0").split(".");
        const major = parts[0] ?? "1";
        const minor = parseInt(parts[1] ?? "0", 10);
        return `${major}.${minor + 1}`;
      };

      const payload: Record<string, unknown> = {
        ...rule,
        title: formData.title.trim(),
        description: formData.description.trim(),
        category: formData.category,
        collectorType: formData.collectorType,
        target: formData.target.trim(),
        trigger: formData.trigger,
        outputEventType: formData.outputEventType.trim(),
        outputSeverity: formData.outputSeverity,
        parameters: buildParameters(formData),
        author: user?.displayName || user?.upn || rule.author,
        version: bumpVersion(rule.version),
      };

      if (formData.trigger === "interval") {
        payload.intervalSeconds = formData.intervalSeconds;
      }
      if (formData.trigger === "phase_change") {
        payload.triggerPhase = formData.triggerPhase.trim();
      }
      if (formData.trigger === "on_event") {
        payload.triggerEventType = formData.triggerEventType.trim();
      }

      const url = `${API_BASE_URL}/api/rules/gather/${encodeURIComponent(rule.ruleId)}`;

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
      setJsonModeEdit(false);
      setJsonError(null);
      showSuccess(`Rule "${formData.title}" updated successfully!`);
      await fetchRules();
    } catch (err) {
      console.error("Error saving rule:", err);
      showError(err instanceof Error ? err.message : "Failed to save rule");
    } finally {
      setSaving(false);
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
      (typeFilter === "builtin" && rule.isBuiltIn && !rule.isCommunity) ||
      (typeFilter === "community" && rule.isCommunity) ||
      (typeFilter === "custom" && !rule.isBuiltIn && !rule.isCommunity);

    return matchesSearch && matchesCategory && matchesType;
  });

  // Summary stats
  const totalRules = rules.length;
  const activeRules = rules.filter((r) => r.enabled).length;
  const builtInCount = rules.filter((r) => r.isBuiltIn && !r.isCommunity).length;
  const communityCount = rules.filter((r) => r.isCommunity).length;
  const customCount = rules.filter((r) => !r.isBuiltIn && !r.isCommunity).length;

  const handleExportSingle = (rule: GatherRule) => {
    const cleaned = stripInternalFields(rule);
    downloadAsJson({ "$schema": "../schema/gather-rule.schema.json", ...cleaned }, `${rule.ruleId}.json`);
  };

  const handleExportAll = () => {
    const cleaned = filteredRules.map(stripInternalFields);
    downloadAsJson(cleaned, "gather-rules-export.json");
  };

  const getCategoryBadge = (category: string) => {
    const colors = CATEGORY_COLORS[category] || { bg: "bg-gray-100", text: "text-gray-700" };
    return (
      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${colors.bg} ${colors.text}`}>
        {category}
      </span>
    );
  };

  // Shared form fields component (used by both create and edit forms)
  const renderFormFields = (
    form: NewRuleForm,
    setForm: (f: NewRuleForm) => void,
    showRuleId: boolean
  ) => (
    <div className="space-y-5">
      {/* Row 1: Rule ID (create only), Title */}
      <div className={`grid grid-cols-1 ${showRuleId ? "sm:grid-cols-2" : ""} gap-4`}>
        {showRuleId && (
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Rule ID <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              value={form.ruleId}
              onChange={(e) => setForm({ ...form, ruleId: e.target.value })}
              placeholder="e.g., custom-network-check"
              autoComplete="off"
              className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
            />
          </div>
        )}
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">
            Title <span className="text-red-500">*</span>
          </label>
          <input
            type="text"
            value={form.title}
            onChange={(e) => setForm({ ...form, title: e.target.value })}
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
          value={form.description}
          onChange={(e) => setForm({ ...form, description: e.target.value })}
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
            value={form.category}
            onChange={(e) => setForm({ ...form, category: e.target.value })}
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
            value={form.collectorType}
            onChange={(e) => setForm({ ...form, collectorType: e.target.value })}
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          >
            {COLLECTOR_TYPES.map((ct) => (
              <option key={ct} value={ct}>
                {COLLECTOR_TYPE_LABELS[ct] || (ct.charAt(0).toUpperCase() + ct.slice(1))}
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
          value={form.target}
          onChange={(e) => setForm({ ...form, target: e.target.value })}
          placeholder={TARGET_PLACEHOLDERS[form.collectorType] || "Target for data collection"}
          autoComplete="off"
          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
        />
        <p className="text-xs text-gray-400 mt-1">{TARGET_HINTS[form.collectorType] || "Registry path, WMI class, event log name, file path, or command depending on collector type"}</p>
      </div>

      {/* Registry: optional Value Name */}
      {form.collectorType === "registry" && (
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Value Name</label>
          <input
            type="text"
            value={form.valueName}
            onChange={(e) => setForm({ ...form, valueName: e.target.value })}
            placeholder="e.g., IsRecoveryAllowed (leave empty to read all values)"
            autoComplete="off"
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          />
          <p className="text-xs text-gray-400 mt-1">Specific registry value to read. Leave empty to read all values in the key.</p>
        </div>
      )}

      {/* EventLog: optional filters */}
      {form.collectorType === "eventlog" && (
        <div className="space-y-4">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Event ID</label>
              <input
                type="text"
                value={form.eventId}
                onChange={(e) => setForm({ ...form, eventId: e.target.value })}
                placeholder="e.g., 62407 (leave empty for all events)"
                autoComplete="off"
                className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
              />
              <p className="text-xs text-gray-400 mt-1">Filter by specific Event ID. Leave empty to collect all events.</p>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Max Entries</label>
              <input
                type="number"
                min={1}
                max={50}
                value={form.maxEntries}
                onChange={(e) => setForm({ ...form, maxEntries: e.target.value })}
                placeholder="10"
                className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
              />
              <p className="text-xs text-gray-400 mt-1">Maximum number of events to return (1–50, default: 10).</p>
            </div>
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Source / Provider</label>
              <input
                type="text"
                value={form.source}
                onChange={(e) => setForm({ ...form, source: e.target.value })}
                placeholder="e.g., Microsoft-Windows-Kernel-General"
                autoComplete="off"
                className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
              />
              <p className="text-xs text-gray-400 mt-1">Filter by event provider/source name. Leave empty for all sources.</p>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Message Filter</label>
              <input
                type="text"
                value={form.messageFilter}
                onChange={(e) => setForm({ ...form, messageFilter: e.target.value })}
                placeholder="e.g., *ESPProgress* (leave empty for no filter)"
                autoComplete="off"
                className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
              />
              <p className="text-xs text-gray-400 mt-1">Filter by message text. Use * as wildcard prefix/suffix.</p>
            </div>
          </div>
        </div>
      )}

      {/* File: optional parameters */}
      {form.collectorType === "file" && (
        <div>
          <label className="flex items-center gap-2 cursor-pointer">
            <input
              type="checkbox"
              checked={form.readContent}
              onChange={(e) => setForm({ ...form, readContent: e.target.checked })}
              className="w-4 h-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
            />
            <span className="text-sm font-medium text-gray-700">Read file content</span>
          </label>
          <p className="text-xs text-gray-400 mt-1 ml-6">Read the last 4000 characters of the file (only files &lt;50 KB). Useful for log files and setup logs.</p>
        </div>
      )}

      {/* LogParser: required pattern + optional settings */}
      {form.collectorType === "logparser" && (
        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Pattern <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              value={form.logPattern}
              onChange={(e) => setForm({ ...form, logPattern: e.target.value })}
              placeholder={`e.g., (?<action>Install|Uninstall).*(?<appName>[A-Za-z0-9_-]+)`}
              autoComplete="off"
              className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 font-mono focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
            />
            <p className="text-xs text-gray-400 mt-1">Regex with named capture groups. Each match emits a separate event. Named groups become event data fields.</p>
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Max Lines</label>
              <input
                type="number"
                min={1}
                max={10000}
                value={form.maxLines}
                onChange={(e) => setForm({ ...form, maxLines: e.target.value })}
                placeholder="1000"
                className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
              />
              <p className="text-xs text-gray-400 mt-1">Max lines to parse per execution (default: 1000).</p>
            </div>
            <div className="flex flex-col justify-center">
              <label className="flex items-center gap-2 cursor-pointer mt-4">
                <input
                  type="checkbox"
                  checked={form.trackPosition}
                  onChange={(e) => setForm({ ...form, trackPosition: e.target.checked })}
                  className="w-4 h-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
                />
                <span className="text-sm font-medium text-gray-700">Track position</span>
              </label>
              <p className="text-xs text-gray-400 mt-1 ml-6">Resume from last read position across executions (recommended).</p>
            </div>
          </div>
        </div>
      )}

      {/* Trigger */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Trigger</label>
        <select
          value={form.trigger}
          onChange={(e) => setForm({ ...form, trigger: e.target.value })}
          className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
        >
          {TRIGGERS.map((t) => (
            <option key={t} value={t}>{formatTrigger(t)}</option>
          ))}
        </select>
      </div>

      {/* Conditional Trigger Fields */}
      {form.trigger === "interval" && (
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Interval (seconds)</label>
          <input
            type="number"
            min={5}
            max={3600}
            value={form.intervalSeconds}
            onChange={(e) => setForm({ ...form, intervalSeconds: parseInt(e.target.value) || 60 })}
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          />
          <p className="text-xs text-gray-400 mt-1">How often to run this rule (5 - 3600 seconds)</p>
        </div>
      )}

      {form.trigger === "phase_change" && (
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Trigger Phase</label>
          <input
            type="text"
            value={form.triggerPhase}
            onChange={(e) => setForm({ ...form, triggerPhase: e.target.value })}
            placeholder="e.g., DeviceESP, AccountESP, DevicePreparation"
            autoComplete="off"
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          />
        </div>
      )}

      {form.trigger === "on_event" && (
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Trigger Event Type</label>
          <input
            type="text"
            value={form.triggerEventType}
            onChange={(e) => setForm({ ...form, triggerEventType: e.target.value })}
            placeholder="e.g., enrollment_complete, enrollment_failed, app_install_failed"
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
            value={form.outputEventType}
            onChange={(e) => setForm({ ...form, outputEventType: e.target.value })}
            placeholder="e.g., CustomNetworkStatus"
            autoComplete="off"
            className="w-full px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
          />
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Output Severity</label>
          <select
            value={form.outputSeverity}
            onChange={(e) => setForm({ ...form, outputSeverity: e.target.value })}
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
    </div>
  );

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        {/* Header */}
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <button
                  onClick={() => router.push("/dashboard")}
                  className="text-sm text-gray-600 hover:text-gray-900 mb-2 flex items-center"
                >
                  &larr; Back to Dashboard
                </button>
                <div>
                  <div className="flex items-center gap-4">
                    <h1 className="text-3xl font-bold text-gray-900">Gather Rules</h1>
                    <Link href="/docs#gather-rules" className="text-sm text-indigo-600 hover:text-indigo-800 flex items-center gap-1 mt-1">
                      <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6.253v13m0-13C10.832 5.477 9.246 5 7.5 5S4.168 5.477 3 6.253v13C4.168 18.477 5.754 18 7.5 18s3.332.477 4.5 1.253m0-13C13.168 5.477 14.754 5 16.5 5c1.747 0 3.332.477 4.5 1.253v13C19.832 18.477 18.247 18 16.5 18c-1.746 0-3.332.477-4.5 1.253" />
                      </svg>
                      Documentation
                    </Link>
                  </div>
                  <p className="text-sm text-gray-600 mt-1">Manage data collection rules for device enrollment</p>
                </div>
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
                  <div className="text-sm font-medium text-gray-500">Built-in / Community</div>
                  <div className="mt-1 text-2xl font-bold text-blue-600">{builtInCount + communityCount}</div>
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
                      <option value="community">Community</option>
                      <option value="custom">Custom</option>
                    </select>
                  </div>

                  <div className="flex items-center gap-2">
                  {/* Export All Button */}
                  <button
                    onClick={handleExportAll}
                    className="px-4 py-2 bg-gray-600 text-white rounded-lg hover:bg-gray-700 transition-colors flex items-center space-x-2 text-sm font-medium whitespace-nowrap"
                    title="Export all visible rules as JSON"
                  >
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                    </svg>
                    <span>Export All</span>
                  </button>

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
              </div>

              {/* Create Custom Rule Form */}
              {showCreateForm && (
                <div className="bg-white rounded-lg shadow">
                  <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-indigo-50 to-purple-50">
                    <div className="flex items-center justify-between">
                      <div className="flex items-center space-x-2">
                        <svg className="w-6 h-6 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
                        </svg>
                        <div>
                          <h2 className="text-xl font-semibold text-gray-900">Create Custom Rule</h2>
                          <p className="text-sm text-gray-500 mt-1">Define a new data collection rule for enrolled devices</p>
                        </div>
                      </div>
                      {/* JSON Mode Toggle */}
                      <div className="flex items-center bg-gray-100 rounded-lg p-1">
                        <button onClick={() => { setJsonModeCreate(false); setJsonError(null); }} className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${!jsonModeCreate ? "bg-white text-gray-900 shadow-sm" : "text-gray-500 hover:text-gray-700"}`}>Form</button>
                        <button onClick={() => { setJsonText(JSON.stringify(newRule, null, 2)); setJsonModeCreate(true); setJsonError(null); }} className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${jsonModeCreate ? "bg-white text-gray-900 shadow-sm" : "text-gray-500 hover:text-gray-700"}`}>JSON</button>
                      </div>
                    </div>
                  </div>
                  <div className="p-6">
                    {jsonModeCreate ? (
                      <div className="space-y-3">
                        <div className="flex items-center justify-between">
                          <p className="text-sm text-gray-600">Edit the rule as JSON. All fields are supported including <code className="bg-gray-100 px-1 rounded text-xs">parameters</code> for collector-specific options.</p>
                          <button type="button" onClick={() => {
                            try {
                              const parsed = JSON.parse(jsonText) as NewRuleForm;
                              if (!parsed.ruleId && !parsed.title) throw new Error("JSON must include at least ruleId and title");
                              setNewRule({ ...EMPTY_FORM, ...parsed });
                              setJsonModeCreate(false);
                              setJsonError(null);
                            } catch (e) {
                              setJsonError(e instanceof Error ? e.message : "Invalid JSON");
                            }
                          }} className="text-xs text-indigo-600 hover:text-indigo-800 font-medium whitespace-nowrap ml-4">Apply JSON &rarr;</button>
                        </div>
                        {jsonError && <p className="text-xs text-red-600 bg-red-50 border border-red-200 rounded px-3 py-2">{jsonError}</p>}
                        <textarea
                          value={jsonText}
                          onChange={(e) => { setJsonText(e.target.value); setJsonError(null); }}
                          rows={20}
                          spellCheck={false}
                          className="w-full px-4 py-3 border border-gray-300 rounded-lg text-sm font-mono text-gray-900 bg-gray-50 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 resize-y"
                        />
                      </div>
                    ) : (
                      renderFormFields(newRule, setNewRule, true)
                    )}

                    {/* Action Buttons */}
                    <div className="flex items-center justify-end space-x-3 pt-4 mt-5 border-t border-gray-200">
                      <button
                        onClick={() => {
                          setShowCreateForm(false);
                          setJsonModeCreate(false);
                          setJsonError(null);
                          setNewRule({ ...EMPTY_FORM });
                        }}
                        disabled={creating}
                        className="px-6 py-2 border border-gray-300 rounded-lg text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium"
                      >
                        Cancel
                      </button>
                      <button
                        onClick={() => {
                          if (jsonModeCreate) {
                            try {
                              const parsed = JSON.parse(jsonText) as NewRuleForm;
                              setJsonError(null);
                              handleCreateRule({ ...EMPTY_FORM, ...parsed });
                            } catch (e) {
                              setJsonError(e instanceof Error ? e.message : "Invalid JSON");
                            }
                          } else {
                            handleCreateRule();
                          }
                        }}
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
              <div className="space-y-3">
                <div className="flex items-center space-x-2 px-1">
                  <svg className="w-5 h-5 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" />
                  </svg>
                  <span className="text-sm text-gray-500">
                    {filteredRules.length} rule{filteredRules.length !== 1 ? "s" : ""} found
                    {(searchQuery || categoryFilter !== "all" || typeFilter !== "all") && " (filtered)"}
                  </span>
                </div>

                {filteredRules.length === 0 ? (
                  <div className="bg-white rounded-lg shadow p-8 text-center">
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
                  filteredRules.map((rule) => {
                    const isExpanded = expandedRuleId === rule.ruleId;
                    const isEditing = editingRuleId === rule.ruleId;
                    const catColor = CATEGORY_COLORS[rule.category] || { bg: "bg-gray-100", text: "text-gray-700" };
                    const canEdit = !rule.isBuiltIn && !rule.isCommunity;

                    return (
                      <div
                        key={rule.ruleId}
                        className={`bg-white rounded-lg shadow border transition-all ${
                          isExpanded ? "border-indigo-300 ring-1 ring-indigo-200" : "border-gray-200 hover:border-gray-300"
                        } ${!rule.enabled ? "opacity-60" : ""}`}
                      >
                        {/* Collapsed Header */}
                        <div
                          className="p-4 cursor-pointer select-none"
                          onClick={() => {
                            if (isEditing) return;
                            setExpandedRuleId(isExpanded ? null : rule.ruleId);
                            if (isExpanded && editingRuleId === rule.ruleId) {
                              setEditingRuleId(null);
                            }
                          }}
                        >
                          <div className="flex items-center space-x-4">
                            {/* Enable/Disable Toggle */}
                            <button
                              onClick={(e) => {
                                e.stopPropagation();
                                handleToggleRule(rule);
                              }}
                              disabled={togglingRule === rule.ruleId}
                              className={`relative inline-flex h-6 w-11 flex-shrink-0 items-center rounded-full transition-colors ${
                                togglingRule === rule.ruleId
                                  ? "opacity-50 cursor-not-allowed"
                                  : "cursor-pointer"
                              } ${rule.enabled ? "bg-emerald-500" : "bg-gray-300"}`}
                              title={rule.enabled ? "Disable rule" : "Enable rule"}
                            >
                              <span
                                className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                                  rule.enabled ? "translate-x-6" : "translate-x-1"
                                }`}
                              />
                            </button>

                            {/* Rule ID */}
                            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-mono font-medium bg-gray-100 text-gray-600 border border-gray-200 flex-shrink-0 hidden sm:inline-flex">
                              {rule.ruleId}
                            </span>

                            {/* Title */}
                            <div className="flex-1 min-w-0">
                              <h3 className="text-sm font-semibold text-gray-900 truncate">
                                {rule.title}
                              </h3>
                            </div>

                            {/* Category Badge */}
                            <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${catColor.bg} ${catColor.text} flex-shrink-0`}>
                              {rule.category.charAt(0).toUpperCase() + rule.category.slice(1)}
                            </span>

                            {/* Collector Type */}
                            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-50 text-gray-600 flex-shrink-0 hidden md:inline-flex">
                              {COLLECTOR_TYPE_LABELS[rule.collectorType] || rule.collectorType}
                            </span>

                            {/* Type Badge */}
                            <span
                              className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium flex-shrink-0 ${
                                rule.isBuiltIn
                                  ? "bg-blue-50 text-blue-600 border border-blue-200"
                                  : rule.isCommunity
                                  ? "bg-amber-50 text-amber-600 border border-amber-200"
                                  : "bg-purple-50 text-purple-600 border border-purple-200"
                              }`}
                            >
                              {rule.isBuiltIn ? "Built-in" : rule.isCommunity ? "Community" : "Custom"}
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
                        {isExpanded && !isEditing && (
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
                            {rule.tags && rule.tags.length > 0 && (
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
                            {rule.description && (
                              <div>
                                <h4 className="text-sm font-semibold text-gray-700 mb-1">Description</h4>
                                <p className="text-sm text-gray-600 leading-relaxed">{rule.description}</p>
                              </div>
                            )}

                            {/* Collection Details */}
                            <div>
                              <h4 className="text-sm font-semibold text-gray-700 mb-2">Collection Details</h4>
                              <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 space-y-3">
                                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-sm">
                                  <div>
                                    <span className="text-gray-500 font-medium">Collector Type:</span>{" "}
                                    <span className="text-gray-900 font-semibold">{COLLECTOR_TYPE_LABELS[rule.collectorType] || rule.collectorType}</span>
                                  </div>
                                  <div>
                                    <span className="text-gray-500 font-medium">Category:</span>{" "}
                                    {getCategoryBadge(rule.category)}
                                  </div>
                                </div>
                                <div className="text-sm">
                                  <span className="text-gray-500 font-medium">Target:</span>
                                  <code className="ml-2 px-2 py-1 bg-gray-200 rounded text-xs font-mono text-gray-800 break-all">
                                    {rule.target}
                                  </code>
                                </div>
                                {rule.parameters && Object.keys(rule.parameters).length > 0 && (
                                  <div className="text-sm">
                                    <span className="text-gray-500 font-medium">Parameters:</span>
                                    <div className="mt-1 space-y-1">
                                      {Object.entries(rule.parameters).map(([key, value]) => (
                                        <div key={key} className="flex items-start gap-2">
                                          <code className="px-1.5 py-0.5 bg-indigo-100 rounded text-xs font-mono text-indigo-700">{key}</code>
                                          <span className="text-gray-400">=</span>
                                          <code className="px-1.5 py-0.5 bg-gray-200 rounded text-xs font-mono text-gray-700 break-all">{value}</code>
                                        </div>
                                      ))}
                                    </div>
                                  </div>
                                )}
                              </div>
                            </div>

                            {/* Trigger Details */}
                            <div>
                              <h4 className="text-sm font-semibold text-gray-700 mb-2">Trigger</h4>
                              <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 text-sm">
                                <div className="flex items-center gap-2">
                                  <svg className="w-4 h-4 text-amber-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
                                  </svg>
                                  <span className="font-semibold text-gray-900">{formatTrigger(rule.trigger)}</span>
                                  {rule.trigger === "interval" && rule.intervalSeconds && (
                                    <span className="text-gray-500">every {rule.intervalSeconds} seconds</span>
                                  )}
                                  {rule.trigger === "phase_change" && rule.triggerPhase && (
                                    <span className="text-gray-500">
                                      on phase: <code className="px-1 bg-gray-200 rounded text-xs">{rule.triggerPhase}</code>
                                    </span>
                                  )}
                                  {rule.trigger === "on_event" && rule.triggerEventType && (
                                    <span className="text-gray-500">
                                      on event: <code className="px-1 bg-gray-200 rounded text-xs">{rule.triggerEventType}</code>
                                    </span>
                                  )}
                                </div>
                              </div>
                            </div>

                            {/* Output */}
                            <div>
                              <h4 className="text-sm font-semibold text-gray-700 mb-2">Output</h4>
                              <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 text-sm">
                                <div className="flex flex-wrap items-center gap-4">
                                  <div>
                                    <span className="text-gray-500 font-medium">Event Type:</span>{" "}
                                    <code className="px-1.5 py-0.5 bg-gray-200 rounded text-xs font-mono text-gray-800">{rule.outputEventType}</code>
                                  </div>
                                  <div>
                                    <span className="text-gray-500 font-medium">Severity:</span>{" "}
                                    <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                                      rule.outputSeverity.toLowerCase() === "error" || rule.outputSeverity.toLowerCase() === "critical"
                                        ? "bg-red-100 text-red-700"
                                        : rule.outputSeverity.toLowerCase() === "warning"
                                        ? "bg-yellow-100 text-yellow-700"
                                        : "bg-blue-100 text-blue-700"
                                    }`}>
                                      {rule.outputSeverity.charAt(0).toUpperCase() + rule.outputSeverity.slice(1)}
                                    </span>
                                  </div>
                                </div>
                              </div>
                            </div>

                            {/* Actions */}
                            <div className="flex items-center justify-end space-x-3 pt-4 border-t border-gray-200">
                              <button
                                onClick={() => handleExportSingle(rule)}
                                className="px-4 py-2 text-sm bg-gray-100 text-gray-700 rounded-lg hover:bg-gray-200 transition-colors flex items-center space-x-2"
                                title="Export rule as JSON"
                              >
                                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                                </svg>
                                <span>Export</span>
                              </button>
                              {canEdit && (
                                <button
                                  onClick={() => startEditing(rule)}
                                  className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 transition-colors flex items-center space-x-2"
                                  title="Edit rule"
                                >
                                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                                  </svg>
                                  <span>Edit</span>
                                </button>
                              )}
                              {!rule.isBuiltIn && !rule.isCommunity && (
                                <button
                                  onClick={() => handleDeleteRule(rule)}
                                  disabled={deletingRule === rule.ruleId}
                                  className="px-4 py-2 text-sm bg-red-600 text-white rounded-lg hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
                                  title="Delete rule"
                                >
                                  {deletingRule === rule.ruleId ? (
                                    <>
                                      <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
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
                              )}
                            </div>
                          </div>
                        )}

                        {/* Edit Form (replaces detail view when editing) */}
                        {isExpanded && isEditing && (
                          <div className="border-t border-gray-200 p-6">
                            <div className="flex items-center justify-between mb-4">
                              <div className="flex items-center space-x-2">
                                <svg className="w-5 h-5 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                                </svg>
                                <h4 className="text-sm font-semibold text-gray-900">
                                  Editing: {rule.ruleId}
                                </h4>
                              </div>
                              {/* JSON Mode Toggle */}
                              <div className="flex items-center bg-gray-100 rounded-lg p-1">
                                <button onClick={() => { setJsonModeEdit(false); setJsonError(null); }} className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${!jsonModeEdit ? "bg-white text-gray-900 shadow-sm" : "text-gray-500 hover:text-gray-700"}`}>Form</button>
                                <button onClick={() => { setJsonText(JSON.stringify(editForm, null, 2)); setJsonModeEdit(true); setJsonError(null); }} className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${jsonModeEdit ? "bg-white text-gray-900 shadow-sm" : "text-gray-500 hover:text-gray-700"}`}>JSON</button>
                              </div>
                            </div>

                            {jsonModeEdit ? (
                              <div className="space-y-3">
                                <div className="flex items-center justify-between">
                                  <p className="text-sm text-gray-600">Edit the rule as JSON. All fields are supported including <code className="bg-gray-100 px-1 rounded text-xs">parameters</code> for collector-specific options.</p>
                                  <button type="button" onClick={() => {
                                    try {
                                      const parsed = JSON.parse(jsonText) as NewRuleForm;
                                      if (!parsed.title) throw new Error("JSON must include title");
                                      setEditForm({ ...editForm, ...parsed });
                                      setJsonModeEdit(false);
                                      setJsonError(null);
                                    } catch (e) {
                                      setJsonError(e instanceof Error ? e.message : "Invalid JSON");
                                    }
                                  }} className="text-xs text-indigo-600 hover:text-indigo-800 font-medium whitespace-nowrap ml-4">Apply JSON &rarr;</button>
                                </div>
                                {jsonError && <p className="text-xs text-red-600 bg-red-50 border border-red-200 rounded px-3 py-2">{jsonError}</p>}
                                <textarea
                                  value={jsonText}
                                  onChange={(e) => { setJsonText(e.target.value); setJsonError(null); }}
                                  rows={20}
                                  spellCheck={false}
                                  className="w-full px-4 py-3 border border-gray-300 rounded-lg text-sm font-mono text-gray-900 bg-gray-50 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 resize-y"
                                />
                              </div>
                            ) : (
                              renderFormFields(editForm, setEditForm, false)
                            )}

                            {/* Action Buttons */}
                            <div className="flex items-center justify-end space-x-3 pt-4 mt-5 border-t border-gray-200">
                              <button
                                onClick={() => { setEditingRuleId(null); setJsonModeEdit(false); setJsonError(null); }}
                                disabled={saving}
                                className="px-6 py-2 border border-gray-300 rounded-lg text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium"
                              >
                                Cancel
                              </button>
                              <button
                                onClick={() => {
                                  if (jsonModeEdit) {
                                    try {
                                      const parsed = JSON.parse(jsonText) as NewRuleForm;
                                      setJsonError(null);
                                      handleSaveEdit(rule, { ...editForm, ...parsed });
                                    } catch (e) {
                                      setJsonError(e instanceof Error ? e.message : "Invalid JSON");
                                    }
                                  } else {
                                    handleSaveEdit(rule);
                                  }
                                }}
                                disabled={saving}
                                className="px-6 py-2 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2 text-sm font-medium"
                              >
                                {saving ? (
                                  <>
                                    <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                                    <span>Saving...</span>
                                  </>
                                ) : (
                                  <span>Save Changes</span>
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
            </div>
          )}
        </main>
      </div>
    </ProtectedRoute>
  );
}
