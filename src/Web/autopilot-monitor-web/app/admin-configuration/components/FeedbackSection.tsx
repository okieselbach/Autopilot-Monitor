"use client";

import { useCallback, useEffect, useState } from "react";
import { API_BASE_URL } from "@/lib/config";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface FeedbackEntry {
  upn: string;
  tenantId: string;
  displayName: string;
  rating: number | null;
  comment: string | null;
  dismissed: boolean;
  submitted: boolean;
  interactedAt: string | null;
}

interface FeedbackSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
}

export function FeedbackSection({ getAccessToken, setError }: FeedbackSectionProps) {
  const [expanded, setExpanded] = useState(false);
  const [loading, setLoading] = useState(false);
  const [entries, setEntries] = useState<FeedbackEntry[]>([]);
  const [currentPage, setCurrentPage] = useState(0);
  const entriesPerPage = 5;

  const fetchFeedback = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);

      const response = await authenticatedFetch(`${API_BASE_URL}/api/feedback/all`, getAccessToken);

      if (!response.ok) {
        throw new Error(`Failed to load feedback: ${response.statusText}`);
      }

      const data = await response.json();
      // Sort by interactedAt descending (newest first)
      const sorted = (data.feedback || []).sort((a: FeedbackEntry, b: FeedbackEntry) => {
        const dateA = a.interactedAt ? new Date(a.interactedAt).getTime() : 0;
        const dateB = b.interactedAt ? new Date(b.interactedAt).getTime() : 0;
        return dateB - dateA;
      });
      setEntries(sorted);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while fetching feedback");
      } else {
        console.error("Error fetching feedback:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to load feedback");
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, setError]);

  useEffect(() => {
    if (expanded && entries.length === 0) {
      fetchFeedback();
    }
  }, [expanded, entries.length, fetchFeedback]);

  // Stats
  const submittedEntries = entries.filter(e => e.submitted);
  const dismissedEntries = entries.filter(e => e.dismissed && !e.submitted);
  const avgRating = submittedEntries.length > 0
    ? (submittedEntries.reduce((sum, e) => sum + (e.rating || 0), 0) / submittedEntries.length).toFixed(1)
    : "—";

  // Pagination
  const totalPages = Math.ceil(entries.length / entriesPerPage);
  const paginatedEntries = entries.slice(
    currentPage * entriesPerPage,
    (currentPage + 1) * entriesPerPage
  );

  const formatTimeAgo = (dateStr: string | null): string => {
    if (!dateStr) return "unknown";
    const diff = Date.now() - new Date(dateStr).getTime();
    const minutes = Math.floor(diff / 60000);
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days < 7) return `${days}d ago`;
    const weeks = Math.floor(days / 7);
    return `${weeks}w ago`;
  };

  const renderStars = (rating: number | null) => {
    if (rating == null) return null;
    return (
      <span className="inline-flex gap-0.5">
        {[1, 2, 3, 4, 5].map(s => (
          <svg
            key={s}
            className={`w-4 h-4 ${s <= rating ? "text-yellow-400" : "text-gray-300 dark:text-gray-600"}`}
            fill="currentColor"
            viewBox="0 0 20 20"
          >
            <path d="M9.049 2.927c.3-.921 1.603-.921 1.902 0l1.07 3.292a1 1 0 00.95.69h3.462c.969 0 1.371 1.24.588 1.81l-2.8 2.034a1 1 0 00-.364 1.118l1.07 3.292c.3.921-.755 1.688-1.54 1.118l-2.8-2.034a1 1 0 00-1.175 0l-2.8 2.034c-.784.57-1.838-.197-1.539-1.118l1.07-3.292a1 1 0 00-.364-1.118L2.98 8.72c-.783-.57-.38-1.81.588-1.81h3.461a1 1 0 00.951-.69l1.07-3.292z" />
          </svg>
        ))}
      </span>
    );
  };

  return (
    <div className="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden border border-purple-200 dark:border-purple-800">
      {/* Header */}
      <button
        onClick={() => setExpanded(!expanded)}
        className="w-full px-6 py-4 flex items-center justify-between hover:bg-gray-50 dark:hover:bg-gray-750 transition-colors"
      >
        <div className="flex items-center gap-3">
          <div className="w-8 h-8 bg-purple-100 dark:bg-purple-900 rounded-lg flex items-center justify-center">
            <svg className="w-4 h-4 text-purple-600 dark:text-purple-400" fill="currentColor" viewBox="0 0 20 20">
              <path d="M9.049 2.927c.3-.921 1.603-.921 1.902 0l1.07 3.292a1 1 0 00.95.69h3.462c.969 0 1.371 1.24.588 1.81l-2.8 2.034a1 1 0 00-.364 1.118l1.07 3.292c.3.921-.755 1.688-1.54 1.118l-2.8-2.034a1 1 0 00-1.175 0l-2.8 2.034c-.784.57-1.838-.197-1.539-1.118l1.07-3.292a1 1 0 00-.364-1.118L2.98 8.72c-.783-.57-.38-1.81.588-1.81h3.461a1 1 0 00.951-.69l1.07-3.292z" />
            </svg>
          </div>
          <div className="text-left">
            <h2 className="text-lg font-semibold text-gray-900 dark:text-white">User Feedback</h2>
            <p className="text-sm text-gray-500 dark:text-gray-400">In-app feedback from tenant admins and operators</p>
          </div>
        </div>
        <svg
          className={`w-5 h-5 text-gray-400 transition-transform ${expanded ? "rotate-180" : ""}`}
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {/* Content */}
      {expanded && (
        <div className="px-6 pb-6 border-t border-gray-200 dark:border-gray-700">
          {loading ? (
            <div className="flex items-center justify-center py-8">
              <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-purple-600" />
            </div>
          ) : entries.length === 0 ? (
            <div className="text-center py-8 text-gray-500 dark:text-gray-400">
              No feedback received yet
            </div>
          ) : (
            <>
              {/* Stats */}
              <div className="flex flex-wrap items-center gap-4 py-4 text-sm">
                <span className="text-gray-600 dark:text-gray-300">
                  <span className="font-semibold text-purple-600 dark:text-purple-400">{submittedEntries.length}</span> Submitted
                </span>
                <span className="text-gray-400">|</span>
                <span className="text-gray-600 dark:text-gray-300">
                  <span className="font-semibold text-gray-500">{dismissedEntries.length}</span> Dismissed
                </span>
                <span className="text-gray-400">|</span>
                <span className="text-gray-600 dark:text-gray-300">
                  Avg <span className="font-semibold text-yellow-500">{avgRating}</span>
                </span>
                <button
                  onClick={fetchFeedback}
                  className="ml-auto text-sm text-purple-600 hover:text-purple-700 dark:text-purple-400 dark:hover:text-purple-300"
                >
                  Refresh
                </button>
              </div>

              {/* Entries */}
              <div className="space-y-2">
                {paginatedEntries.map((entry) => (
                  <div
                    key={entry.upn}
                    className={`border rounded-lg p-3 transition-all ${
                      entry.dismissed && !entry.submitted
                        ? "bg-gray-50 dark:bg-gray-750 border-gray-200 dark:border-gray-700 opacity-60"
                        : "bg-white dark:bg-gray-800 border-gray-200 dark:border-gray-700"
                    }`}
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-1">
                      <div className="flex items-center gap-2">
                        <span className="text-sm font-medium text-gray-900 dark:text-gray-100 truncate max-w-[200px]">
                          {entry.upn}
                        </span>
                        {entry.submitted ? renderStars(entry.rating) : (
                          <span className="text-xs text-gray-400 dark:text-gray-500 italic">dismissed</span>
                        )}
                      </div>
                      <div className="flex items-center gap-2 text-xs text-gray-500 dark:text-gray-400">
                        <span>{formatTimeAgo(entry.interactedAt)}</span>
                        <span className="hidden sm:inline">·</span>
                        <span className="hidden sm:inline truncate max-w-[120px]">
                          {entry.tenantId.substring(0, 8)}...
                        </span>
                      </div>
                    </div>
                    {entry.comment && (
                      <p className="mt-1 text-sm text-gray-600 dark:text-gray-300 italic">
                        &quot;{entry.comment}&quot;
                      </p>
                    )}
                  </div>
                ))}
              </div>

              {/* Pagination */}
              {totalPages > 1 && (
                <div className="flex items-center justify-between pt-4 border-t border-gray-200 dark:border-gray-700 mt-4">
                  <button
                    onClick={() => setCurrentPage(p => Math.max(0, p - 1))}
                    disabled={currentPage === 0}
                    className="px-4 py-2 text-sm font-medium text-gray-700 dark:text-gray-300 bg-white dark:bg-gray-700 border border-gray-300 dark:border-gray-600 rounded-lg hover:bg-gray-50 dark:hover:bg-gray-650 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                  >
                    Previous
                  </button>
                  <span className="text-sm text-gray-600 dark:text-gray-400">
                    Page {currentPage + 1} of {totalPages}
                  </span>
                  <button
                    onClick={() => setCurrentPage(p => Math.min(totalPages - 1, p + 1))}
                    disabled={currentPage >= totalPages - 1}
                    className="px-4 py-2 text-sm font-medium text-gray-700 dark:text-gray-300 bg-white dark:bg-gray-700 border border-gray-300 dark:border-gray-600 rounded-lg hover:bg-gray-50 dark:hover:bg-gray-650 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                  >
                    Next
                  </button>
                </div>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}
