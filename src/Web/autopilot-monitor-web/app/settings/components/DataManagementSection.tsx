"use client";

interface DataManagementSectionProps {
  dataRetentionDays: number;
  setDataRetentionDays: (value: number) => void;
  sessionTimeoutHours: number;
  setSessionTimeoutHours: (value: number) => void;
}

export default function DataManagementSection({
  dataRetentionDays,
  setDataRetentionDays,
  sessionTimeoutHours,
  setSessionTimeoutHours,
}: DataManagementSectionProps) {
  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200">
        <h2 className="text-xl font-semibold text-gray-900">Data Management</h2>
        <p className="text-sm text-gray-500 mt-1">Configure data retention and session timeout policies</p>
      </div>
      <div className="p-6 space-y-6">
        {/* Data Retention Days */}
        <div>
          <label className="block">
            <span className="text-gray-700 font-medium">Data Retention Period (Days)</span>
            <p className="text-sm text-gray-500 mb-2">
              Sessions and events older than this will be automatically deleted by the daily maintenance job. Default: 90 days.
            </p>
            <input
              type="number"
              min="7"
              max="180"
              value={dataRetentionDays}
              onChange={(e) => setDataRetentionDays(parseInt(e.target.value) || 90)}
              className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
            />
            <p className="text-xs text-gray-400 mt-1">Minimum: 7 days, Maximum: 180 days</p>
          </label>
        </div>

        {/* Session Timeout Hours */}
        <div>
          <label className="block">
            <span className="text-gray-700 font-medium">Session Timeout (Hours)</span>
            <p className="text-sm text-gray-500 mb-2">
              Sessions in "InProgress" status longer than this will be marked as "Failed - Timed Out".
              This prevents stalled sessions from running indefinitely and skewing statistics.
              <br />
              <strong>Tip:</strong> Use the same value as your ESP (Enrollment Status Page) timeout for consistency.
            </p>
            <input
              type="number"
              min="1"
              max="12"
              value={sessionTimeoutHours}
              onChange={(e) => setSessionTimeoutHours(parseInt(e.target.value) || 5)}
              className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
            />
            <p className="text-xs text-gray-400 mt-1">Default: 5 hours (ESP default). Minimum: 1 hour, Maximum: 12 hours</p>
          </label>
        </div>
      </div>
    </div>
  );
}
