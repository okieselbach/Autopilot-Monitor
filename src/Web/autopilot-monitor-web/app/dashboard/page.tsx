"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useSignalR } from "../../contexts/SignalRContext";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import { StatsCard } from "./components/StatsCards";
import { WelcomeMessage } from "./components/WelcomeMessage";
import { SessionTable } from "./components/SessionTable";
import { DeleteConfirmModal, BlockConfirmModal } from "./components/ConfirmationModals";
import TipOfTheDay from "./components/TipOfTheDay";
import { useAdminMode } from "@/hooks/useAdminMode";
import { useDeleteSession } from "./hooks/useDeleteSession";
import { useBlockDevice } from "./hooks/useBlockDevice";
import { useTenantSecurityConfig } from "./hooks/useTenantSecurityConfig";
import { useTenantList } from "./hooks/useTenantList";
import { useDashboardFilters } from "./hooks/useDashboardFilters";
import { useDashboardSessions } from "./hooks/useDashboardSessions";

export default function Home() {
  const router = useRouter();
  const { user, logout, getAccessToken, isPreviewBlocked } = useAuth();
  const { addNotification } = useNotifications();
  const [apiStatus, setApiStatus] = useState<"unchecked" | "checking" | "healthy" | "error">("unchecked");
  const [tenantIdFilter, setTenantIdFilter] = useState("");
  const { adminMode, setAdminMode, globalAdminMode, setGlobalAdminMode } = useAdminMode();

  const signalR = useSignalR();
  const { tenantId } = useTenant();

  const {
    showBlockConfirm, sessionToBlock, blockingDevice, blockedDevicesSet, setBlockedDevicesSet,
    blockDevice, confirmBlock, cancelBlock,
  } = useBlockDevice(getAccessToken, addNotification, adminMode, globalAdminMode);

  const {
    sessions, loading, hasMore, loadingMore,
    refetch, refetchWith, loadMore, removeSession,
  } = useDashboardSessions({
    user, tenantId, globalAdminMode, tenantIdFilter, adminMode,
    getAccessToken, addNotification, setBlockedDevicesSet, signalR,
  });

  const {
    showDeleteConfirm, sessionToDelete,
    deleteSession, confirmDelete, cancelDelete,
  } = useDeleteSession(getAccessToken, addNotification, adminMode, removeSession);

  const {
    searchQuery, setSearchQuery,
    statusFilter, setStatusFilter,
    sortColumn, sortDirection, handleSort,
    columnFilters, setColumnFilters,
    currentPage, sessionsPerPage, handleSessionsPerPageChange,
    handlePreviousPage, handleNextPage,
    effectiveSessions, filteredSessions, sortedSessions, paginatedSessions,
    totalPages,
    stats,
  } = useDashboardFilters({
    sessions,
    blockedDevicesSet,
    tenantId,
    globalAdminMode,
    tenantIdFilter,
  });

  // Redirect regular users (non-admin, non-operator) to progress portal – they must never see the session list
  useEffect(() => {
    if (user && !user.isTenantAdmin && !user.isGlobalAdmin && user.role !== 'Operator') {
      router.replace("/progress");
    }
  }, [user, router]);

  const serialValidationEnabled = useTenantSecurityConfig(tenantId, user, getAccessToken, addNotification);
  const tenantList = useTenantList(globalAdminMode, getAccessToken);

  // Disable global admin mode if user is not a global admin
  useEffect(() => {
    if (user && !user.isGlobalAdmin && globalAdminMode) {
      console.log('[Home] User is not a global admin, disabling global admin mode');
      setGlobalAdminMode(false);
    }
  }, [user, globalAdminMode]);

  // Clear tenant filter when leaving Global Admin mode (refetch is owned by useDashboardSessions)
  useEffect(() => {
    if (!globalAdminMode) setTenantIdFilter("");
  }, [globalAdminMode]);

  const applyTenantIdFilter = (value: string) => {
    setTenantIdFilter(value);
  };

  const submitTenantIdFilter = () => {
    refetch();
  };

  const clearTenantIdFilter = () => {
    setTenantIdFilter("");
    refetchWith("");
  };

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
      {/* Main content */}
      <main className="max-w-7xl mx-auto py-4 sm:px-6 lg:px-8">
        <div className="px-4 sm:px-0">
          {/* Feedback & bug report banner */}
          <div className="mb-4 bg-blue-50 border border-blue-300 rounded-lg px-4 py-3 flex items-start gap-3 dark:bg-blue-950/30 dark:border-blue-700/50">
            <svg className="w-4 h-4 text-blue-500 mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
            </svg>
            <p className="text-sm text-blue-800 dark:text-blue-300">
              <span className="font-semibold">Private Preview.</span>{" "}
              The platform is under active development.{" "}
              If something looks off, check the{" "}
              <Link
                href="/changelog"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                Private Preview Changelog
              </Link>{" "}
              or{" "}
              <Link
                href="/docs/known-issues"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                Known Issues
              </Link>
              .{" "}
              Feedback or bug report?{" "}
              <a
                href="https://github.com/okieselbach/Autopilot-Monitor/issues"
                target="_blank"
                rel="noopener noreferrer"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                Open a GitHub issue
              </a>
              {" "}or message me on{" "}
              <a
                href="https://www.linkedin.com/in/oliver-kieselbach/"
                target="_blank"
                rel="noopener noreferrer"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                LinkedIn
              </a>
              .
            </p>
          </div>

          {serialValidationEnabled === false && (
            <div className="mb-6 bg-red-600 border-2 border-red-700 rounded-xl p-5 shadow-lg">
              <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
                <div className="flex items-start gap-3">
                  <svg className="w-6 h-6 text-white mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
                  </svg>
                  <div>
                    <p className="text-base font-bold text-white">Action required: Autopilot Device Validation is disabled</p>
                    <p className="text-sm text-red-100 mt-0.5">
                      Agent ingestion is blocked. Enable Autopilot Device Validation in Settings to start monitoring devices.
                    </p>
                  </div>
                </div>
                <a
                  href="/settings"
                  className="shrink-0 inline-flex items-center gap-2 bg-white text-red-700 font-semibold text-sm px-4 py-2 rounded-lg hover:bg-red-50 transition-colors"
                >
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                  </svg>
                  Open Settings
                </a>
              </div>
            </div>
          )}

          {/* Stats cards */}
          <div className="grid grid-cols-1 gap-5 sm:grid-cols-2 lg:grid-cols-5 mb-2">
            <StatsCard
              title="Active Sessions"
              value={loading ? "..." : stats.activeSessionsCount.toString()}
              description="Currently enrolling"
              color="blue"
            />
            <StatsCard
              title="Success Rate"
              value={loading ? "..." : `${stats.successRate}%`}
              description="Last 7 days"
              color="green"
            />
            <StatsCard
              title="Avg. Duration"
              value={loading ? "..." : `${stats.avgDuration} min`}
              description="Last 7 days"
              color="purple"
            />
            <StatsCard
              title="Total Today"
              value={loading ? "..." : stats.totalToday.toString()}
              description="Started today"
              color="indigo"
            />
            <StatsCard
              title="Failed Today"
              value={loading ? "..." : stats.failedToday.toString()}
              description="Needs attention"
              color="red"
            />
          </div>

          <TipOfTheDay />

          {/* Welcome message - only show when no sessions */}
          {sessions.length === 0 && <WelcomeMessage />}

          {/* Sessions List */}
          {sessions.length > 0 && (
            <SessionTable
              sessions={effectiveSessions}
              filteredSessions={filteredSessions}
              sortedSessions={sortedSessions}
              paginatedSessions={paginatedSessions}
              searchQuery={searchQuery}
              onSearchQueryChange={setSearchQuery}
              statusFilter={statusFilter}
              onStatusFilterChange={setStatusFilter}
              sortColumn={sortColumn}
              sortDirection={sortDirection}
              onSort={handleSort}
              currentPage={currentPage}
              totalPages={totalPages}
              onPreviousPage={handlePreviousPage}
              onNextPage={handleNextPage}
              sessionsPerPage={sessionsPerPage}
              onSessionsPerPageChange={handleSessionsPerPageChange}
              hasMore={hasMore}
              loadingMore={loadingMore}
              onLoadMore={loadMore}
              adminMode={adminMode}
              globalAdminMode={globalAdminMode}
              tenantIdFilter={tenantIdFilter}
              onTenantIdFilterChange={applyTenantIdFilter}
              onTenantIdFilterSubmit={submitTenantIdFilter}
              onTenantIdFilterClear={clearTenantIdFilter}
              tenantList={tenantList}
              blockedDevicesSet={blockedDevicesSet}
              isPreviewBlocked={isPreviewBlocked}
              user={user}
              columnFilters={columnFilters}
              onColumnFiltersChange={setColumnFilters}
              onDeleteSession={deleteSession}
              onBlockDevice={blockDevice}
            />
          )}
        </div>
      </main>

      {/* Delete Confirmation Modal */}
      {showDeleteConfirm && sessionToDelete && (
        <DeleteConfirmModal
          sessionToDelete={sessionToDelete}
          onConfirm={confirmDelete}
          onCancel={cancelDelete}
        />
      )}

      {/* Block Device Confirmation Modal */}
      {showBlockConfirm && sessionToBlock && (
        <BlockConfirmModal
          sessionToBlock={sessionToBlock}
          blockingDevice={blockingDevice}
          onConfirm={confirmBlock}
          onCancel={cancelBlock}
        />
      )}
    </div>
    </ProtectedRoute>
  );
}
