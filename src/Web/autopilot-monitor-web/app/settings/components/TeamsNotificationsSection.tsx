"use client";

interface TeamsNotificationsSectionProps {
  teamsWebhookUrl: string;
  setTeamsWebhookUrl: (value: string) => void;
  teamsNotifyOnSuccess: boolean;
  setTeamsNotifyOnSuccess: (value: boolean) => void;
  teamsNotifyOnFailure: boolean;
  setTeamsNotifyOnFailure: (value: boolean) => void;
}

export default function TeamsNotificationsSection({
  teamsWebhookUrl,
  setTeamsWebhookUrl,
  teamsNotifyOnSuccess,
  setTeamsNotifyOnSuccess,
  teamsNotifyOnFailure,
  setTeamsNotifyOnFailure,
}: TeamsNotificationsSectionProps) {
  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-sky-50 to-blue-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-sky-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Teams Notifications</h2>
            <p className="text-sm text-gray-500 mt-1">Send enrollment status notifications to a Microsoft Teams channel via Incoming Webhook.</p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-4">

        {/* Webhook URL */}
        <div>
          <label className="block">
            <span className="text-gray-700 font-medium">Incoming Webhook URL</span>
            <p className="text-sm text-gray-500 mb-2">
              Create an Incoming Webhook in your Teams channel (Channel → Connectors → Incoming Webhook) and paste the URL here.
            </p>
            <div className="flex items-center gap-2">
              <input
                type="url"
                value={teamsWebhookUrl}
                onChange={(e) => setTeamsWebhookUrl(e.target.value)}
                placeholder="https://your-org.webhook.office.com/webhookb2/..."
                className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-sky-500 focus:border-sky-500 transition-colors font-mono text-sm"
              />
              {teamsWebhookUrl && (
                <span className="mt-1 inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium bg-green-100 text-green-800 whitespace-nowrap">
                  Active
                </span>
              )}
            </div>
          </label>
        </div>

        {/* Notify on Success */}
        <div className={`flex items-center justify-between p-4 rounded-lg border transition-colors ${teamsWebhookUrl ? 'border-gray-200 hover:border-sky-200' : 'border-gray-100 opacity-50'}`}>
          <div>
            <p className="font-medium text-gray-900">Notify on Success</p>
            <p className="text-sm text-gray-500">Send a notification when an enrollment completes successfully</p>
          </div>
          <button
            onClick={() => teamsWebhookUrl && setTeamsNotifyOnSuccess(!teamsNotifyOnSuccess)}
            disabled={!teamsWebhookUrl}
            className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors disabled:cursor-not-allowed ${teamsNotifyOnSuccess && teamsWebhookUrl ? 'bg-sky-500' : 'bg-gray-300'}`}
          >
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${teamsNotifyOnSuccess && teamsWebhookUrl ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

        {/* Notify on Failure */}
        <div className={`flex items-center justify-between p-4 rounded-lg border transition-colors ${teamsWebhookUrl ? 'border-gray-200 hover:border-sky-200' : 'border-gray-100 opacity-50'}`}>
          <div>
            <p className="font-medium text-gray-900">Notify on Failure</p>
            <p className="text-sm text-gray-500">Send a notification when an enrollment fails</p>
          </div>
          <button
            onClick={() => teamsWebhookUrl && setTeamsNotifyOnFailure(!teamsNotifyOnFailure)}
            disabled={!teamsWebhookUrl}
            className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors disabled:cursor-not-allowed ${teamsNotifyOnFailure && teamsWebhookUrl ? 'bg-sky-500' : 'bg-gray-300'}`}
          >
            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${teamsNotifyOnFailure && teamsWebhookUrl ? 'translate-x-6' : 'translate-x-1'}`} />
          </button>
        </div>

      </div>
    </div>
  );
}
