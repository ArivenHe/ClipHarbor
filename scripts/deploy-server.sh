#!/usr/bin/env bash
set -euo pipefail
# Invoked remotely through the password-authenticated GitHub Actions SSH session.
root=$(pwd -P)
release=${1:?Missing commit}
[[ "$release" =~ ^[0-9a-f]{40}$ ]] || { echo 'Invalid release identifier' >&2; exit 1; }
slot=${2:-$release}
[[ "$slot" =~ ^[0-9a-f]{40}(-[0-9a-f]{12})?$ ]] || { echo 'Invalid deployment slot' >&2; exit 1; }
directory="$root/releases/$slot"
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
set -a
# shellcheck disable=SC1091
source runtime.env
set +a
set_profile() {
  case "${SYNC_PROXY_MODE:-standalone}" in
    standalone) export COMPOSE_PROFILES=standalone;;
    external) unset COMPOSE_PROFILES;;
    *) echo 'Invalid proxy mode' >&2; exit 1;;
  esac
}
set_profile
previous=$(readlink "$root/current" || true)
rollback() {
  if [ -n "$previous" ] && [ -d "$root/$previous" ]; then
    cd "$root/$previous"
    unset SYNC_PROXY_MODE
    set -a
    # shellcheck disable=SC1091
    source runtime.env
    set +a
    set_profile
    docker compose --env-file runtime.env -f compose.yml up -d --no-build --pull never >/dev/null
    echo 'Health verification failed; previous release restored.' >&2
  else
    echo 'Health verification failed; inspect server logs. Data volumes have been retained.' >&2
  fi
}
if ! docker compose --env-file runtime.env -f compose.yml up -d --no-build --pull never; then rollback; exit 1; fi
if [ "${SYNC_PROXY_MODE:-standalone}" = external ]; then
  health_url="http://127.0.0.1:${SYNC_PORT:-5890}/healthz"
else
  health_url="https://$SYNC_DOMAIN/healthz"
fi
healthy=false
for _attempt in $(seq 1 30); do
  response=$(curl --noproxy '*' --fail --silent --show-error --connect-timeout 5 --max-time 10 "$health_url" 2>/dev/null || true)
  if HEALTH_RESPONSE="$response" EXPECTED_COMMIT="$release" python3 -c 'import json,os,sys; r=json.loads(os.environ["HEALTH_RESPONSE"] or "{}"); sys.exit(0 if r.get("status")=="ok" and r.get("commit")==os.environ["EXPECTED_COMMIT"] else 1)' 2>/dev/null; then healthy=true; break; fi
  sleep 2
done
if [ "$healthy" != true ]; then docker compose --env-file runtime.env -f compose.yml logs --tail 40 server >&2 || true; rollback; exit 1; fi
ln -sfn "releases/$slot" "$root/current.next"
mv -Tf "$root/current.next" "$root/current"
echo "Deployed and verified $release"
if [ "${SYNC_PROXY_MODE:-standalone}" = external ]; then echo "Backend ready on 127.0.0.1:${SYNC_PORT:-5890}; configure the existing HTTPS proxy for $SYNC_DOMAIN."; fi
