import { describe, it, expect } from "vitest";
import { api } from "../api";

describe("api.notifications tenant endpoints", () => {
  it("tenantList resolves to /api/notifications", () => {
    expect(api.notifications.tenantList()).toMatch(/\/api\/notifications$/);
  });

  it("tenantDismiss interpolates the notification id into the path", () => {
    const url = api.notifications.tenantDismiss("abc-123");
    expect(url).toMatch(/\/api\/notifications\/abc-123\/dismiss$/);
  });

  it("tenantDismissAll resolves to the dismiss-all path", () => {
    expect(api.notifications.tenantDismissAll()).toMatch(/\/api\/notifications\/dismiss-all$/);
  });

  it("tenant endpoints are distinct from global notification endpoints", () => {
    expect(api.notifications.tenantList()).not.toEqual(api.notifications.list());
    expect(api.notifications.tenantDismiss("x")).not.toEqual(api.notifications.dismiss("x"));
    expect(api.notifications.tenantDismissAll()).not.toEqual(api.notifications.dismissAll());
  });
});
