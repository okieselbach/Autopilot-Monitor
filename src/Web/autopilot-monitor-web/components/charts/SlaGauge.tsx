"use client";

import { PieChart, Pie, Cell, ResponsiveContainer } from "recharts";
import { chartColors } from "./chartTheme";

interface SlaGaugeProps {
  /** Current value (e.g. 94.5 for success rate, 32 for duration in minutes) */
  value: number;
  /** Target value (e.g. 95.0 for success rate, 60 for max duration) */
  target: number;
  /** Label shown below the value */
  label: string;
  /** Unit shown after the value (e.g. "%" or "min") */
  unit: string;
  /** If true, lower is better (e.g. duration) */
  invert?: boolean;
}

/**
 * Semi-circular gauge displaying current value vs target with color zones.
 * Green = at/above target, Yellow = within 5% margin, Red = >5% below target.
 * For inverted metrics (duration), lower is better.
 */
export function SlaGauge({ value, target, label, unit, invert = false }: SlaGaugeProps) {
  // Determine compliance status
  const getStatus = (): "met" | "warning" | "breached" => {
    if (invert) {
      // Lower is better (e.g. duration)
      if (value <= target) return "met";
      if (value <= target * 1.05) return "warning";
      return "breached";
    } else {
      // Higher is better (e.g. success rate)
      if (value >= target) return "met";
      if (value >= target * 0.95) return "warning";
      return "breached";
    }
  };

  const status = getStatus();
  const statusColor =
    status === "met" ? chartColors.success :
    status === "warning" ? chartColors.warning :
    chartColors.danger;

  const statusLabel =
    status === "met" ? "Compliant" :
    status === "warning" ? "At Risk" :
    "Breached";

  // PieChart data for semi-circle gauge
  // Fill portion vs empty portion (mapped to 180 degrees)
  const maxVal = invert ? target * 2 : 100;
  const fillPercent = Math.min(Math.max(value / maxVal, 0), 1);

  const data = [
    { name: "filled", value: fillPercent * 100 },
    { name: "empty", value: (1 - fillPercent) * 100 },
  ];

  return (
    <div className="flex flex-col items-center">
      <div className="relative" style={{ width: 180, height: 100 }}>
        <ResponsiveContainer width="100%" height={180}>
          <PieChart>
            <Pie
              data={data}
              cx="50%"
              cy="100%"
              startAngle={180}
              endAngle={0}
              innerRadius={60}
              outerRadius={80}
              dataKey="value"
              stroke="none"
            >
              <Cell fill={statusColor} />
              <Cell fill="#374151" />
            </Pie>
          </PieChart>
        </ResponsiveContainer>
        <div className="absolute inset-0 flex flex-col items-center justify-end pb-1">
          <span className="text-2xl font-bold text-white">
            {value.toFixed(1)}{unit}
          </span>
        </div>
      </div>
      <span className="text-sm text-gray-400 mt-1">{label}</span>
      <span className="text-xs mt-0.5" style={{ color: statusColor }}>
        {statusLabel} (target: {target}{unit})
      </span>
    </div>
  );
}
