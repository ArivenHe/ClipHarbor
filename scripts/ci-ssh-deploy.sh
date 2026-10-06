#!/usr/bin/env bash
set -euo pipefail
: "${SERVER_SSH_HOST:?}" "${SERVER_SSH_USER:?}" "${SSHPASS:?}" "${SERVER_SSH_KNOWN_HOSTS:?}" "${SYNC_DOMAIN:?}" "${SYNC_ADMIN_USERNAME:?}" "${SYNC_ADMIN_PASSWORD:?}" "${SYNC_DATABASE_PASSWORD:?}"
commit=${DEPLOY_COMMIT:-${GITHUB_SHA:?}}
sync_port=${SYNC_PORT:-5890}
proxy_mode=${SYNC_PROXY_MODE:-standalone}
[[ "$commit" =~ ^[0-9a-f]{40}$ && "$sync_port" =~ ^[0-9]+$ && "$sync_port" -ge 1024 && "$sync_port" -le 65535 ]] || { echo 'Invalid commit or application port' >&2; exit 1; }
[[ "$proxy_mode" == standalone || "$proxy_mode" == external ]] || { echo 'Invalid proxy mode' >&2; exit 1; }
port=${SERVER_SSH_PORT:-22}
target=${SERVER_DEPLOY_DIR:-/opt/clipharbor}
[[ "$SERVER_SSH_HOST" =~ ^[a-zA-Z0-9.-]+$ && "$SERVER_SSH_USER" =~ ^[a-zA-Z0-9_-]+$ && "$port" =~ ^[0-9]+$ ]] || { echo 'Invalid SSH target' >&2; exit 1; }
[[ "$target" =~ ^/[a-zA-Z0-9_./-]+$ && "$target" != / && "$target" != *..* ]] || { echo 'Invalid deployment directory' >&2; exit 1; }
[[ "$SYNC_DOMAIN" =~ ^[a-zA-Z0-9.-]+$ && "$SYNC_ADMIN_USERNAME" =~ ^[a-zA-Z0-9_@.-]+$ ]] || { echo 'Invalid public settings' >&2; exit 1; }
install -d -m 700 "$RUNNER_TEMP/clipharbor-ssh"
printf '%s\n' "$SERVER_SSH_KNOWN_HOSTS" > "$RUNNER_TEMP/clipharbor-ssh/known_hosts"
chmod 600 "$RUNNER_TEMP/clipharbor-ssh/known_hosts"
options=(-o StrictHostKeyChecking=yes -o UserKnownHostsFile="$RUNNER_TEMP/clipharbor-ssh/known_hosts" -o 'PreferredAuthentications=password,keyboard-interactive' -o PubkeyAuthentication=no -o ConnectTimeout=15)
destination="$SERVER_SSH_USER@$SERVER_SSH_HOST"
mkdir -p bundle/secrets
chmod 700 bundle/secrets
printf '%s' "$SYNC_DATABASE_PASSWORD" > bundle/secrets/database_password
printf '%s' "$SYNC_ADMIN_PASSWORD" > bundle/secrets/admin_password
chmod 600 bundle/secrets/*
printf 'SYNC_DOMAIN=%s\nSYNC_ADMIN_USERNAME=%s\nSYNC_PORT=%s\nSYNC_PROXY_MODE=%s\n' "$SYNC_DOMAIN" "$SYNC_ADMIN_USERNAME" "$sync_port" "$proxy_mode" >> bundle/runtime.env
# A separate directory also preserves rollback when changing only deployment settings.
configuration=$(cat bundle/SHA256SUMS.txt bundle/runtime.env | sha256sum | cut -c1-12)
slot="$commit-$configuration"
remote="$target/releases/$slot"
cleanup() { rm -rf bundle/secrets "$RUNNER_TEMP/clipharbor-ssh"; }
trap cleanup EXIT
sshpass -e ssh "${options[@]}" -p "$port" "$destination" "umask 077; mkdir -p '$remote'"
sshpass -e scp "${options[@]}" -P "$port" -r bundle/. "$destination:$remote/"
sshpass -e ssh "${options[@]}" -p "$port" "$destination" "cd '$target' && bash 'releases/$slot/deploy-server.sh' '$commit' '$slot'"
