export function SectionGatherRules() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8 space-y-8">
      <div className="flex items-center space-x-3 mb-2">
        <svg className="w-8 h-8 text-indigo-500 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Gather Rules</h2>
      </div>

      <p className="text-gray-700 leading-relaxed">
        Gather Rules let you define <strong>what data the agent should collect</strong> from the device during enrollment.
        Each rule specifies a <strong>collector type</strong> (how to collect), a <strong>target</strong> (what to collect),
        optional <strong>parameters</strong> (filters and options), and a <strong>trigger</strong> (when to collect).
        Results are sent as events to the backend and appear in the session timeline.
      </p>

      <div className="p-4 bg-blue-50 border border-blue-200 rounded-lg text-sm text-blue-900">
        <p className="font-semibold mb-1">Security</p>
        <p>All collector types enforce allowlists on the agent to prevent unauthorized data access.
        Registry paths, file paths, WMI queries, and commands are validated against hardcoded allowlists before execution.
        If a rule targets a disallowed resource, the agent emits a <code className="bg-blue-100 px-1 rounded">security_warning</code> event instead.</p>
      </div>

      {/* ── Collector Types ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">Collector Types</h3>

      {/* Registry */}
      <div className="border border-gray-200 rounded-lg overflow-hidden">
        <div className="px-4 py-2.5 bg-gray-100 border-b border-gray-200">
          <p className="font-semibold text-sm text-gray-900">Registry</p>
        </div>
        <div className="px-4 py-4 space-y-3 text-sm text-gray-700">
          <p>Reads values from the Windows Registry.</p>
          <div>
            <p className="font-medium text-gray-900">Target</p>
            <p>Full registry path including hive prefix.</p>
            <code className="block mt-1 bg-gray-50 border border-gray-200 rounded px-3 py-2 text-xs">HKLM\SOFTWARE\Microsoft\Enrollments</code>
          </div>
          <div>
            <p className="font-medium text-gray-900">Parameters</p>
            <ul className="list-disc ml-5 space-y-1">
              <li><code className="bg-gray-100 px-1 rounded">valueName</code> — Read a specific value. Leave empty to read all values in the key (max 50).</li>
            </ul>
          </div>
          <div>
            <p className="font-medium text-gray-900">Allowed Prefixes</p>
            <p className="text-xs text-gray-500 mt-0.5">All paths are under <code className="bg-gray-100 px-1 rounded">HKLM\</code> or <code className="bg-gray-100 px-1 rounded">HKCU\</code>. Segment-bounded matching — subkeys are allowed, but sibling keys are not.</p>
            <div className="mt-1 bg-gray-50 border border-gray-200 rounded px-3 py-2 text-xs font-mono space-y-1">
              <p className="text-gray-400 font-sans not-italic">MDM / Enrollment</p>
              <p>SOFTWARE\Microsoft\Enrollments</p>
              <p>SOFTWARE\Microsoft\EnterpriseDesktopAppManagement</p>
              <p>SOFTWARE\Microsoft\Provisioning</p>
              <p>SOFTWARE\Microsoft\PolicyManager</p>
              <p>SOFTWARE\Microsoft\Windows\CurrentVersion\MDM</p>
              <p className="text-gray-400 font-sans not-italic pt-1">AAD / Entra Join</p>
              <p>SOFTWARE\Microsoft\IdentityStore</p>
              <p>SYSTEM\CurrentControlSet\Control\CloudDomainJoin</p>
              <p className="text-gray-400 font-sans not-italic pt-1">Windows Update / WUfB</p>
              <p>SOFTWARE\Microsoft\WindowsUpdate</p>
              <p>SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate</p>
              <p className="text-gray-400 font-sans not-italic pt-1">BitLocker</p>
              <p>SOFTWARE\Microsoft\BitLocker</p>
              <p>SYSTEM\CurrentControlSet\Control\BitLockerStatus</p>
              <p className="text-gray-400 font-sans not-italic pt-1">Network / Proxy</p>
              <p>SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings</p>
              <p>SYSTEM\CurrentControlSet\Services\Tcpip</p>
              <p className="text-gray-400 font-sans not-italic pt-1">Autopilot / OOBE / Setup</p>
              <p>SOFTWARE\Microsoft\Windows\CurrentVersion\Setup</p>
              <p>SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE</p>
              <p>SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon</p>
              <p className="text-gray-400 font-sans not-italic pt-1">TPM</p>
              <p>SYSTEM\CurrentControlSet\Services\TPM</p>
              <p>SOFTWARE\Microsoft\Tpm</p>
              <p className="text-gray-400 font-sans not-italic pt-1">Intune IME</p>
              <p>SOFTWARE\Microsoft\IntuneManagementExtension</p>
              <p className="text-gray-400 font-sans not-italic pt-1">Certificates (SCEP)</p>
              <p>SOFTWARE\Microsoft\SystemCertificates</p>
              <p>SOFTWARE\Policies\Microsoft\SystemCertificates</p>
            </div>
          </div>
          <div className="p-3 bg-green-50 border border-green-200 rounded-lg">
            <p className="font-semibold text-green-900 text-xs mb-1">Example</p>
            <p className="text-xs text-green-800">Read the BitLocker recovery key status:</p>
            <code className="block mt-1 text-xs">Target: <strong>HKLM\SYSTEM\CurrentControlSet\Control\BitLockerStatus</strong></code>
          </div>
        </div>
      </div>

      {/* Event Log */}
      <div className="border border-gray-200 rounded-lg overflow-hidden">
        <div className="px-4 py-2.5 bg-gray-100 border-b border-gray-200">
          <p className="font-semibold text-sm text-gray-900">Event Log</p>
        </div>
        <div className="px-4 py-4 space-y-3 text-sm text-gray-700">
          <p>Reads entries from Windows Event Logs — supports both <strong>classic logs</strong> (Application, System, Security)
          and <strong>operational/analytic logs</strong> (e.g., <code className="bg-gray-100 px-1 rounded">Microsoft-Windows-Shell-Core/Operational</code>).</p>
          <div>
            <p className="font-medium text-gray-900">Target</p>
            <p>The full event log name.</p>
            <code className="block mt-1 bg-gray-50 border border-gray-200 rounded px-3 py-2 text-xs">Microsoft-Windows-Shell-Core/Operational</code>
          </div>
          <div>
            <p className="font-medium text-gray-900">Parameters</p>
            <ul className="list-disc ml-5 space-y-1">
              <li><code className="bg-gray-100 px-1 rounded">eventId</code> — Filter by a specific Event ID (e.g., <code className="bg-gray-100 px-1 rounded">62407</code>). Leave empty for all events.</li>
              <li><code className="bg-gray-100 px-1 rounded">messageFilter</code> — Contains-filter on the event message. Use <code className="bg-gray-100 px-1 rounded">*</code> as wildcard (e.g., <code className="bg-gray-100 px-1 rounded">*ESPProgress*</code>).</li>
              <li><code className="bg-gray-100 px-1 rounded">maxEntries</code> — Max events to return (1–50, default: 10).</li>
              <li><code className="bg-gray-100 px-1 rounded">source</code> — Filter by provider/source name.</li>
            </ul>
          </div>
          <div className="p-3 bg-green-50 border border-green-200 rounded-lg">
            <p className="font-semibold text-green-900 text-xs mb-1">Example — ESP Progress Telemetry</p>
            <p className="text-xs text-green-800">Collect Shell-Core ESP progress events at enrollment completion:</p>
            <div className="mt-1 text-xs space-y-0.5">
              <p>Target: <strong>Microsoft-Windows-Shell-Core/Operational</strong></p>
              <p>Event ID: <strong>62407</strong></p>
              <p>Message Filter: <strong>*ESPProgress*</strong></p>
              <p>Max Entries: <strong>50</strong></p>
              <p>Trigger: <strong>On Event</strong> → <code className="bg-green-100 px-1 rounded">enrollment_complete</code></p>
            </div>
          </div>
        </div>
      </div>

      {/* WMI Query */}
      <div className="border border-gray-200 rounded-lg overflow-hidden">
        <div className="px-4 py-2.5 bg-gray-100 border-b border-gray-200">
          <p className="font-semibold text-sm text-gray-900">WMI Query</p>
        </div>
        <div className="px-4 py-4 space-y-3 text-sm text-gray-700">
          <p>Executes a WMI/CIM query using full <strong>WQL syntax</strong>. The target must be a complete <code className="bg-gray-100 px-1 rounded">SELECT</code> statement.</p>
          <div>
            <p className="font-medium text-gray-900">Target</p>
            <p>Full WQL query string. Must start with an allowed class prefix.</p>
            <code className="block mt-1 bg-gray-50 border border-gray-200 rounded px-3 py-2 text-xs">SELECT * FROM Win32_BIOS</code>
          </div>
          <div>
            <p className="font-medium text-gray-900">Allowed WMI Classes</p>
            <div className="mt-1 bg-gray-50 border border-gray-200 rounded px-3 py-2 text-xs font-mono columns-2 space-y-0.5">
              <p>Win32_OperatingSystem</p>
              <p>Win32_ComputerSystem</p>
              <p>Win32_BIOS</p>
              <p>Win32_Processor</p>
              <p>Win32_BaseBoard</p>
              <p>Win32_Battery</p>
              <p>Win32_TPM</p>
              <p>Win32_NetworkAdapter</p>
              <p>Win32_NetworkAdapterConfiguration</p>
              <p>Win32_DiskDrive</p>
              <p>Win32_LogicalDisk</p>
              <p>SoftwareLicensingProduct</p>
            </div>
          </div>
          <div className="p-3 bg-green-50 border border-green-200 rounded-lg">
            <p className="font-semibold text-green-900 text-xs mb-1">Example — Network Adapter Monitoring</p>
            <p className="text-xs text-green-800">Monitor network adapters every 30 seconds during enrollment:</p>
            <div className="mt-1 text-xs space-y-0.5">
              <p>Target: <strong>SELECT * FROM Win32_NetworkAdapterConfiguration</strong></p>
              <p>Trigger: <strong>Interval</strong> → 30 seconds</p>
            </div>
          </div>
        </div>
      </div>

      {/* File */}
      <div className="border border-gray-200 rounded-lg overflow-hidden">
        <div className="px-4 py-2.5 bg-gray-100 border-b border-gray-200">
          <p className="font-semibold text-sm text-gray-900">File</p>
        </div>
        <div className="px-4 py-4 space-y-3 text-sm text-gray-700">
          <p>Checks file or directory existence and optionally reads file content. Environment variables are expanded.</p>
          <div>
            <p className="font-medium text-gray-900">Target</p>
            <p>File or directory path. Environment variables like <code className="bg-gray-100 px-1 rounded">%ProgramData%</code> are supported.</p>
            <code className="block mt-1 bg-gray-50 border border-gray-200 rounded px-3 py-2 text-xs">C:\Windows\Panther\UnattendGC\setupact.log</code>
          </div>
          <div>
            <p className="font-medium text-gray-900">Parameters</p>
            <ul className="list-disc ml-5 space-y-1">
              <li><code className="bg-gray-100 px-1 rounded">readContent</code> — Set to <code className="bg-gray-100 px-1 rounded">true</code> to read file content (files must be &lt;50 KB). The agent reads the <strong>last 4000 characters</strong> — most relevant for log files where recent entries are at the end.</li>
            </ul>
          </div>
          <div>
            <p className="font-medium text-gray-900">Allowed Path Prefixes</p>
            <div className="mt-1 bg-gray-50 border border-gray-200 rounded px-3 py-2 text-xs font-mono space-y-0.5">
              <p>C:\ProgramData\Microsoft\IntuneManagementExtension</p>
              <p>C:\Windows\CCM\Logs</p>
              <p>C:\Windows\Logs</p>
              <p>C:\Windows\Panther</p>
              <p>C:\Windows\SetupDiag</p>
              <p>C:\ProgramData\Microsoft\DiagnosticLogCSP</p>
              <p>C:\Windows\SoftwareDistribution\ReportingEvents.log</p>
            </div>
          </div>
          <div className="p-3 bg-green-50 border border-green-200 rounded-lg">
            <p className="font-semibold text-green-900 text-xs mb-1">Example</p>
            <p className="text-xs text-green-800">Read the Panther setup log on failure:</p>
            <div className="mt-1 text-xs space-y-0.5">
              <p>Target: <strong>C:\Windows\Panther\setuperr.log</strong></p>
              <p>Parameters: <code className="bg-green-100 px-1 rounded">readContent: true</code></p>
              <p>Trigger: <strong>On Event</strong> → <code className="bg-green-100 px-1 rounded">enrollment_failed</code></p>
            </div>
          </div>
        </div>
      </div>

      {/* Command (Allowlisted) */}
      <div className="border border-gray-200 rounded-lg overflow-hidden">
        <div className="px-4 py-2.5 bg-gray-100 border-b border-gray-200">
          <p className="font-semibold text-sm text-gray-900">Command (Allowlisted)</p>
        </div>
        <div className="px-4 py-4 space-y-3 text-sm text-gray-700">
          <p>Runs a pre-approved command (PowerShell or CLI). Only commands from the <strong>exact allowlist</strong> are permitted — custom commands are blocked.</p>
          <div>
            <p className="font-medium text-gray-900">Target</p>
            <p>The exact command string as it appears in the allowlist. Must match exactly (case-insensitive).</p>
          </div>
          <div>
            <p className="font-medium text-gray-900">Allowed Commands</p>
            <div className="mt-1 bg-gray-50 border border-gray-200 rounded px-3 py-2 text-xs font-mono space-y-0.5">
              <p className="text-gray-400"># TPM and Security</p>
              <p>Get-Tpm</p>
              <p>Get-SecureBootPolicy</p>
              <p>Get-SecureBootUEFI -Name SetupMode</p>
              <p className="text-gray-400"># BitLocker</p>
              <p>Get-BitLockerVolume -MountPoint C:</p>
              <p className="text-gray-400"># Network</p>
              <p>Get-NetAdapter | Select-Object Name, Status, InterfaceDescription, MacAddress, LinkSpeed</p>
              <p>Get-DnsClientServerAddress | Select-Object InterfaceAlias, ServerAddresses</p>
              <p>Get-NetIPConfiguration | Select-Object InterfaceAlias, IPv4Address, IPv4DefaultGateway, DNSServer</p>
              <p>netsh winhttp show proxy</p>
              <p>ipconfig /all</p>
              <p className="text-gray-400"># Domain / Identity</p>
              <p>nltest /dsgetdc:</p>
              <p>dsregcmd /status</p>
              <p className="text-gray-400"># Certificate</p>
              <p>certutil -store My</p>
              <p className="text-gray-400"># Windows Update</p>
              <p>Get-HotFix | Select-Object -First 10 HotFixID, InstalledOn, Description</p>
            </div>
          </div>
          <div className="p-3 bg-green-50 border border-green-200 rounded-lg">
            <p className="font-semibold text-green-900 text-xs mb-1">Example — TPM at Enrollment Complete</p>
            <div className="mt-1 text-xs space-y-0.5">
              <p>Target: <strong>Get-Tpm</strong></p>
              <p>Trigger: <strong>On Event</strong> → <code className="bg-green-100 px-1 rounded">enrollment_complete</code></p>
            </div>
          </div>
        </div>
      </div>

      {/* Log Parser */}
      <div className="border border-gray-200 rounded-lg overflow-hidden">
        <div className="px-4 py-2.5 bg-gray-100 border-b border-gray-200">
          <p className="font-semibold text-sm text-gray-900">Log Parser</p>
        </div>
        <div className="px-4 py-4 space-y-3 text-sm text-gray-700">
          <p>Parses <strong>CMTrace-format</strong> log files using regex patterns with named capture groups.
          Each match emits a separate event. Supports position tracking to resume from the last read position.</p>
          <div>
            <p className="font-medium text-gray-900">Target</p>
            <p>Path to a CMTrace-format log file. Environment variables are expanded.</p>
            <code className="block mt-1 bg-gray-50 border border-gray-200 rounded px-3 py-2 text-xs">%ProgramData%\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log</code>
          </div>
          <div>
            <p className="font-medium text-gray-900">Parameters</p>
            <ul className="list-disc ml-5 space-y-1">
              <li><code className="bg-gray-100 px-1 rounded">pattern</code> <span className="text-red-500">(required)</span> — Regex with named capture groups, e.g., <code className="bg-gray-100 px-1 rounded">{`(?<appName>\\w+)`}</code></li>
              <li><code className="bg-gray-100 px-1 rounded">trackPosition</code> — <code className="bg-gray-100 px-1 rounded">true</code> (default) to resume from last read position across executions.</li>
              <li><code className="bg-gray-100 px-1 rounded">maxLines</code> — Max lines to read per execution (default: 1000).</li>
            </ul>
          </div>
          <div className="p-3 bg-green-50 border border-green-200 rounded-lg">
            <p className="font-semibold text-green-900 text-xs mb-1">Example — IME App Workload Parsing</p>
            <div className="mt-1 text-xs space-y-0.5">
              <p>Target: <strong>%ProgramData%\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log</strong></p>
              <p>Pattern: <strong>{`(?<action>Install|Uninstall).*(?<appName>[A-Za-z0-9_-]+)`}</strong></p>
              <p>Trigger: <strong>Interval</strong> → 30 seconds</p>
            </div>
          </div>
        </div>
      </div>

      {/* ── Trigger Types ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">Trigger Types</h3>

      <p className="text-gray-700 text-sm">Triggers define <strong>when</strong> a gather rule executes. Choose the trigger that matches your collection needs.</p>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div className="border border-gray-200 rounded-lg p-4">
          <p className="font-semibold text-gray-900 text-sm mb-1">Startup</p>
          <p className="text-xs text-gray-600">Runs once when the agent starts monitoring. Use for collecting initial device state (BIOS, TPM, OS info).</p>
        </div>
        <div className="border border-gray-200 rounded-lg p-4">
          <p className="font-semibold text-gray-900 text-sm mb-1">Interval</p>
          <p className="text-xs text-gray-600">Runs repeatedly at a configurable interval (5–3600 seconds). Use for continuous monitoring like network status or policy changes.</p>
        </div>
        <div className="border border-gray-200 rounded-lg p-4">
          <p className="font-semibold text-gray-900 text-sm mb-1">Phase Change</p>
          <p className="text-xs text-gray-600">Runs when enrollment transitions to a specific phase. Valid phases:</p>
          <p className="text-xs text-gray-500 mt-1 font-mono">Start, DevicePreparation, DeviceSetup, AppsDevice, AccountSetup, AppsUser, FinalizingSetup, Complete, Failed</p>
        </div>
        <div className="border border-gray-200 rounded-lg p-4">
          <p className="font-semibold text-gray-900 text-sm mb-1">On Event</p>
          <p className="text-xs text-gray-600">Runs when a specific event type is emitted by the agent. Common event types:</p>
          <p className="text-xs text-gray-500 mt-1 font-mono">enrollment_complete, enrollment_failed, app_install_failed, app_install_succeeded, phase_change</p>
          <div className="mt-2 p-2 bg-amber-50 border border-amber-200 rounded text-xs text-amber-800">
            <strong>Tip:</strong> Use <code className="bg-amber-100 px-1 rounded">enrollment_complete</code> or <code className="bg-amber-100 px-1 rounded">enrollment_failed</code> to collect data &quot;at the end&quot; of enrollment.
          </div>
        </div>
      </div>

      {/* ── Output ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">Output</h3>

      <p className="text-gray-700 text-sm">
        Each gather rule execution emits an event with the configured <strong>Output Event Type</strong> and <strong>Severity</strong>.
        The collected data is stored in the event&apos;s <code className="bg-gray-100 px-1 rounded">data</code> field as key-value pairs.
        These events appear in the session timeline and can be exported via CSV or timeline export.
      </p>
    </section>
  );
}
