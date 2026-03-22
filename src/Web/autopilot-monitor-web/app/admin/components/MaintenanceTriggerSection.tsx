"use client";

import { useState } from "react";
import { API_BASE_URL } from "@/lib/config";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface MaintenanceTriggerSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

export function MaintenanceTriggerSection({
  getAccessToken,
  setError,
  setSuccessMessage,
}: MaintenanceTriggerSectionProps) {
  const [triggeringMaintenance, setTriggeringMaintenance] = useState(false);
  const [maintenanceDate, setMaintenanceDate] = useState<string>("");

  const handleTriggerMaintenance = async () => {
    try {
      setTriggeringMaintenance(true);
      setError(null);
      setSuccessMessage(null);

      const queryParams = maintenanceDate ? `?date=${maintenanceDate}` : '';
      const response = await authenticatedFetch(`${API_BASE_URL}/api/maintenance/trigger${queryParams}`, getAccessToken, {
        method: "POST",
      });

      if (!response.ok) {
        throw new Error(`Failed to trigger maintenance: ${response.statusText}`);
      }

      const dateInfo = maintenanceDate ? ` for ${maintenanceDate}` : '';
      setSuccessMessage(`Maintenance job completed successfully${dateInfo}!`);

      setTimeout(() => setSuccessMessage(null), 5000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while triggering maintenance");
      } else {
        console.error("Error triggering maintenance:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to trigger maintenance job");
    } finally {
      setTriggeringMaintenance(false);
    }
  };

  return (
    <div className="bg-gradient-to-br from-purple-50 to-violet-50 dark:from-gray-800 dark:to-gray-800 border-2 border-purple-300 dark:border-purple-700 rounded-lg shadow-lg">
      <div className="p-6 border-b border-purple-200 dark:border-purple-700 bg-gradient-to-r from-purple-100 to-violet-100 dark:from-purple-900/40 dark:to-violet-900/40">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-purple-600 dark:text-purple-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-purple-900 dark:text-purple-100">Manual Maintenance Trigger</h2>
            <p className="text-sm text-purple-600 dark:text-purple-300 mt-1">Execute platform-wide maintenance operations</p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-4">
        <p className="text-sm text-purple-900 dark:text-gray-200">
          Runs all timer-triggered tasks plus additional backfill &amp; repair operations that only run on manual trigger.
        </p>
        <div className="space-y-3">
          <div>
            <p className="text-xs font-semibold text-purple-700 dark:text-purple-300 uppercase tracking-wide mb-1">Standard tasks (also run by 2h timer)</p>
            <ul className="text-sm text-purple-900 dark:text-gray-200 space-y-1 ml-4">
              <li className="flex items-start">
                <span className="text-purple-500 dark:text-purple-400 mr-2">&bull;</span>
                <span>Mark stalled sessions as timed out</span>
              </li>
              <li className="flex items-start">
                <span className="text-purple-500 dark:text-purple-400 mr-2">&bull;</span>
                <span>Aggregate metrics into historical snapshots</span>
              </li>
              <li className="flex items-start">
                <span className="text-purple-500 dark:text-purple-400 mr-2">&bull;</span>
                <span>Clean up old data based on retention policies</span>
              </li>
              <li className="flex items-start">
                <span className="text-purple-500 dark:text-purple-400 mr-2">&bull;</span>
                <span>Recompute platform-wide statistics</span>
              </li>
            </ul>
          </div>
          <div>
            <p className="text-xs font-semibold text-amber-700 dark:text-amber-300 uppercase tracking-wide mb-1">Manual-only tasks (backfill &amp; repair)</p>
            <ul className="text-sm text-purple-900 dark:text-gray-200 space-y-1 ml-4">
              <li className="flex items-start">
                <span className="text-amber-500 dark:text-amber-400 mr-2">&bull;</span>
                <span>Backfill sessions missing from SessionsIndex</span>
              </li>
              <li className="flex items-start">
                <span className="text-amber-500 dark:text-amber-400 mr-2">&bull;</span>
                <span>Clean up ghost SessionsIndex entries <span className="text-xs text-gray-500 dark:text-gray-400">(remove after 2026-06)</span></span>
              </li>
              <li className="flex items-start">
                <span className="text-amber-500 dark:text-amber-400 mr-2">&bull;</span>
                <span>Backfill tenant OnboardedAt dates <span className="text-xs text-gray-500 dark:text-gray-400">(remove when complete)</span></span>
              </li>
            </ul>
          </div>
        </div>
        <div className="bg-purple-50 dark:bg-gray-700 border border-purple-200 dark:border-purple-600 rounded-lg p-4">
          <label className="block text-sm font-medium text-purple-800 dark:text-purple-200 mb-1">
            Target Date (optional)
          </label>
          <input
            type="date"
            value={maintenanceDate}
            onChange={(e) => setMaintenanceDate(e.target.value)}
            max={new Date(Date.now() - 86400000).toISOString().split('T')[0]}
            className="w-full max-w-xs px-3 py-2 border border-purple-300 dark:border-purple-600 rounded-lg text-sm bg-white dark:bg-gray-600 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-purple-500 focus:border-purple-500"
          />
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-2">
            Leave empty to run the standard maintenance with automatic catch-up (aggregates any missed days within the last 7 days).
            Select a specific date to manually aggregate metrics for that day, e.g. to backfill data older than 7 days.
          </p>
        </div>
        <div className="bg-white dark:bg-gray-700 border border-purple-300 dark:border-purple-600 rounded-lg p-3">
          <div className="flex items-start space-x-2">
            <svg className="w-5 h-5 text-purple-600 dark:text-purple-400 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
            </svg>
            <p className="text-sm text-gray-800 dark:text-gray-200">
              <strong>Warning:</strong> This operation runs across all tenants and may take several minutes to complete.
              Use this only for testing or when immediate cleanup is needed.
            </p>
          </div>
        </div>
        <div className="flex justify-end pt-2">
          <button
            onClick={handleTriggerMaintenance}
            disabled={triggeringMaintenance}
            className="px-6 py-3 bg-gradient-to-r from-purple-600 to-violet-600 text-white rounded-lg hover:from-purple-700 hover:to-violet-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow-md hover:shadow-lg flex items-center space-x-2"
          >
            {triggeringMaintenance ? (
              <>
                <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-white"></div>
                <span>Running...</span>
              </>
            ) : (
              <>
                <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M14.752 11.168l-3.197-2.132A1 1 0 0010 9.87v4.263a1 1 0 001.555.832l3.197-2.132a1 1 0 000-1.664z" />
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <span>Run Now</span>
              </>
            )}
          </button>
        </div>
      </div>
    </div>
  );
}
