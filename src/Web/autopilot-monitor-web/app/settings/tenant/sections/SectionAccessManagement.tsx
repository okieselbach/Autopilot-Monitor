"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import AdminManagementSection from "../../components/AdminManagementSection";

export function SectionAccessManagement() {
  const {
    admins, loadingAdmins,
    newAdminEmail, setNewAdminEmail,
    newMemberRole, setNewMemberRole,
    addingAdmin, removingAdmin, togglingAdmin,
    adminSearchQuery, setAdminSearchQuery,
    currentAdminPage, setCurrentAdminPage,
    user,
    handleAddAdmin, handleRemoveAdmin,
    handleToggleTenantAdmin, handleUpdatePermissions,
  } = useTenantConfig();

  return (
    <>
      <TenantNotifications />
      <AdminManagementSection
        admins={admins}
        loadingAdmins={loadingAdmins}
        newAdminEmail={newAdminEmail}
        setNewAdminEmail={setNewAdminEmail}
        newMemberRole={newMemberRole}
        setNewMemberRole={setNewMemberRole}
        addingAdmin={addingAdmin}
        removingAdmin={removingAdmin}
        togglingAdmin={togglingAdmin}
        adminSearchQuery={adminSearchQuery}
        setAdminSearchQuery={setAdminSearchQuery}
        currentAdminPage={currentAdminPage}
        setCurrentAdminPage={setCurrentAdminPage}
        user={user}
        onAddAdmin={handleAddAdmin}
        onRemoveAdmin={handleRemoveAdmin}
        onToggleAdmin={handleToggleTenantAdmin}
        onUpdatePermissions={handleUpdatePermissions}
      />
    </>
  );
}
