"use client";

import {
  ResponsiveContainer,
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Cell,
} from "recharts";
import { chartColors, tooltipStyle, axisTick, axisLine } from "./chartTheme";

export interface AppBarChartProps {
  data: Array<Record<string, unknown>>;
  categoryKey: string;
  valueKey: string;
  height?: number;
  /** Horizontal bars (category on Y axis). Default: vertical. */
  horizontal?: boolean;
  /** Bar colour or a function to derive per-bar colour from the row. */
  barColor?: string | ((row: Record<string, unknown>) => string);
  valueFormatter?: (value: number) => string;
}

/**
 * Reusable bar chart used by the App Dashboard detail page.
 * Used for "Top Failure Codes" and "Installer Phase Breakdown" etc.
 */
export default function AppBarChart({
  data,
  categoryKey,
  valueKey,
  height = 240,
  horizontal = false,
  barColor = chartColors.primary,
  valueFormatter,
}: AppBarChartProps) {
  return (
    <ResponsiveContainer width="100%" height={height}>
      <BarChart
        data={data}
        layout={horizontal ? "vertical" : "horizontal"}
        margin={{ top: 8, right: 16, left: horizontal ? 80 : 0, bottom: 4 }}
      >
        <CartesianGrid
          stroke={chartColors.gridLine}
          strokeDasharray="3 3"
          horizontal={!horizontal}
          vertical={horizontal}
        />
        {horizontal ? (
          <>
            <XAxis
              type="number"
              tick={axisTick}
              axisLine={axisLine}
              tickLine={false}
              tickFormatter={valueFormatter}
            />
            <YAxis
              type="category"
              dataKey={categoryKey}
              tick={axisTick}
              axisLine={axisLine}
              tickLine={false}
              width={120}
            />
          </>
        ) : (
          <>
            <XAxis
              dataKey={categoryKey}
              tick={axisTick}
              axisLine={axisLine}
              tickLine={false}
            />
            <YAxis
              tick={axisTick}
              axisLine={axisLine}
              tickLine={false}
              tickFormatter={valueFormatter}
              width={48}
            />
          </>
        )}
        <Tooltip
          contentStyle={tooltipStyle}
          cursor={{ fill: chartColors.gridLine, opacity: 0.3 }}
          formatter={(value: number) => [valueFormatter ? valueFormatter(value) : value, ""]}
        />
        <Bar dataKey={valueKey} radius={[4, 4, 0, 0]}>
          {data.map((row, idx) => (
            <Cell
              key={idx}
              fill={typeof barColor === "function" ? barColor(row) : barColor}
            />
          ))}
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  );
}
