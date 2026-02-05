/**
 * Centralized application configuration
 * All configuration values are defined here for easy maintenance
 */

/**
 * API Base URL
 *
 * Sources (in priority order):
 * 1. NEXT_PUBLIC_API_BASE_URL environment variable
 * 2. Default: http://localhost:7071 (local development)
 *
 * Production: Set NEXT_PUBLIC_API_BASE_URL in your environment
 * Example: https://autopilot-monitor-api.azurewebsites.net
 */
export const API_BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:7071";

/**
 * Cache durations (in milliseconds)
 */
export const CACHE_DURATION = {
  /** Usage metrics cache: 5 minutes */
  USAGE_METRICS: 5 * 60 * 1000,
  /** Session data refresh: 10 seconds */
  SESSION_REFRESH: 10 * 1000,
};

/**
 * SignalR Configuration
 */
export const SIGNALR_CONFIG = {
  /** Hub name for autopilot monitoring */
  HUB_NAME: "autopilotmonitor",
  /** Reconnect delay in milliseconds */
  RECONNECT_DELAY: 5000,
};

/**
 * UI Configuration
 */
export const UI_CONFIG = {
  /** Number of sessions per page in pagination */
  SESSIONS_PER_PAGE: 10,
  /** Auto-hide duration for success messages (in milliseconds) */
  SUCCESS_MESSAGE_DURATION: 3000,
  /** Maintenance success message duration (in milliseconds) */
  MAINTENANCE_SUCCESS_DURATION: 5000,
};
