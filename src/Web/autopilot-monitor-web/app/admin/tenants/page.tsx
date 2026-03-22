import { redirect } from "next/navigation";

export default function TenantsPage() {
  redirect("/admin/tenants/management");
}
