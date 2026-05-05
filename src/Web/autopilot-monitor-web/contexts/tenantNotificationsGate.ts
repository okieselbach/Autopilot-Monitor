/**
 * Returns true iff the current user is entitled to fetch tenant notifications.
 * Tenant members of any role (Admin/Operator/Viewer) and Global Admins qualify;
 * unauthenticated users and authenticated users without a tenant role do not.
 *
 * Extracted to a plain .ts module so that vitest can import it without pulling in
 * the React/JSX-laden context module.
 */
export function canFetchTenantNotifications(
  user: { role: string | null; isGlobalAdmin: boolean } | null | undefined
): boolean {
  if (user == null) return false;
  return user.role != null || user.isGlobalAdmin === true;
}
