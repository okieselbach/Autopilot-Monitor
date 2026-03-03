"use client";

interface AutopilotValidationSectionProps {
  validateAutopilotDevice: boolean;
  setValidateAutopilotDevice: (value: boolean) => void;
  validateCorporateIdentifier: boolean;
  setValidateCorporateIdentifier: (value: boolean) => void;
  autopilotConsentInProgress: boolean;
  saving: boolean;
  onBeginConsent: (trigger: 'autopilot' | 'corporate') => void;
  previewMode: boolean;
}

export default function AutopilotValidationSection({
  validateAutopilotDevice,
  setValidateAutopilotDevice,
  validateCorporateIdentifier,
  setValidateCorporateIdentifier,
  autopilotConsentInProgress,
  saving,
  onBeginConsent,
  previewMode,
}: AutopilotValidationSectionProps) {
  const anyValidationEnabled = validateAutopilotDevice || validateCorporateIdentifier;

  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200">
        <div className="flex items-center justify-between">
          <div>
            <h2 className="text-xl font-semibold text-gray-900">
              {previewMode ? "Enrollment Device Validation" : "Autopilot Device Validation"}
            </h2>
            <p className="text-sm text-gray-500 mt-1">
              {previewMode
                ? "Validate devices against Intune registrations before accepting agent data (mandatory for agent ingestion)"
                : "Validate devices against Intune Windows Autopilot registration (mandatory for agent ingestion)"}
            </p>
          </div>
          <span className={`inline-flex items-center px-3 py-1 rounded-full text-xs font-medium ${anyValidationEnabled ? "bg-green-100 text-green-800" : "bg-red-100 text-red-800"}`}>
            {anyValidationEnabled ? "Enabled" : "Disabled"}
          </span>
        </div>
      </div>
      <div className="p-6 space-y-4">
        {/* Autopilot Device Validation toggle */}
        <label className="flex items-start justify-between gap-4">
          <div>
            <p className="font-medium text-gray-900">Enable Autopilot Device Validation</p>
            <p className="text-sm text-gray-500">
              Enabling starts Microsoft Entra admin consent for the <strong>DeviceManagementServiceConfig.Read.All</strong> permission. After consent, the setting is saved automatically.
            </p>
          </div>
          <button
            onClick={() => {
              if (validateAutopilotDevice) {
                setValidateAutopilotDevice(false);
              } else {
                // If corporate identifier is already on, consent exists — just enable
                if (validateCorporateIdentifier) {
                  setValidateAutopilotDevice(true);
                } else {
                  onBeginConsent('autopilot');
                }
              }
            }}
            disabled={saving || autopilotConsentInProgress}
            className={`relative inline-flex h-8 w-14 shrink-0 items-center rounded-full transition-colors disabled:opacity-60 disabled:cursor-not-allowed ${validateAutopilotDevice ? 'bg-green-600' : 'bg-gray-300'}`}
          >
            <span className={`inline-block h-6 w-6 transform rounded-full bg-white transition-transform ${validateAutopilotDevice ? 'translate-x-7' : 'translate-x-1'}`} />
          </button>
        </label>

        {/* Corporate Identifier Validation toggle (Preview only) */}
        {previewMode && (
          <label className="flex items-start justify-between gap-4">
            <div>
              <p className="font-medium text-gray-900">
                Enable Corporate Identifier Validation
                <span className="ml-2 text-[10px] font-medium text-indigo-500 bg-indigo-50 px-1.5 py-0.5 rounded">Preview</span>
              </p>
              <p className="text-sm text-gray-500">
                Validates devices against Intune Corporate Device Identifiers (manufacturer + model + serial number).
                Uses the <strong>DeviceManagementServiceConfig.ReadWrite.All</strong> permission.
              </p>
            </div>
            <button
              onClick={() => {
                if (validateCorporateIdentifier) {
                  setValidateCorporateIdentifier(false);
                } else {
                  // If autopilot is already on, consent exists — just enable
                  if (validateAutopilotDevice) {
                    setValidateCorporateIdentifier(true);
                  } else {
                    onBeginConsent('corporate');
                  }
                }
              }}
              disabled={saving || autopilotConsentInProgress}
              className={`relative inline-flex h-8 w-14 shrink-0 items-center rounded-full transition-colors disabled:opacity-60 disabled:cursor-not-allowed ${validateCorporateIdentifier ? 'bg-green-600' : 'bg-gray-300'}`}
            >
              <span className={`inline-block h-6 w-6 transform rounded-full bg-white transition-transform ${validateCorporateIdentifier ? 'translate-x-7' : 'translate-x-1'}`} />
            </button>
          </label>
        )}

        <div className="bg-amber-50 border border-amber-200 rounded-lg p-3">
          <p className="text-sm text-amber-900">
            <strong>Important:</strong>{" "}
            {previewMode
              ? "If both validations are disabled, backend agent endpoints reject requests for this tenant. Enable at least one and complete admin consent first."
              : "If this is disabled, backend agent endpoints reject requests for this tenant. Use this toggle and complete admin consent first."}
          </p>
        </div>

        {autopilotConsentInProgress && (
          <div className="bg-blue-50 border border-blue-200 rounded-lg p-3 text-sm text-blue-800">
            Checking or applying admin consent...
          </div>
        )}
      </div>
    </div>
  );
}
