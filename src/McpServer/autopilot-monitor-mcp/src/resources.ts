import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { EVENT_TYPES_CATALOG, DEVICE_PROPERTIES_CATALOG } from './resource-catalog.js';
import { DIAG_ZIP_MAP } from './diag-zip-map.js';

/**
 * MCP-protocol resources. Note that some clients (e.g. Claude Code's HTTP-MCP
 * bridge in stateless mode) do not expose `resources/list` correctly — for
 * those clients, use the `get_resource(name)` tool which returns the same
 * data via a regular tool call.
 */
export function registerResources(server: McpServer): void {
  server.registerResource(
    'event_types',
    'autopilot://event-types',
    {
      title: 'Event Types Catalog',
      mimeType: 'application/json',
      description: 'Catalog of all known enrollment event type strings. Consult this before calling search_sessions_by_event to know valid eventType values.',
    },
    async () => ({
      contents: [
        {
          uri: 'autopilot://event-types',
          mimeType: 'application/json',
          text: JSON.stringify(EVENT_TYPES_CATALOG, null, 2),
        },
      ],
    })
  );

  server.registerResource(
    'device_properties',
    'autopilot://device-properties',
    {
      title: 'Device Properties Catalog',
      mimeType: 'application/json',
      description:
        'Catalog of known device property keys for the deviceProperties filter in search_sessions. ' +
        'Keys use "eventType.propertyName" dot notation. New agent properties are searchable immediately ' +
        'even before being added to this catalog — this list aids discoverability.',
    },
    async () => ({
      contents: [
        {
          uri: 'autopilot://device-properties',
          mimeType: 'application/json',
          text: JSON.stringify(DEVICE_PROPERTIES_CATALOG, null, 2),
        },
      ],
    })
  );

  server.registerResource(
    'diag_zip_layout',
    'autopilot://diag-zip-layout',
    {
      title: 'Diagnostics ZIP Layout',
      mimeType: 'application/json',
      description:
        'Expected file layout of an agent diagnostics ZIP. get_session_diagnostics returns a download ' +
        'URL for the archive plus this map so the client can extract + analyze it locally (the backend ' +
        'never unzips it). Read files in priority order; AppWorkload*.log can be hundreds of MB → grep only.',
    },
    async () => ({
      contents: [
        {
          uri: 'autopilot://diag-zip-layout',
          mimeType: 'application/json',
          text: JSON.stringify(DIAG_ZIP_MAP, null, 2),
        },
      ],
    })
  );
}
