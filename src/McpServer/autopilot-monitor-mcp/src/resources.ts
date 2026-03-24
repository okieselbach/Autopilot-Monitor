import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';

export function registerResources(server: McpServer): void {
  server.resource(
    'event_types',
    'autopilot://event-types',
    {
      mimeType: 'application/json',
      description: 'Catalog of all known enrollment event type strings. Consult this before calling search_sessions_by_event to know valid eventType values.',
    },
    async () => ({
      contents: [
        {
          uri: 'autopilot://event-types',
          mimeType: 'application/json',
          text: JSON.stringify(
            {
              phase_events: [
                'phase_transition',
                'esp_state_change',
                'completion_check',
                'enrollment_complete',
                'enrollment_failed',
                'desktop_arrived',
              ],
              app_events: [
                'app_install_started',
                'app_install_completed',
                'app_install_failed',
                'app_download_started',
                'app_install_skipped',
              ],
              network_events: ['network_state_change', 'network_connectivity_check'],
              device_info_events: [
                'os_info',
                'hardware_spec',
                'tpm_status',
                'autopilot_profile',
                'secureboot_status',
                'bitlocker_status',
                'network_adapters',
                'network_interface_info',
                'aad_join_status',
                'enrollment_type_detected',
              ],
              error_events: ['error_detected'],
              vulnerability_events: ['software_inventory_analysis', 'vulnerability_report'],
              other: [
                'performance_snapshot',
                'log_entry',
                'gather_result',
                'script_completed',
                'script_failed',
                'ime_agent_version',
              ],
            },
            null,
            2
          ),
        },
      ],
    })
  );
}
