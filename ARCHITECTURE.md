# Architecture

```text
+----------------------------------------------------------------------------------+
|                         Autopilot Monitor - Architecture                         |
+----------------------------------------------------------------------------------+

 [Admin/User Browser]
          |
          | HTTPS
          v
+-----------------------------+      OIDC        +-------------------------------+
| Web App (Next.js)           |<---------------->| Microsoft Identity (Entra ID) |
| Dashboard / Progress / Docs |                  | Auth / Authorization          |
+-----------------------------+                  +-------------------------------+
          ^    ^
          |    |
          |    | Live updates
          |    |
          |    |                         +---------------------------+
          |    +-------------------------| SignalR Hub               |
          |                              | Real-time event stream    |
          |                              +---------------------------+
          |                                          ^
          | REST / API                               |
          v                                          | events
+---------------------------+        Data &          |
| Azure Functions Backend   |------> Integrations ---+
| APIs / Ingestion / Rules  |               \
+---------------------------+                \       +---------------------------+
          ^                                   +----->| Storage Tables            |
          | telemetry/events                         | Sessions / Events / Rules |
          |                                          +---------------------------+
          |                                          +---------------------------+
+---------------------------+                        | Notifications             |
| Agent on Windows Devices  |                        | Teams / Webhooks ...      |
| Enrollment + app progress |                        +---------------------------+
+---------------------------+
