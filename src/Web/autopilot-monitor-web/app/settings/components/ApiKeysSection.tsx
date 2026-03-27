"use client";

import { useState, useEffect, useCallback } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface ApiKey {
  keyId: string;
  label: string;
  scope: string;
  tenantId: string;
  createdBy: string;
  createdAt: string;
  expiresAt: string | null;
  isActive: boolean;
  requestCount: number;
}

interface ApiKeysSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (e: string | null) => void;
  setSuccessMessage: (m: string | null) => void;
  isGlobalAdmin?: boolean;  // if true, show scope selector and load /global/api-keys
  tenants?: { tenantId: string; displayName?: string }[];  // for GA to pick tenant when creating tenant-scoped key
}

export default function ApiKeysSection({
  getAccessToken, setError, setSuccessMessage, isGlobalAdmin = false, tenants = []
}: ApiKeysSectionProps) {
  const [keys, setKeys] = useState<ApiKey[]>([]);
  const [loading, setLoading] = useState(false);
  const [creatingKey, setCreatingKey] = useState(false);
  const [revokingKey, setRevokingKey] = useState<string | null>(null);
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [newLabel, setNewLabel] = useState("");
  const [newScope, setNewScope] = useState<"tenant" | "global">("tenant");
  const [newTenantId, setNewTenantId] = useState("");
  const [newExpiryDays, setNewExpiryDays] = useState<string>("");
  const [createdKey, setCreatedKey] = useState<{ rawKey: string; label: string } | null>(null);
  const [copiedKey, setCopiedKey] = useState(false);
  const [confirmRevokeId, setConfirmRevokeId] = useState<string | null>(null);

  const loadKeys = useCallback(async () => {
    try {
      setLoading(true);
      const endpoint = isGlobalAdmin ? api.apiKeys.globalList() : api.apiKeys.list();
      const res = await authenticatedFetch(endpoint, getAccessToken);
      if (!res.ok) throw new Error(`Failed to load API keys: ${res.statusText}`);
      const data = await res.json();
      setKeys(data.keys ?? []);
    } catch (err) {
      if (err instanceof TokenExpiredError) return;
      setError(err instanceof Error ? err.message : "Failed to load API keys");
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, isGlobalAdmin, setError]);

  useEffect(() => { loadKeys(); }, [loadKeys]);

  const handleCreate = async () => {
    if (!newLabel.trim()) { setError("Label is required"); return; }
    try {
      setCreatingKey(true);
      setError(null);
      const body: Record<string, unknown> = { label: newLabel.trim(), scope: newScope };
      if (newExpiryDays && parseInt(newExpiryDays) > 0) body.expiresInDays = parseInt(newExpiryDays);
      if (newScope === "tenant" && newTenantId) body.tenantIdOverride = newTenantId;
      const res = await authenticatedFetch(api.apiKeys.list(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      if (!res.ok) {
        const d = await res.json().catch(() => ({}));
        throw new Error(d.message ?? `Failed: ${res.statusText}`);
      }
      const data = await res.json();
      setCreatedKey({ rawKey: data.rawKey, label: newLabel.trim() });
      setNewLabel(""); setNewScope("tenant"); setNewTenantId(""); setNewExpiryDays(""); setShowCreateForm(false);
      await loadKeys();
    } catch (err) {
      if (err instanceof TokenExpiredError) return;
      setError(err instanceof Error ? err.message : "Failed to create API key");
    } finally {
      setCreatingKey(false);
    }
  };

  const handleRevoke = async (keyId: string) => {
    try {
      setRevokingKey(keyId);
      setError(null);
      const res = await authenticatedFetch(api.apiKeys.delete(keyId), getAccessToken, { method: "DELETE" });
      if (!res.ok) throw new Error(`Failed to revoke: ${res.statusText}`);
      setSuccessMessage("API key revoked successfully");
      await loadKeys();
    } catch (err) {
      if (err instanceof TokenExpiredError) return;
      setError(err instanceof Error ? err.message : "Failed to revoke API key");
    } finally {
      setRevokingKey(null);
      setConfirmRevokeId(null);
    }
  };

  const copyToClipboard = () => {
    if (createdKey) {
      navigator.clipboard.writeText(createdKey.rawKey).then(() => {
        setCopiedKey(true);
        setTimeout(() => setCopiedKey(false), 2000);
      });
    }
  };

  const formatDate = (d: string | null) => d ? new Date(d).toLocaleDateString() : "Never";
  const isExpired = (expiresAt: string | null) => expiresAt ? new Date(expiresAt) < new Date() : false;

  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-indigo-50 to-blue-50">
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-2">
            <svg className="w-6 h-6 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 7a2 2 0 012 2m4 0a6 6 0 01-7.743 5.743L11 17H9v2H7v2H4a1 1 0 01-1-1v-2.586a1 1 0 01.293-.707l5.964-5.964A6 6 0 1121 9z" />
            </svg>
            <div>
              <h2 className="text-lg font-medium text-gray-900">API Keys</h2>
              <p className="text-sm text-gray-500">Manage API keys for AI agent and MCP server access</p>
            </div>
          </div>
          <button
            onClick={() => setShowCreateForm(!showCreateForm)}
            className="px-4 py-2 bg-indigo-600 text-white text-sm font-medium rounded-md hover:bg-indigo-700 transition-colors"
          >
            + New API Key
          </button>
        </div>
      </div>

      {/* New key one-time display */}
      {createdKey && (
        <div className="p-4 mx-6 mt-4 bg-green-50 border border-green-200 rounded-lg">
          <div className="flex items-start justify-between">
            <div className="flex-1">
              <p className="text-sm font-medium text-green-800">API key created: <strong>{createdKey.label}</strong></p>
              <p className="text-xs text-green-600 mt-1">Copy this key now — it will not be shown again.</p>
              <div className="mt-2 flex items-center space-x-2">
                <code className="flex-1 px-3 py-2 bg-white border border-green-200 rounded text-xs font-mono text-gray-800 break-all">
                  {createdKey.rawKey}
                </code>
                <button
                  onClick={copyToClipboard}
                  className="px-3 py-2 bg-green-600 text-white text-xs rounded hover:bg-green-700 whitespace-nowrap"
                >
                  {copiedKey ? "Copied!" : "Copy"}
                </button>
              </div>
            </div>
            <button onClick={() => setCreatedKey(null)} className="ml-2 text-green-400 hover:text-green-600">
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>
        </div>
      )}

      {/* Create form */}
      {showCreateForm && (
        <div className="p-6 border-b border-gray-100 bg-gray-50">
          <h3 className="text-sm font-medium text-gray-700 mb-4">New API Key</h3>
          <div className="space-y-4">
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">Label</label>
              <input
                type="text"
                value={newLabel}
                onChange={(e) => setNewLabel(e.target.value)}
                placeholder="e.g. Claude Desktop Local"
                className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:ring-indigo-500 focus:border-indigo-500"
              />
            </div>
            {isGlobalAdmin && (
              <div>
                <label className="block text-xs font-medium text-gray-600 mb-1">Scope</label>
                <div className="flex space-x-4">
                  <label className="flex items-center space-x-2 cursor-pointer">
                    <input type="radio" value="tenant" checked={newScope === "tenant"} onChange={() => setNewScope("tenant")} className="text-indigo-600" />
                    <span className="text-sm text-gray-700">Tenant-scoped</span>
                  </label>
                  <label className="flex items-center space-x-2 cursor-pointer">
                    <input type="radio" value="global" checked={newScope === "global"} onChange={() => setNewScope("global")} className="text-indigo-600" />
                    <span className="text-sm text-gray-700">Global (cross-tenant)</span>
                  </label>
                </div>
                {newScope === "global" && (
                  <p className="text-xs text-amber-600 mt-1">Global keys have access to all tenants. Use with care.</p>
                )}
              </div>
            )}
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">Expiry (days, leave blank for unlimited)</label>
              <input
                type="number"
                value={newExpiryDays}
                onChange={(e) => setNewExpiryDays(e.target.value)}
                placeholder="e.g. 365 (or blank for no expiry)"
                min="1"
                className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:ring-indigo-500 focus:border-indigo-500"
              />
            </div>
            <div className="flex space-x-2">
              <button
                onClick={handleCreate}
                disabled={creatingKey || !newLabel.trim()}
                className="px-4 py-2 bg-indigo-600 text-white text-sm font-medium rounded-md hover:bg-indigo-700 disabled:opacity-50 transition-colors"
              >
                {creatingKey ? "Creating..." : "Create Key"}
              </button>
              <button
                onClick={() => { setShowCreateForm(false); setNewLabel(""); setNewExpiryDays(""); }}
                className="px-4 py-2 border border-gray-300 text-gray-700 text-sm font-medium rounded-md hover:bg-gray-50"
              >
                Cancel
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Key list */}
      <div className="p-6">
        {loading ? (
          <p className="text-sm text-gray-500">Loading API keys...</p>
        ) : keys.length === 0 ? (
          <p className="text-sm text-gray-500">No API keys yet. Create one to enable AI agent access.</p>
        ) : (
          <div className="space-y-3">
            {keys.map((key) => (
              <div
                key={key.keyId}
                className={`flex items-center justify-between p-4 border rounded-lg ${!key.isActive || isExpired(key.expiresAt) ? "border-red-100 bg-red-50" : "border-gray-200 bg-white"}`}
              >
                <div className="flex-1 min-w-0">
                  <div className="flex items-center space-x-2 flex-wrap gap-y-1">
                    <span className="text-sm font-medium text-gray-900">{key.label}</span>
                    <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${key.scope === "global" ? "bg-purple-100 text-purple-800" : "bg-blue-100 text-blue-800"}`}>
                      {key.scope === "global" ? "Global" : "Tenant"}
                    </span>
                    {!key.isActive && (
                      <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-red-100 text-red-800">Revoked</span>
                    )}
                    {key.isActive && isExpired(key.expiresAt) && (
                      <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-orange-100 text-orange-800">Expired</span>
                    )}
                  </div>
                  <div className="mt-1 text-xs text-gray-500 space-x-3">
                    <span>Created by {key.createdBy || "unknown"}</span>
                    <span>•</span>
                    <span>Created {formatDate(key.createdAt)}</span>
                    <span>•</span>
                    <span>Expires {formatDate(key.expiresAt)}</span>
                    <span>•</span>
                    <span>{key.requestCount.toLocaleString()} requests</span>
                    {isGlobalAdmin && key.scope === "tenant" && (
                      <><span>•</span><span>Tenant: {key.tenantId}</span></>
                    )}
                  </div>
                </div>
                <div className="ml-4 flex-shrink-0">
                  {confirmRevokeId === key.keyId ? (
                    <div className="flex items-center space-x-2">
                      <span className="text-xs text-red-600">Revoke?</span>
                      <button
                        onClick={() => handleRevoke(key.keyId)}
                        disabled={revokingKey === key.keyId}
                        className="px-2 py-1 bg-red-600 text-white text-xs rounded hover:bg-red-700 disabled:opacity-50"
                      >
                        {revokingKey === key.keyId ? "..." : "Yes"}
                      </button>
                      <button
                        onClick={() => setConfirmRevokeId(null)}
                        className="px-2 py-1 border border-gray-300 text-gray-600 text-xs rounded hover:bg-gray-50"
                      >
                        No
                      </button>
                    </div>
                  ) : (
                    <button
                      onClick={() => setConfirmRevokeId(key.keyId)}
                      className="px-3 py-1 border border-red-200 text-red-600 text-xs rounded hover:bg-red-50 transition-colors"
                    >
                      Revoke
                    </button>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Rate limit info */}
      <div className="px-6 pb-6">
        <p className="text-xs text-gray-400">Rate limits: tenant-scoped keys 60 req/min · global-scoped keys 120 req/min</p>
      </div>
    </div>
  );
}
