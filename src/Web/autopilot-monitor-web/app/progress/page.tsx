"use client";

import { useState, useEffect, useRef } from "react";
import { API_BASE_URL } from "@/lib/config";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useSignalR } from "../../contexts/SignalRContext";
import { ProtectedRoute } from "../../components/ProtectedRoute";

interface Session {
  sessionId: string;
  tenantId: string;
  serialNumber: string;
  deviceName: string;
  manufacturer: string;
  model: string;
  startedAt: string;
  status: string;
  currentPhase: number;
  eventCount: number;
  durationSeconds: number;
  failureReason?: string;
}

const phaseSteps = [
  { id: 0, label: "Device registered", shortLabel: "Registration" },
  { id: 1, label: "Network configured", shortLabel: "Network" },
  { id: 2, label: "Identity verified", shortLabel: "Identity" },
  { id: 3, label: "Management enrolled", shortLabel: "Enrollment" },
  { id: 4, label: "Security policies applied", shortLabel: "Policies" },
  { id: 5, label: "Installing apps", shortLabel: "Apps" },
  { id: 6, label: "User setup", shortLabel: "User Setup" },
  { id: 7, label: "Finalizing setup", shortLabel: "Complete" },
];

export default function ProgressPortalPage() {
  const [serialInput, setSerialInput] = useState("");
  const [session, setSession] = useState<Session | null>(null);
  const [allSessions, setAllSessions] = useState<Session[]>([]);
  const [searching, setSearching] = useState(false);
  const [searched, setSearched] = useState(false);
  const [notFound, setNotFound] = useState(false);

  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();
  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();

  const hasJoinedGroup = useRef(false);
  const sessionRef = useRef<Session | null>(null);

  // Keep ref in sync
  useEffect(() => {
    sessionRef.current = session;
  }, [session]);

  // Join tenant group for real-time updates
  useEffect(() => {
    if (isConnected && !hasJoinedGroup.current) {
      joinGroup(`tenant-${tenantId}`);
      hasJoinedGroup.current = true;
    }
    return () => {
      if (hasJoinedGroup.current) {
        leaveGroup(`tenant-${tenantId}`);
        hasJoinedGroup.current = false;
      }
    };
  }, [isConnected, tenantId]);

  // Listen for real-time session updates
  useEffect(() => {
    const handleNewEvents = (data: { session: Session }) => {
      if (
        data.session &&
        sessionRef.current &&
        data.session.sessionId === sessionRef.current.sessionId
      ) {
        setSession(data.session);
      }
    };
    on("newevents", handleNewEvents);
    on("newSession", handleNewEvents);
    return () => {
      off("newevents", handleNewEvents);
      off("newSession", handleNewEvents);
    };
  }, [on, off]);

  const searchBySerial = async () => {
    if (!serialInput.trim()) return;

    setSearching(true);
    setSearched(true);
    setNotFound(false);
    setSession(null);

    try {
      const token = await getAccessToken();
      if (!token) {
        setSearching(false);
        return;
      }

      const response = await fetch(
        `${API_BASE_URL}/api/sessions?tenantId=${tenantId}`,
        { headers: { Authorization: `Bearer ${token}` } }
      );

      if (response.ok) {
        const data = await response.json();
        const sessions: Session[] = data.sessions || [];
        setAllSessions(sessions);

        // Find session by serial number (case-insensitive)
        const query = serialInput.trim().toLowerCase();
        const found = sessions
          .filter(
            (s) =>
              s.serialNumber.toLowerCase() === query ||
              s.serialNumber.toLowerCase().includes(query) ||
              s.deviceName?.toLowerCase().includes(query)
          )
          .sort(
            (a, b) =>
              new Date(b.startedAt).getTime() -
              new Date(a.startedAt).getTime()
          )[0];

        if (found) {
          setSession(found);
        } else {
          setNotFound(true);
        }
      }
    } catch (error) {
      console.error("Search failed:", error);
      setNotFound(true);
    } finally {
      setSearching(false);
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Enter") searchBySerial();
  };

  // Derive progress
  const overallProgress = session
    ? session.status === "Succeeded"
      ? 100
      : session.status === "Failed"
      ? Math.min(
          100,
          ((session.currentPhase === 99
            ? 3
            : session.currentPhase) /
            7) *
            100
        )
      : Math.min(100, (session.currentPhase / 7) * 100)
    : 0;

  const estimatedRemaining = session
    ? (() => {
        if (session.status !== "InProgress") return null;
        const currentPhase = Math.min(session.currentPhase, 7);
        if (currentPhase === 0) return null;
        const elapsed = session.durationSeconds;
        const rate = elapsed / currentPhase;
        const remaining = (7 - currentPhase) * rate;
        return Math.round(remaining / 60);
      })()
    : null;

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gradient-to-b from-blue-50 to-white">
        <div className="max-w-2xl mx-auto px-4 py-12">
          {/* Header */}
          <div className="text-center mb-10">
            <div className="inline-flex items-center justify-center w-16 h-16 bg-blue-100 rounded-full mb-4">
              <svg
                className="w-8 h-8 text-blue-600"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={2}
                  d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"
                />
              </svg>
            </div>
            <h1 className="text-3xl font-bold text-gray-900 mb-2">
              Device Setup Progress
            </h1>
            <p className="text-gray-500">
              Enter your device serial number to check status
            </p>
          </div>

          {/* Search */}
          <div className="flex items-center space-x-3 mb-10">
            <div className="flex-1 relative">
              <input
                type="text"
                value={serialInput}
                onChange={(e) => setSerialInput(e.target.value)}
                onKeyDown={handleKeyDown}
                placeholder="Enter serial number or device name..."
                className="w-full px-4 py-3 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 text-lg"
              />
            </div>
            <button
              onClick={searchBySerial}
              disabled={searching || !serialInput.trim()}
              className="px-6 py-3 bg-blue-600 text-white rounded-lg hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed font-medium"
            >
              {searching ? "Searching..." : "Check Status"}
            </button>
          </div>

          {/* Not Found */}
          {notFound && (
            <div className="bg-white rounded-xl shadow-sm border border-gray-200 p-8 text-center">
              <svg
                className="w-12 h-12 mx-auto text-gray-300 mb-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={1.5}
                  d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
                />
              </svg>
              <h2 className="text-lg font-semibold text-gray-900 mb-2">
                Device Not Found
              </h2>
              <p className="text-gray-500 text-sm">
                No enrollment session found for &quot;{serialInput}&quot;.
                Please check the serial number and try again.
              </p>
            </div>
          )}

          {/* Session Found - Progress Display */}
          {session && (
            <div className="bg-white rounded-xl shadow-sm border border-gray-200 overflow-hidden">
              {/* Status Header */}
              <div
                className={`px-6 py-4 ${
                  session.status === "InProgress"
                    ? "bg-blue-50 border-b border-blue-100"
                    : session.status === "Succeeded"
                    ? "bg-green-50 border-b border-green-100"
                    : "bg-red-50 border-b border-red-100"
                }`}
              >
                <div className="text-center">
                  <h2
                    className={`text-xl font-semibold ${
                      session.status === "InProgress"
                        ? "text-blue-800"
                        : session.status === "Succeeded"
                        ? "text-green-800"
                        : "text-red-800"
                    }`}
                  >
                    {session.status === "InProgress"
                      ? "Setting up your device..."
                      : session.status === "Succeeded"
                      ? "Setup complete!"
                      : "Setup encountered an issue"}
                  </h2>
                  <p className="text-sm text-gray-500 mt-1">
                    {session.deviceName || session.serialNumber} |{" "}
                    {session.manufacturer} {session.model}
                  </p>
                </div>
              </div>

              <div className="p-6">
                {/* Overall Progress Bar */}
                <div className="mb-8">
                  <div className="flex items-center justify-between mb-2">
                    <span className="text-sm text-gray-500">
                      Overall Progress
                    </span>
                    <span
                      className={`text-sm font-semibold ${
                        session.status === "Failed"
                          ? "text-red-600"
                          : "text-blue-600"
                      }`}
                    >
                      {Math.round(overallProgress)}%
                    </span>
                  </div>
                  <div className="w-full h-4 bg-gray-100 rounded-full overflow-hidden">
                    <div
                      className={`h-full rounded-full transition-all duration-1000 ${
                        session.status === "Failed"
                          ? "bg-red-500"
                          : session.status === "Succeeded"
                          ? "bg-green-500"
                          : "bg-blue-500"
                      }`}
                      style={{ width: `${overallProgress}%` }}
                    />
                  </div>
                </div>

                {/* Phase Steps */}
                <div className="space-y-3 mb-8">
                  {phaseSteps.map((step) => {
                    const effectivePhase =
                      session.currentPhase === 99
                        ? 3
                        : session.currentPhase;
                    const isCompleted =
                      session.status === "Succeeded" ||
                      step.id < effectivePhase;
                    const isCurrent =
                      step.id === effectivePhase &&
                      session.status === "InProgress";
                    const isFailed =
                      step.id === effectivePhase &&
                      session.status === "Failed";
                    const isPending = step.id > effectivePhase;

                    return (
                      <div
                        key={step.id}
                        className="flex items-center space-x-3"
                      >
                        {/* Icon */}
                        <div className="flex-shrink-0">
                          {isCompleted ? (
                            <div className="w-8 h-8 rounded-full bg-green-100 flex items-center justify-center">
                              <svg
                                className="w-5 h-5 text-green-600"
                                fill="none"
                                viewBox="0 0 24 24"
                                stroke="currentColor"
                              >
                                <path
                                  strokeLinecap="round"
                                  strokeLinejoin="round"
                                  strokeWidth={3}
                                  d="M5 13l4 4L19 7"
                                />
                              </svg>
                            </div>
                          ) : isCurrent ? (
                            <div className="w-8 h-8 rounded-full bg-blue-100 flex items-center justify-center">
                              <div className="w-3 h-3 bg-blue-500 rounded-full animate-pulse" />
                            </div>
                          ) : isFailed ? (
                            <div className="w-8 h-8 rounded-full bg-red-100 flex items-center justify-center">
                              <svg
                                className="w-5 h-5 text-red-600"
                                fill="none"
                                viewBox="0 0 24 24"
                                stroke="currentColor"
                              >
                                <path
                                  strokeLinecap="round"
                                  strokeLinejoin="round"
                                  strokeWidth={3}
                                  d="M6 18L18 6M6 6l12 12"
                                />
                              </svg>
                            </div>
                          ) : (
                            <div className="w-8 h-8 rounded-full bg-gray-100 flex items-center justify-center">
                              <div className="w-3 h-3 bg-gray-300 rounded-full" />
                            </div>
                          )}
                        </div>

                        {/* Label */}
                        <span
                          className={`text-sm ${
                            isCompleted
                              ? "text-green-700 font-medium"
                              : isCurrent
                              ? "text-blue-700 font-medium"
                              : isFailed
                              ? "text-red-700 font-medium"
                              : "text-gray-400"
                          }`}
                        >
                          {step.label}
                          {isCurrent &&
                            step.id === 5 &&
                            ` (${session.currentPhase === 5 ? "in progress" : ""})`}
                        </span>
                      </div>
                    );
                  })}
                </div>

                {/* Estimated Time / Status */}
                {session.status === "InProgress" && estimatedRemaining && (
                  <div className="bg-blue-50 rounded-lg p-4 text-center">
                    <div className="text-sm text-blue-700">
                      Estimated time remaining:{" "}
                      <span className="font-semibold">
                        ~{estimatedRemaining} minutes
                      </span>
                    </div>
                  </div>
                )}

                {session.status === "InProgress" && (
                  <div className="mt-6 bg-gray-50 rounded-lg p-4 text-center">
                    <p className="text-sm text-gray-600">
                      You can leave your device on this screen. Setup will
                      continue automatically.
                    </p>
                  </div>
                )}

                {session.status === "Succeeded" && (
                  <div className="bg-green-50 rounded-lg p-4 text-center">
                    <p className="text-sm text-green-700 font-medium">
                      Your device is ready to use! Total setup time:{" "}
                      {Math.round(session.durationSeconds / 60)} minutes.
                    </p>
                  </div>
                )}

                {session.status === "Failed" && (
                  <div className="bg-red-50 rounded-lg p-4 text-center">
                    <p className="text-sm text-red-700">
                      {session.failureReason ||
                        "Setup encountered an error. Please contact your IT department."}
                    </p>
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      </div>
    </ProtectedRoute>
  );
}
