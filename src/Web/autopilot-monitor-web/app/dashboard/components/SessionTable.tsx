"use client";

import { useRouter } from "next/navigation";
import { Session } from "../types";

interface SessionTableProps {
  sessions: Session[];
  filteredSessions: Session[];
  sortedSessions: Session[];
  paginatedSessions: Session[];
  searchQuery: string;
  onSearchQueryChange: (query: string) => void;
  statusFilter: string | null;
  onStatusFilterChange: (status: string | null) => void;
  sortColumn: keyof Session | null;
  sortDirection: "asc" | "desc";
  onSort: (column: keyof Session) => void;
  currentPage: number;
  totalPages: number;
  onPreviousPage: () => void;
  onNextPage: () => void;
  adminMode: boolean;
  galacticAdminMode: boolean;
  blockedDevicesSet: Set<string>;
  isPreviewBlocked: boolean;
  user: { isGalacticAdmin?: boolean } | null;
  onDeleteSession: (sessionId: string, tenantId: string, deviceName?: string) => void;
  onBlockDevice: (serialNumber: string, tenantId: string, deviceName?: string) => void;
}

export function SessionTable({
  sessions,
  filteredSessions,
  sortedSessions,
  paginatedSessions,
  searchQuery,
  onSearchQueryChange,
  statusFilter,
  onStatusFilterChange,
  sortColumn,
  sortDirection,
  onSort,
  currentPage,
  totalPages,
  onPreviousPage,
  onNextPage,
  adminMode,
  galacticAdminMode,
  blockedDevicesSet,
  isPreviewBlocked,
  user,
  onDeleteSession,
  onBlockDevice,
}: SessionTableProps) {
  const router = useRouter();

  return (
    <div className="mt-8 bg-white shadow rounded-lg p-6">
      <div className="flex items-center justify-between mb-4 gap-3 flex-wrap">
        <h2 className="text-xl font-semibold text-gray-900">
          Sessions ({sessions.length})
          {filteredSessions.length !== sessions.length && (
            <span className="text-sm text-gray-500 ml-2">
              ({filteredSessions.length} filtered)
            </span>
          )}
        </h2>
        {isPreviewBlocked ? (
          <span className="inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-semibold bg-amber-100 text-amber-800 border border-amber-200 shrink-0">
            <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            Preview approval pending
          </span>
        ) : (
          <span className="inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-semibold bg-green-100 text-green-800 border border-green-200 shrink-0">
            <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M5 13l4 4L19 7" />
            </svg>
            Preview approved
          </span>
        )}
      </div>

      {/* Search Input */}
      <div className="mb-4 relative">
        <input
          type="text"
          placeholder="Search by device, serial, model, status, session ID, or duration (e.g., >30 for >30min)"
          value={searchQuery}
          onChange={(e) => onSearchQueryChange(e.target.value)}
          className="w-full px-4 py-2 pr-10 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
        />
        {searchQuery && (
          <button
            onClick={() => onSearchQueryChange("")}
            className="absolute right-3 top-2.5 text-gray-400 hover:text-gray-600 transition-colors"
            title="Clear search"
          >
            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        )}
      </div>

      {/* Status Filter Badges */}
      <div className="mb-4 flex items-center gap-2 flex-wrap">
        {(["Succeeded", "InProgress", "Pending", "Failed"] as const).map((status) => {
          const config: Record<string, { bg: string; bgActive: string; text: string; label: string }> = {
            Succeeded: { bg: "bg-green-50 text-green-700 border-green-200 hover:bg-green-100", bgActive: "bg-green-600 text-white border-green-600", text: "text-green-600", label: "Succeeded" },
            InProgress: { bg: "bg-blue-50 text-blue-700 border-blue-200 hover:bg-blue-100", bgActive: "bg-blue-600 text-white border-blue-600", text: "text-blue-600", label: "In Progress" },
            Pending: { bg: "bg-amber-50 text-amber-700 border-amber-200 hover:bg-amber-100", bgActive: "bg-amber-500 text-white border-amber-500", text: "text-amber-600", label: "Pending" },
            Failed: { bg: "bg-red-50 text-red-700 border-red-200 hover:bg-red-100", bgActive: "bg-red-600 text-white border-red-600", text: "text-red-600", label: "Failed" },
          };
          const c = config[status];
          const count = sessions.filter(s => s.status === status).length;
          const isActive = statusFilter === status;
          return (
            <button
              key={status}
              onClick={() => onStatusFilterChange(isActive ? null : status)}
              className={`inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-semibold border transition-colors cursor-pointer ${isActive ? c.bgActive : c.bg}`}
            >
              {c.label}
              <span className={`rounded-full px-1.5 py-0.5 text-[10px] font-bold leading-none ${isActive ? "bg-white/25" : "bg-black/5"}`}>
                {count}
              </span>
            </button>
          );
        })}
        {statusFilter && (
          <button
            onClick={() => onStatusFilterChange(null)}
            className="inline-flex items-center gap-1 px-2 py-1 rounded-full text-xs text-gray-500 hover:text-gray-700 hover:bg-gray-100 transition-colors cursor-pointer"
            title="Clear filter"
          >
            <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
            Clear
          </button>
        )}
      </div>

      <div className="overflow-x-auto">
        <table className="min-w-full divide-y divide-gray-200">
          <thead className="bg-gray-50">
            <tr>
              <SortableHeader column="deviceName" currentSort={sortColumn} direction={sortDirection} onSort={onSort}>
                Device
              </SortableHeader>
              {galacticAdminMode && (
                <th scope="col" className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  Tenant ID
                </th>
              )}
              <SortableHeader column="model" currentSort={sortColumn} direction={sortDirection} onSort={onSort}>
                Model
              </SortableHeader>
              <SortableHeader column="status" currentSort={sortColumn} direction={sortDirection} onSort={onSort}>
                Status
              </SortableHeader>
              <SortableHeader column="eventCount" currentSort={sortColumn} direction={sortDirection} onSort={onSort} className="px-3">
                Events
              </SortableHeader>
              <SortableHeader column="durationSeconds" currentSort={sortColumn} direction={sortDirection} onSort={onSort} className="px-3">
                Duration
              </SortableHeader>
              <SortableHeader column="startedAt" currentSort={sortColumn} direction={sortDirection} onSort={onSort} className="px-3">
                Started
              </SortableHeader>
              {adminMode && (
                <th scope="col" className="pl-3 pr-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                  Actions
                </th>
              )}
            </tr>
          </thead>
          <tbody className="bg-white divide-y divide-gray-200">
            {paginatedSessions.length === 0 ? (
              <tr>
                <td colSpan={(galacticAdminMode ? 1 : 0) + (adminMode ? 7 : 6)} className="px-6 py-8 text-center text-gray-500">
                  No sessions found matching your search.
                </td>
              </tr>
            ) : (
              paginatedSessions.map((session) => (
              <tr
                key={session.sessionId}
                onClick={() => router.push(`/sessions/${session.sessionId}`)}
                className="hover:bg-gray-50 cursor-pointer transition-colors"
              >
                <td className="px-6 py-4 whitespace-nowrap">
                  <div className="text-sm font-medium text-gray-900">
                    {session.deviceName || session.serialNumber}
                  </div>
                  <div className="text-sm text-gray-500">
                    {session.serialNumber}
                  </div>
                </td>
                {galacticAdminMode && (
                  <td className="px-6 py-4 whitespace-nowrap">
                    <button
                      onClick={(e) => {
                        e.stopPropagation();
                        navigator.clipboard.writeText(session.tenantId);
                      }}
                      className="group flex items-center gap-1 text-xs font-mono text-gray-600 hover:text-blue-600 transition-colors"
                      title={session.tenantId}
                    >
                      <span>{session.tenantId.split('-').slice(0, 2).join('-')}...</span>
                      <svg className="w-3 h-3 opacity-0 group-hover:opacity-100 transition-opacity flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
                      </svg>
                    </button>
                  </td>
                )}
                <td className="px-6 py-4 whitespace-nowrap">
                  <div className="text-sm font-medium text-gray-900">
                    {session.manufacturer || "Unknown manufacturer"}
                  </div>
                  <div className="text-sm text-gray-500">
                    {session.model || "Unknown model"}
                  </div>
                </td>
                <td className="px-6 py-4 whitespace-nowrap">
                  <div className="flex items-center gap-1.5">
                    <StatusBadge status={session.status} failureReason={session.failureReason} />
                    {session.isHybridJoin && (
                      <span
                        className="px-2 inline-flex items-center gap-1 text-xs leading-5 font-semibold rounded-full bg-purple-100 text-purple-800"
                        title="Hybrid Azure AD Join"
                      >
                        Hybrid
                      </span>
                    )}
                    {blockedDevicesSet.has(`${session.tenantId}:${session.serialNumber}`) && (
                      <span
                        className="px-2 inline-flex items-center gap-1 text-xs leading-5 font-semibold rounded-full bg-orange-100 text-orange-800"
                        title="Device is currently blocked"
                      >
                        <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
                        </svg>
                        Blocked
                      </span>
                    )}
                  </div>
                </td>
                <td className="px-3 py-4 whitespace-nowrap text-sm text-gray-500">
                  {session.eventCount}
                </td>
                <td className="px-3 py-4 whitespace-nowrap text-sm text-gray-500">
                  {Math.round(session.durationSeconds / 60)} min
                </td>
                <td className="px-3 py-4 whitespace-nowrap text-sm text-gray-500">
                  {new Date(session.startedAt).toLocaleString()}
                </td>
                {adminMode && (
                  <td className="pl-3 pr-4 py-4 whitespace-nowrap text-right text-sm font-medium">
                    <div className="flex items-center justify-end gap-2">
                      {galacticAdminMode && user?.isGalacticAdmin && (
                        <button
                          onClick={(e) => {
                            e.stopPropagation();
                            onBlockDevice(session.serialNumber, session.tenantId, session.deviceName || session.serialNumber);
                          }}
                          className="text-orange-500 hover:text-orange-700 transition-colors"
                          title="Device blocken"
                        >
                          <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
                          </svg>
                        </button>
                      )}
                      <button
                        onClick={(e) => {
                          e.stopPropagation();
                          onDeleteSession(session.sessionId, session.tenantId, session.deviceName || session.serialNumber);
                        }}
                        className="text-red-600 hover:text-red-900 transition-colors"
                        title="Session löschen"
                      >
                        <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                        </svg>
                      </button>
                    </div>
                  </td>
                )}
              </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {/* Pagination Controls */}
      {totalPages > 1 && (
        <div className="mt-4 flex items-center justify-between">
          <div className="text-sm text-gray-700">
            Page {currentPage} of {totalPages} ({sortedSessions.length} total sessions)
          </div>
          <div className="flex gap-2">
            <button
              onClick={onPreviousPage}
              disabled={currentPage === 1}
              className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            >
              ← Previous
            </button>
            <button
              onClick={onNextPage}
              disabled={currentPage === totalPages}
              className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            >
              Next →
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

function SortableHeader({
  column,
  currentSort,
  direction,
  onSort,
  children,
  className,
}: {
  column: keyof Session;
  currentSort: keyof Session | null;
  direction: "asc" | "desc";
  onSort: (column: keyof Session) => void;
  children: React.ReactNode;
  className?: string;
}) {
  const isActive = currentSort === column;

  return (
    <th
      onClick={() => onSort(column)}
      className={`py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider cursor-pointer hover:bg-gray-100 transition-colors select-none ${className ?? "px-6"}`}
    >
      <div className="flex items-center gap-2">
        {children}
        <span className="text-gray-400">
          {isActive ? (direction === "asc" ? "↑" : "↓") : "↕"}
        </span>
      </div>
    </th>
  );
}

function StatusBadge({ status, failureReason }: { status: string; failureReason?: string }) {
  const statusConfig = {
    InProgress: { color: "bg-blue-100 text-blue-800", text: "In Progress" },
    Pending: { color: "bg-amber-100 text-amber-800", text: "Pending" },
    Succeeded: { color: "bg-green-100 text-green-800", text: "Succeeded" },
    Failed: { color: "bg-red-100 text-red-800", text: "Failed" },
    Unknown: { color: "bg-gray-100 text-gray-800", text: "Unknown" },
  };

  const config = statusConfig[status as keyof typeof statusConfig] || statusConfig.Unknown;

  const isTimeout = status === "Failed" && failureReason && failureReason.toLowerCase().includes("timed out");

  return (
    <span
      className={`px-2 inline-flex items-center gap-1 text-xs leading-5 font-semibold rounded-full ${config.color}`}
      title={failureReason || undefined}
    >
      {config.text}
      {isTimeout && (
        <span title={failureReason} className="inline-flex items-center">
          ⏱️
        </span>
      )}
    </span>
  );
}
