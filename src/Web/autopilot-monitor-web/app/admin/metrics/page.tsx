import { redirect } from "next/navigation";

export default function MetricsPage() {
  redirect("/admin/metrics/agent-metrics");
}
