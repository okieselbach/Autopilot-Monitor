"use client";

interface StatCardProps {
  label: string;
  value: number | string;
  borderColor?: string;
  valueColor?: string;
}

export function StatCard({ label, value, borderColor, valueColor }: StatCardProps) {
  return (
    <div className={`bg-white rounded-lg shadow p-4${borderColor ? ` border-l-4 ${borderColor}` : ""}`}>
      <div className="text-sm font-medium text-gray-500">{label}</div>
      <div className={`mt-1 text-2xl font-bold ${valueColor ?? "text-gray-900"}`}>{value}</div>
    </div>
  );
}
