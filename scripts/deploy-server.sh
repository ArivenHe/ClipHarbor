#!/usr/bin/env bash
set -euo pipefail
# Invoked remotely through the password-authenticated GitHub Actions SSH session.
root=$(pwd -P)
release=${1:?Missing commit}
[[ "$release" =~ ^[0-9a-f]{40}$ ]] || { echo 'Invalid release identifier' >&2; exit 1; }
directory="$root/releases/$release"
cd "$directory"
sha256sum -c SHA256SUMS.txt
test "$(cat COMMIT)" = "$release"
command -v docker >/dev/null
docker compose version >/dev/null
docker load --input images.tar.gz >/dev/null
mkdir -p "$root/secrets"
chmod 700 "$root/secrets"
# Credentials are sent as protected files and installed only on first deployment.
for name in database_password admin_password; do
  if [ ! -s "$root/secrets/$name" ]; then install -m 444 "$directory/secrets/$name" "$root/secrets/$name"; fi
done
rm -rf "$directory/secrets"
export SECRETS_DIR="$root/secrets"
previous=$(readlink "$root/current" || true)
rollback() {
  if [ -n "$previous" ] && [ -d "$root/$previous" ]; then
    cd "$root/$previous"
    docker compose --env-file runtime.env -f compose.yml up -d --no-build --pull never >/dev/null
    echo 'Health verification failed; previous release restored.' >&2
  else
    echo 'Health verification failed; inspect server logs. Data volumes have been retained.' >&2
  fi
}
if ! docker compose --env-file runtime.env -f compose.yml up -d --no-build --pull never; then rollback; exit 1; fi
set -a
# shellcheck disable=SC1091
source runtime.env
set +a
healthy=false
for _attempt in $(seq 1 30); do
  response=$(curl --fail --silent --show-error --connect-timeout 5 --max-time 10 "https://$SYNC_DOMAIN/healthz" 2>/dev/null || true)
  if HEALTH_RESPONSE="$response" EXPECTED_COMMIT="$release" python3 -c 'import json,os,sys; r=json.loads(os.environ["HEALTH_RESPONSE"] or "{}"); sys.exit(0 if r.get("status")=="ok" and r.get("commit")==os.environ["EXPECTED_COMMIT"] else 1)' 2>/dev/null; then healthy=true; break; fi
  sleep 2
done
if [ "$healthy" != true ]; then rollback; exit 1; fi
ln -sfn "releases/$release" "$root/current.next"
mv -Tf "$root/current.next" "$root/current"
echo "Deployed and verified $release"
