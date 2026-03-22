"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { FeedbackSection } from "../../components/FeedbackSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionUserFeedback() {
  const { getAccessToken, setError } = useAdminConfig();
  return (
    <>
      <AdminNotifications />
      <FeedbackSection getAccessToken={getAccessToken} setError={setError} />
    </>
  );
}
