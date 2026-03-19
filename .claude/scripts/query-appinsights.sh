#!/bin/bash
# Helper script to query Application Insights via Azure CLI
# Usage: query-appinsights.sh "<KQL-Query>" [timespan]
#
# Requires AUTOPILOT_MONITOR_APPINSIGHTS_ID environment variable (App Insights App-ID GUID)
# Timespan format: ISO 8601 Duration (e.g., PT1H, PT24H, P7D). Default: PT1H

QUERY="$1"
TIMESPAN="${2:-PT1H}"

if [ -z "$QUERY" ]; then
  echo "Usage: query-appinsights.sh \"<KQL-Query>\" [timespan]"
  exit 1
fi

if [ -z "$AUTOPILOT_MONITOR_APPINSIGHTS_ID" ]; then
  echo "ERROR: AUTOPILOT_MONITOR_APPINSIGHTS_ID environment variable not set"
  echo "Set it to the Application Insights App ID (Azure Portal > API Access > Application ID)"
  exit 1
fi

# Ensure az CLI is in PATH (Windows/Git Bash may not have it)
if ! command -v az &>/dev/null; then
  for AZ_DIR in \
    "/c/Program Files/Microsoft SDKs/Azure/CLI2/wbin" \
    "/c/Program Files (x86)/Microsoft SDKs/Azure/CLI2/wbin"; do
    if [ -d "$AZ_DIR" ]; then
      export PATH="$AZ_DIR:$PATH"
      break
    fi
  done
  if ! command -v az &>/dev/null; then
    echo "ERROR: Azure CLI (az) not found. Install it or add it to PATH."
    exit 1
  fi
fi

# Execute query
RESULT=$(az monitor app-insights query \
  --app "$AUTOPILOT_MONITOR_APPINSIGHTS_ID" \
  --analytics-query "$QUERY" \
  --offset "$TIMESPAN" \
  2>&1)

EXIT_CODE=$?
if [ $EXIT_CODE -ne 0 ]; then
  echo "ERROR: az query failed (exit code $EXIT_CODE)"
  echo "$RESULT"
  exit $EXIT_CODE
fi

# Transform nested table format (columns+rows) into readable JSON array of objects
python3 -c "
import json, sys

data = json.loads(sys.stdin.read())
tables = data.get('tables', data.get('Tables', []))
if not tables:
    print('[]')
    sys.exit(0)

table = tables[0]
cols = [c['name'] for c in table['columns']]
rows = table['rows']

result = []
for row in rows:
    obj = {}
    for i, col in enumerate(cols):
        val = row[i] if i < len(row) else None
        if val is not None:
            obj[col] = val
    result.append(obj)

print(json.dumps(result, indent=2, default=str))
" <<< "$RESULT"
