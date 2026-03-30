import type { ToolAnnotations } from '@modelcontextprotocol/sdk/types.js';

/** Read-only query tool — no side effects, idempotent, closed-world (our backend only). */
export const READ_ONLY: ToolAnnotations = {
  readOnlyHint: true,
  destructiveHint: false,
  idempotentHint: true,
  openWorldHint: false,
};

/** KQL / raw log query — read-only but open-world (arbitrary KQL against App Insights). */
export const READ_ONLY_OPEN: ToolAnnotations = {
  readOnlyHint: true,
  destructiveHint: false,
  idempotentHint: true,
  openWorldHint: true,
};
