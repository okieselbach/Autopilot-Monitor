#!/bin/bash
# Helper script to query Azure Table Storage via REST API
# Usage: query-table.sh <table_name> <odata_filter> [select_fields] [top_count]
#
# Requires AUTOPILOT_MONITOR_TABLE_CS environment variable with format:
# TableEndpoint=https://....table.core.windows.net/;SharedAccessSignature=sv=...

TABLE_NAME="$1"
FILTER="$2"
SELECT="$3"

if [ -z "$TABLE_NAME" ] || [ -z "$FILTER" ]; then
  echo "Usage: query-table.sh <table_name> <odata_filter> [select_fields]"
  exit 1
fi

if [ -z "$AUTOPILOT_MONITOR_TABLE_CS" ]; then
  echo "ERROR: AUTOPILOT_MONITOR_TABLE_CS environment variable not set"
  exit 1
fi

# Parse connection string
ENDPOINT=$(echo "$AUTOPILOT_MONITOR_TABLE_CS" | sed -n 's/.*TableEndpoint=\([^;]*\).*/\1/p' | sed 's:/*$::')
SAS=$(echo "$AUTOPILOT_MONITOR_TABLE_CS" | sed -n 's/.*SharedAccessSignature=\(.*\)/\1/p')

if [ -z "$ENDPOINT" ] || [ -z "$SAS" ]; then
  echo "ERROR: Could not parse connection string (need TableEndpoint and SharedAccessSignature)"
  exit 1
fi

# URL-encode the OData filter for use in query string
ENCODED_FILTER=$(python3 -c "import urllib.parse; print(urllib.parse.quote('$FILTER', safe=''))" 2>/dev/null)
if [ -z "$ENCODED_FILTER" ]; then
  # Fallback: manual encoding of common OData chars
  ENCODED_FILTER=$(echo "$FILTER" | sed "s/ /%20/g; s/'/%27/g")
fi

# Build full URL with query params
QUERY_URL="${ENDPOINT}/${TABLE_NAME}()?${SAS}&\$filter=${ENCODED_FILTER}"
if [ -n "$SELECT" ]; then
  ENCODED_SELECT=$(python3 -c "import urllib.parse; print(urllib.parse.quote('$SELECT', safe=''))" 2>/dev/null)
  if [ -z "$ENCODED_SELECT" ]; then
    ENCODED_SELECT="$SELECT"
  fi
  QUERY_URL="${QUERY_URL}&\$select=${ENCODED_SELECT}"
fi
TOP="$4"
if [ -n "$TOP" ]; then
  QUERY_URL="${QUERY_URL}&\$top=${TOP}"
fi

# Execute query
curl -s -H "Accept: application/json;odata=nometadata" \
     -H "x-ms-version: 2024-11-04" \
     --globoff \
     "$QUERY_URL"
