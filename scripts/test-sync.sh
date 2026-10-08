#!/usr/bin/env bash
set -euo pipefail
dotnet build server/ClipHarbor.Server/ClipHarbor.Server.csproj -c Release
dotnet build shared/ClipHarbor.SyncHost/ClipHarbor.SyncHost.csproj -c Release
password_file=$(mktemp)
log_file=$(mktemp)
server_pid=''
cleanup() { if [ -n "$server_pid" ]; then kill "$server_pid" 2>/dev/null || true; wait "$server_pid" 2>/dev/null || true; fi; rm -f "$password_file" "$log_file"; }
trap cleanup EXIT
printf '%s' "${CLIPHARBOR_TEST_PASSWORD:-integration-test-only}" > "$password_file"
chmod 600 "$password_file"
server=server/ClipHarbor.Server/bin/Release/net10.0/ClipHarbor.Server.dll
dotnet "$server" --create-account alice --password-file "$password_file"
dotnet "$server" --create-account bob --password-file "$password_file"
dotnet "$server" --create-admin consoleadmin --password-file "$password_file"
CLIPHARBOR_LOGIN_RATE_LIMIT=200 ASPNETCORE_URLS="${CLIPHARBOR_TEST_URL:-http://localhost:28080}" dotnet "$server" > "$log_file" 2>&1 &
server_pid=$!
for _attempt in $(seq 1 60); do
  if ! kill -0 "$server_pid" 2>/dev/null; then cat "$log_file"; exit 1; fi
  if curl --fail --silent "${CLIPHARBOR_TEST_URL:-http://localhost:28080}/healthz" >/dev/null; then break; fi
  sleep 1
done
if ! dotnet run --project integration/ClipHarbor.IntegrationTests/ClipHarbor.IntegrationTests.csproj -c Release; then cat "$log_file"; exit 1; fi
if ! python3 integration/test-native-bridge.py; then cat "$log_file"; exit 1; fi
if ! python3 integration/test-admin.py; then cat "$log_file"; exit 1; fi
