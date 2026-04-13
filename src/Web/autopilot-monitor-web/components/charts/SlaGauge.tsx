"use client";

import { useEffect, useState } from "react";
import { PieChart, Pie, Cell, ResponsiveContainer } from "recharts";

interface SlaGaugeProps {
  value: number;
  target: number;
  label: string;
  unit: string;
  invert?: boolean;
}

export function SlaGauge({ value, target, label, unit, invert = false }: SlaGaugeProps) {
  const [isDark, setIsDark] = useState(false);

  useEffect(() => {
    const check = () => setIsDark(document.documentElement.classList.contains("dark"));
    check();
    const observer = new MutationObserver(check);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["class"] });
    return () => observer.disconnect();
  }, []);

  const getStatus = (): "met" | "warning" | "breached" => {
    if (invert) {
      if (value <= target) return "met";
      if (value <= target * 1.05) return "warning";
      return "breached";
    } else {
      if (value >= target) return "met";
      if (value >= target * 0.95) return "warning";
      return "breached";
    }
  };

  const status = getStatus();
  const statusColor =
    status === "met" ? "#16a34a" :
    status === "warning" ? "#d97706" :
    "#dc2626";

  const statusLabel =
    status === "met" ? "Compliant" :
    status === "warning" ? "At Risk" :
    "Breached";

  const emptyColor = isDark ? "#374151" : "#e5e7eb"; // gray-700 / gray-200

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
              <Cell fill={emptyColor} />
            </Pie>
          </PieChart>
        </ResponsiveContainer>
        <div className="absolute inset-0 flex flex-col items-center justify-end pb-1">
          <span className="text-2xl font-bold text-gray-900 dark:text-white">
            {value.toFixed(1)}{unit}
          </span>
        </div>
      </div>
      <span className="text-sm text-gray-600 dark:text-gray-400 mt-1">{label}</span>
      <span className="text-xs font-medium mt-0.5 px-2 py-0.5 rounded" style={{ color: statusColor }}>
        {statusLabel} (target: {target}{unit})
      </span>
    </div>
  );
}
