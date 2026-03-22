# Tenant Config Report (Galactic Admin)

## Goal
New galactic-admin-only page that shows a read-only report of a tenant's full configuration,
including admin-set parameters and computed runtime (agent) parameters.

## Plan
- [x] Write plan
- [ ] Create `/app/tenant-config-report/page.tsx` — galactic admin only, read-only report
  - Tenant selector (reuse pattern from gather-rules page)
  - Fetch config via `GET /api/config/{tenantId}` (existing endpoint, GA cross-tenant)
  - Display all TenantConfiguration fields grouped by section
  - Show computed runtime parameters (AgentConfigResponse equivalents with defaults)
  - Highlight non-default values for quick scanning
- [ ] Add nav item to galactic admin section in `globalNavConfig.tsx`
- [ ] Commit and push to feature branch
