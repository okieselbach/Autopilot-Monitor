/**
 * Shared chart theme used by recharts-based charts in the App Dashboard.
 * Keeps colours, grid, axis, legend and tooltip styling consistent with the
 * existing dark theme across the app.
 */

export const chartColors = {
  primary: "#60a5fa",      // blue-400
  success: "#34d399",      // emerald-400
  danger: "#f87171",       // red-400
  warning: "#fbbf24",      // amber-400
  muted: "#9ca3af",        // gray-400
  gridLine: "#374151",     // gray-700
  axisLine: "#4b5563",     // gray-600
  tooltipBg: "#1f2937",    // gray-800
  tooltipBorder: "#374151", // gray-700
  tooltipText: "#f3f4f6",  // gray-100
} as const;

export const tooltipStyle = {
  backgroundColor: chartColors.tooltipBg,
  border: `1px solid ${chartColors.tooltipBorder}`,
  borderRadius: "0.375rem",
  color: chartColors.tooltipText,
  fontSize: "0.75rem",
  padding: "0.5rem 0.75rem",
} as const;

export const axisTick = { fill: chartColors.muted, fontSize: 11 } as const;
export const axisLine = { stroke: chartColors.axisLine } as const;
export const legendStyle = { color: chartColors.muted, fontSize: "0.75rem" } as const;
