"use client";

interface AgentSettingsSectionProps {
  enablePerformanceCollector: boolean;
  setEnablePerformanceCollector: (value: boolean) => void;
  performanceCollectorInterval: number;
  setPerformanceCollectorInterval: (value: number) => void;
  selfDestructOnComplete: boolean;
  setSelfDestructOnComplete: (value: boolean) => void;
  keepLogFile: boolean;
  setKeepLogFile: (value: boolean) => void;
  rebootOnComplete: boolean;
  setRebootOnComplete: (value: boolean) => void;
  rebootDelaySeconds: number;
  setRebootDelaySeconds: (value: number) => void;
  enableGeoLocation: boolean;
  setEnableGeoLocation: (value: boolean) => void;
  enableImeMatchLog: boolean;
  setEnableImeMatchLog: (value: boolean) => void;
  logLevel: string;
  setLogLevel: (value: string) => void;
}

export default function AgentSettingsSection({
  enablePerformanceCollector,
  setEnablePerformanceCollector,
  performanceCollectorInterval,
  setPerformanceCollectorInterval,
  selfDestructOnComplete,
  setSelfDestructOnComplete,
  keepLogFile,
  setKeepLogFile,
  rebootOnComplete,
  setRebootOnComplete,
  rebootDelaySeconds,
  setRebootDelaySeconds,
  enableGeoLocation,
  setEnableGeoLocation,
  enableImeMatchLog,
  setEnableImeMatchLog,
  logLevel,
  setLogLevel,
}: AgentSettingsSectionProps) {
  return (
    <>
      {/* Agent Collectors */}
      <div className="bg-white rounded-lg shadow">
        <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-emerald-50 to-teal-50">
          <div className="flex items-center space-x-2">
            <svg className="w-6 h-6 text-emerald-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
            </svg>
            <div>
              <h2 className="text-xl font-semibold text-gray-900">Agent Collectors</h2>
              <p className="text-sm text-gray-500 mt-1">Enable optional data collectors on enrolled devices. These generate additional telemetry traffic.</p>
            </div>
          </div>
        </div>
        <div className="p-6 space-y-5">
          {/* Performance Collector */}
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-emerald-200 transition-colors">
            <div className="flex-1">
              <div className="flex items-center space-x-2">
                <p className="font-medium text-gray-900">Performance Collector</p>
                <span className="text-xs px-2 py-0.5 rounded-full bg-yellow-100 text-yellow-700">Medium Traffic</span>
              </div>
              <p className="text-sm text-gray-500 mt-1">CPU, memory, disk metrics at configurable intervals</p>
              {enablePerformanceCollector && (
                <div className="mt-2 flex items-center space-x-2">
                  <span className="text-sm text-gray-600">Interval:</span>
                  <input
                    type="number"
                    min="30"
                    max="300"
                    value={performanceCollectorInterval}
                    onChange={(e) => setPerformanceCollectorInterval(parseInt(e.target.value) || 60)}
                    className="w-20 px-2 py-1 border border-gray-300 rounded text-sm text-gray-900 focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500"
                  />
                  <span className="text-sm text-gray-500">seconds</span>
                </div>
              )}
            </div>
            <button
              onClick={() => setEnablePerformanceCollector(!enablePerformanceCollector)}
              className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${enablePerformanceCollector ? 'bg-emerald-500' : 'bg-gray-300'}`}
            >
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enablePerformanceCollector ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>

        </div>
      </div>

      {/* Agent Parameters */}
      <div className="bg-white rounded-lg shadow">
        <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-violet-50 to-purple-50">
          <div className="flex items-center space-x-2">
            <svg className="w-6 h-6 text-violet-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6V4m0 2a2 2 0 100 4m0-4a2 2 0 110 4m-6 8a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4m6 6v10m6-2a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4" />
            </svg>
            <div>
              <h2 className="text-xl font-semibold text-gray-900">Agent Parameters</h2>
              <p className="text-sm text-gray-500 mt-1">Control agent behavior on enrolled devices. Changes take effect on the next agent config refresh.</p>
            </div>
          </div>
        </div>
        <div className="p-6 space-y-4">

          {/* Self-Destruct */}
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
            <div>
              <p className="font-medium text-gray-900">Self-Destruct on Complete</p>
              <p className="text-sm text-gray-500">Remove Scheduled Task and all agent files when enrollment completes</p>
            </div>
            <button onClick={() => setSelfDestructOnComplete(!selfDestructOnComplete)}
              className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${selfDestructOnComplete ? 'bg-violet-500' : 'bg-gray-300'}`}>
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${selfDestructOnComplete ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>

          {/* Keep Log File */}
          {selfDestructOnComplete && (
            <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors ml-4">
              <div>
                <p className="font-medium text-gray-900">Keep Log File</p>
                <p className="text-sm text-gray-500">Preserve the agent log during self-destruct (all other files are removed)</p>
              </div>
              <button onClick={() => setKeepLogFile(!keepLogFile)}
                className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${keepLogFile ? 'bg-violet-500' : 'bg-gray-300'}`}>
                <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${keepLogFile ? 'translate-x-6' : 'translate-x-1'}`} />
              </button>
            </div>
          )}

          {/* Reboot on Complete */}
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
            <div>
              <p className="font-medium text-gray-900">Reboot on Complete</p>
              <p className="text-sm text-gray-500">Reboot the device after enrollment completes (and after self-destruct if enabled)</p>
            </div>
            <button onClick={() => setRebootOnComplete(!rebootOnComplete)}
              className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${rebootOnComplete ? 'bg-violet-500' : 'bg-gray-300'}`}>
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${rebootOnComplete ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>

          {rebootOnComplete && (
            <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors ml-4">
              <div>
                <p className="font-medium text-gray-900">Reboot Delay</p>
                <p className="text-sm text-gray-500">Seconds before reboot is initiated — gives the user time to see what is happening</p>
              </div>
              <div className="flex items-center gap-2">
                <input
                  type="number"
                  min={0}
                  max={3600}
                  value={rebootDelaySeconds}
                  onChange={(e) => setRebootDelaySeconds(Math.max(0, parseInt(e.target.value) || 0))}
                  className="w-20 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 text-right focus:ring-2 focus:ring-violet-500 focus:border-violet-500"
                />
                <span className="text-sm text-gray-500 whitespace-nowrap">seconds</span>
              </div>
            </div>
          )}

          {/* Geo Location */}
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
            <div>
              <p className="font-medium text-gray-900">Geo-Location Detection</p>
              <p className="text-sm text-gray-500">Capture device location, ISP and network info at enrollment start (queries external IP service)</p>
            </div>
            <button onClick={() => setEnableGeoLocation(!enableGeoLocation)}
              className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${enableGeoLocation ? 'bg-violet-500' : 'bg-gray-300'}`}>
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableGeoLocation ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>

          {/* IME Match Log */}
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
            <div>
              <p className="font-medium text-gray-900">IME Pattern Match Log</p>
              <p className="text-sm text-gray-500">
                Write every matched IME log line to a local file for diagnostics
                {enableImeMatchLog && <span className="block text-xs text-gray-400 mt-0.5 font-mono">%ProgramData%\AutopilotMonitor\Logs\ime_pattern_matches.log</span>}
              </p>
            </div>
            <button onClick={() => setEnableImeMatchLog(!enableImeMatchLog)}
              className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${enableImeMatchLog ? 'bg-violet-500' : 'bg-gray-300'}`}>
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enableImeMatchLog ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </div>

          {/* Log Level */}
          <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-violet-200 transition-colors">
            <div>
              <p className="font-medium text-gray-900">Log Level</p>
              <p className="text-sm text-gray-500">Agent log verbosity — Info for normal operation, Debug for troubleshooting, Verbose for full tracing</p>
            </div>
            <select
              value={logLevel}
              onChange={(e) => setLogLevel(e.target.value)}
              className="px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 focus:ring-2 focus:ring-violet-500 focus:border-violet-500"
            >
              <option value="Info">Info</option>
              <option value="Debug">Debug</option>
              <option value="Verbose">Verbose</option>
            </select>
          </div>

        </div>
      </div>
    </>
  );
}
