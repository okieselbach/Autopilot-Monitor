"use client";

import { useState } from "react";
import { API_BASE_URL } from "@/lib/config";
import { TenantConfiguration } from "./TenantManagementSection";
import { TenantSearchSelect } from "./TenantSearchSelect";

interface BlockedDevice {
  tenantId: string;
  serialNumber: string;
  blockedAt: string;
  unblockAt: string;
  blockedByEmail: string;
  durationHours: number;
  reason?: string;
  action?: string;
}

interface DeviceBlockSectionProps {
  tenants: TenantConfiguration[];
  maxSessionWindowHours: number;
  setMaxSessionWindowHours: (value: number) => void;
  maintenanceBlockDurationHours: number;
  setMaintenanceBlockDurationHours: (value: number) => void;
  savingConfig: boolean;
  adminConfigExists: boolean;
  onSaveAdminConfig: () => Promise<void>;
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

export function DeviceBlockSection({
  tenants,
  maxSessionWindowHours,
  setMaxSessionWindowHours,
  maintenanceBlockDurationHours,
  setMaintenanceBlockDurationHours,
  savingConfig,
  adminConfigExists,
  onSaveAdminConfig,
  getAccessToken,
  setError,
  setSuccessMessage,
}: DeviceBlockSectionProps) {
  const [blockSerialNumber, setBlockSerialNumber] = useState("");
  const [blockTenantId, setBlockTenantId] = useState("");
  const [blockDurationHours, setBlockDurationHours] = useState(12);
  const [blockReason, setBlockReason] = useState("");
  const [blockingDevice, setBlockingDevice] = useState(false);
  const [blockedDevices, setBlockedDevices] = useState<BlockedDevice[]>([]);
  const [loadingBlockedDevices, setLoadingBlockedDevices] = useState(false);
  const [unblockingDevice, setUnblockingDevice] = useState<string | null>(null);
  const [blockAction, setBlockAction] = useState<"Block" | "Kill">("Block");
  const [killingDevice, setKillingDevice] = useState<string | null>(null);
  const [blockListTenantId, setBlockListTenantId] = useState("");

  const fetchBlockedDevices = async (tenantId: string) => {
    if (!tenantId) return;
    try {
      setLoadingBlockedDevices(true);
      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");
      const response = await fetch(`${API_BASE_URL}/api/devices/blocked?tenantId=${encodeURIComponent(tenantId)}`, {
        headers: { Authorization: `Bearer ${token}` },
      });
      if (!response.ok) throw new Error(`Failed to load blocked devices: ${response.statusText}`);
      const data = await response.json();
      setBlockedDevices(data.blocked ?? []);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load blocked devices");
    } finally {
      setLoadingBlockedDevices(false);
    }
  };

  const handleBlockDevice = async () => {
    if (!blockSerialNumber.trim() || !blockTenantId.trim()) return;

    if (blockAction === "Kill" && !confirm(
      `REMOTE KILL: This will permanently shut down the agent on device "${blockSerialNumber.trim()}" and remove all agent files. This cannot be undone. Continue?`
    )) return;

    try {
      setBlockingDevice(true);
      setError(null);
      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");
      const response = await fetch(`${API_BASE_URL}/api/devices/block`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
        body: JSON.stringify({
          tenantId: blockTenantId,
          serialNumber: blockSerialNumber.trim(),
          durationHours: blockDurationHours,
          reason: blockReason.trim() || undefined,
          action: blockAction,
        }),
      });
      const result = await response.json();
      if (!response.ok) throw new Error(result.message || response.statusText);
      setSuccessMessage(
        blockAction === "Kill"
          ? `Device ${blockSerialNumber.trim()} issued remote kill signal for ${blockDurationHours}h.`
          : `Device ${blockSerialNumber.trim()} blocked for ${blockDurationHours}h.`
      );
      setTimeout(() => setSuccessMessage(null), 4000);
      setBlockSerialNumber("");
      setBlockReason("");
      setBlockAction("Block");
      if (blockListTenantId === blockTenantId) await fetchBlockedDevices(blockTenantId);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to block device");
    } finally {
      setBlockingDevice(false);
    }
  };

  const handleUnblockDevice = async (tenantId: string, serialNumber: string) => {
    try {
      setUnblockingDevice(serialNumber);
      setError(null);
      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");
      const response = await fetch(
        `${API_BASE_URL}/api/devices/block/${encodeURIComponent(serialNumber)}?tenantId=${encodeURIComponent(tenantId)}`,
        { method: "DELETE", headers: { Authorization: `Bearer ${token}` } }
      );
      const result = await response.json();
      if (!response.ok) throw new Error(result.message || response.statusText);
      setSuccessMessage(`Device ${serialNumber} unblocked.`);
      setTimeout(() => setSuccessMessage(null), 3000);
      setBlockedDevices((prev) => prev.filter((d) => d.serialNumber !== serialNumber || d.tenantId !== tenantId));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to unblock device");
    } finally {
      setUnblockingDevice(null);
    }
  };

  const handleKillDevice = async (device: BlockedDevice) => {
    if (!confirm(
      `REMOTE KILL: This will permanently shut down the agent on device "${device.serialNumber}" and remove all agent files. This cannot be undone. Continue?`
    )) return;

    try {
      setKillingDevice(device.serialNumber);
      setError(null);
      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");
      const remainingHours = Math.max(1, Math.ceil((new Date(device.unblockAt).getTime() - Date.now()) / 3600000));
      const response = await fetch(`${API_BASE_URL}/api/devices/block`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
        body: JSON.stringify({
          tenantId: device.tenantId,
          serialNumber: device.serialNumber,
          durationHours: remainingHours,
          reason: device.reason || undefined,
          action: "Kill",
        }),
      });
      const result = await response.json();
      if (!response.ok) throw new Error(result.message || response.statusText);
      setSuccessMessage(`Device ${device.serialNumber} upgraded to kill signal.`);
      setTimeout(() => setSuccessMessage(null), 4000);
      await fetchBlockedDevices(device.tenantId);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to send kill signal");
    } finally {
      setKillingDevice(null);
    }
  };

  return (
    <div className="bg-gradient-to-br from-red-50 to-orange-50 dark:from-gray-800 dark:to-gray-800 border-2 border-red-300 dark:border-red-700 rounded-lg shadow-lg">
      <div className="p-6 border-b border-red-200 dark:border-red-700 bg-gradient-to-r from-red-100 to-orange-100 dark:from-red-900/40 dark:to-orange-900/40">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-red-600 dark:text-red-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-red-900 dark:text-red-100">Device Block Management</h2>
            <p className="text-sm text-red-600 dark:text-red-300 mt-1">Temporarily block a rogue device from sending data. The agent will stop uploading when it receives the block signal.</p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-6">
        {/* Maintenance Auto-Block Settings */}
        <div className="border border-red-200 dark:border-red-700 rounded-lg p-4 bg-red-50/50 dark:bg-red-900/10">
          <div className="flex items-center space-x-2 mb-3">
            <svg className="w-4 h-4 text-red-500 dark:text-red-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <h3 className="text-sm font-semibold text-red-900 dark:text-red-100">Maintenance Auto-Block Settings</h3>
          </div>
          <p className="text-xs text-red-600 dark:text-red-400 mb-4">
            The nightly maintenance function automatically blocks devices that are still actively sending data beyond the configured window. Sessions with a <code className="bg-red-100 dark:bg-red-900/40 px-1 rounded">LastEventAt</code> timestamp within the window <em>and</em> a <code className="bg-red-100 dark:bg-red-900/40 px-1 rounded">StartedAt</code> older than the window are flagged – regardless of session status.
          </p>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 mb-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                Max Session Window (Hours)
              </label>
              <input
                type="number"
                min={0}
                max={168}
                value={maxSessionWindowHours}
                onChange={(e) => setMaxSessionWindowHours(parseInt(e.target.value) || 0)}
                className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
              />
              <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">0 = disabled</p>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                Maintenance Block Duration (Hours)
              </label>
              <input
                type="number"
                min={1}
                max={720}
                value={maintenanceBlockDurationHours}
                onChange={(e) => setMaintenanceBlockDurationHours(parseInt(e.target.value) || 12)}
                className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
              />
              <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">Applied by the maintenance function</p>
            </div>
          </div>
          <button
            onClick={onSaveAdminConfig}
            disabled={savingConfig || !adminConfigExists}
            className="px-4 py-2 bg-red-600 text-white rounded-lg text-sm font-medium hover:bg-red-700 disabled:opacity-50 flex items-center space-x-2"
          >
            {savingConfig ? (
              <><div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div><span>Saving...</span></>
            ) : (
              <span>Save Maintenance Settings</span>
            )}
          </button>
        </div>

        {/* Block / Kill a device form */}
        <div>
          <h3 className="text-sm font-semibold text-red-900 dark:text-red-100 mb-3">Block or Kill a Device</h3>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Serial Number</label>
              <input
                type="text"
                placeholder="e.g. 1234ABCD"
                value={blockSerialNumber}
                onChange={(e) => setBlockSerialNumber(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Tenant ID</label>
              <TenantSearchSelect
                tenants={tenants}
                value={blockTenantId}
                onChange={setBlockTenantId}
                focusRingClass="focus:ring-red-500 focus:border-red-500"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Action</label>
              <select
                value={blockAction}
                onChange={(e) => setBlockAction(e.target.value as "Block" | "Kill")}
                className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
              >
                <option value="Block">Block (stop uploads)</option>
                <option value="Kill">Kill (remote shutdown)</option>
              </select>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Duration (Hours)</label>
              <input
                type="number"
                min={1}
                max={720}
                value={blockDurationHours}
                onChange={(e) => setBlockDurationHours(parseInt(e.target.value) || 12)}
                className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
              />
            </div>
            <div className="sm:col-span-2">
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Reason (optional)</label>
              <input
                type="text"
                placeholder="e.g. Excessive data volume"
                value={blockReason}
                onChange={(e) => setBlockReason(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
              />
            </div>
          </div>
          {blockAction === "Kill" && (
            <div className="mt-3 p-3 bg-red-100 dark:bg-red-900/40 border border-red-300 dark:border-red-700 rounded-lg">
              <p className="text-xs text-red-800 dark:text-red-200 font-medium">
                The agent will execute its self-destruct routine: remove the Scheduled Task and delete all agent files. This is irreversible once the agent picks up the signal.
              </p>
            </div>
          )}
          <button
            onClick={handleBlockDevice}
            disabled={blockingDevice || !blockSerialNumber.trim() || !blockTenantId}
            className={`mt-4 px-4 py-2 text-white rounded-lg text-sm font-medium disabled:opacity-50 flex items-center space-x-2 ${
              blockAction === "Kill"
                ? "bg-red-800 hover:bg-red-900 dark:bg-red-700 dark:hover:bg-red-800"
                : "bg-red-600 hover:bg-red-700"
            }`}
          >
            {blockingDevice ? (
              <><div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div><span>{blockAction === "Kill" ? "Sending Kill..." : "Blocking..."}</span></>
            ) : (
              <span>{blockAction === "Kill" ? "Send Kill Signal" : "Block Device"}</span>
            )}
          </button>
        </div>

        {/* View active blocks for a tenant */}
        <div className="border-t border-red-200 dark:border-red-700 pt-6">
          <h3 className="text-sm font-semibold text-red-900 dark:text-red-100 mb-3">Active Blocks</h3>
          <div className="flex items-end gap-3 mb-4">
            <div className="flex-1">
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Tenant</label>
              <TenantSearchSelect
                tenants={tenants}
                value={blockListTenantId}
                onChange={(id) => { setBlockListTenantId(id); if (id) fetchBlockedDevices(id); }}
                focusRingClass="focus:ring-red-500 focus:border-red-500"
              />
            </div>
            <button
              onClick={() => fetchBlockedDevices(blockListTenantId)}
              disabled={!blockListTenantId || loadingBlockedDevices}
              className="px-4 py-2 bg-red-100 dark:bg-red-900/40 text-red-800 dark:text-red-200 border border-red-300 dark:border-red-600 rounded-lg text-sm font-medium hover:bg-red-200 disabled:opacity-50"
            >
              {loadingBlockedDevices ? "Loading..." : "Load"}
            </button>
          </div>
          {blockedDevices.length > 0 ? (
            <div className="overflow-x-auto">
              <table className="w-full text-sm text-left">
                <thead className="text-xs text-red-800 dark:text-red-200 uppercase bg-red-100 dark:bg-red-900/30">
                  <tr>
                    <th className="px-3 py-2">Serial Number</th>
                    <th className="px-3 py-2">Type</th>
                    <th className="px-3 py-2">Blocked Since</th>
                    <th className="px-3 py-2">Expires At</th>
                    <th className="px-3 py-2">Blocked By</th>
                    <th className="px-3 py-2">Reason</th>
                    <th className="px-3 py-2">Action</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-red-100 dark:divide-red-900/30">
                  {blockedDevices.map((d) => (
                    <tr key={d.serialNumber} className="bg-white dark:bg-gray-800">
                      <td className="px-3 py-2 font-mono font-medium text-gray-900 dark:text-gray-100">{d.serialNumber}</td>
                      <td className="px-3 py-2">
                        {d.action === "Kill" ? (
                          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-semibold bg-red-700 text-white">Kill</span>
                        ) : (
                          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-semibold bg-orange-200 dark:bg-orange-800 text-orange-800 dark:text-orange-200">Block</span>
                        )}
                      </td>
                      <td className="px-3 py-2 text-gray-600 dark:text-gray-400">{new Date(d.blockedAt).toLocaleString()}</td>
                      <td className="px-3 py-2 text-orange-600 dark:text-orange-400 font-medium">{new Date(d.unblockAt).toLocaleString()}</td>
                      <td className="px-3 py-2 text-gray-600 dark:text-gray-400">{d.blockedByEmail}</td>
                      <td className="px-3 py-2 text-gray-600 dark:text-gray-400">{d.reason || "\u2014"}</td>
                      <td className="px-3 py-2">
                        <div className="flex flex-col gap-1">
                          {d.action !== "Kill" && (
                            <button
                              onClick={() => handleKillDevice(d)}
                              disabled={killingDevice === d.serialNumber}
                              className="px-3 py-1 bg-red-800 text-white rounded text-xs font-medium hover:bg-red-900 disabled:opacity-50"
                            >
                              {killingDevice === d.serialNumber ? "Killing..." : "Kill"}
                            </button>
                          )}
                          <button
                            onClick={() => handleUnblockDevice(d.tenantId, d.serialNumber)}
                            disabled={unblockingDevice === d.serialNumber}
                            className="px-3 py-1 bg-green-600 text-white rounded text-xs font-medium hover:bg-green-700 disabled:opacity-50"
                          >
                            {unblockingDevice === d.serialNumber ? "Removing..." : "Remove"}
                          </button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : blockListTenantId && !loadingBlockedDevices ? (
            <p className="text-sm text-gray-500 dark:text-gray-400 italic">No active blocks for this tenant.</p>
          ) : null}
        </div>
      </div>
    </div>
  );
}
