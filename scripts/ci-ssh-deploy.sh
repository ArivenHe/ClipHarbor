#!/usr/bin/env bash
set -euo pipefail
: "${SERVER_SSH_HOST:?}" "${SERVER_SSH_USER:?}" "${SSHPASS:?}" "${SERVER_SSH_KNOWN_HOSTS:?}" "${SYNC_DOMAIN:?}" "${SYNC_ADMIN_USERNAME:?}" "${SYNC_ADMIN_PASSWORD:?}" "${SYNC_DATABASE_PASSWORD:?}" "${GITHUB_SHA:?}"
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
remote="$target/releases/$GITHUB_SHA"
mkdir -p bundle/secrets
chmod 700 bundle/secrets
printf '%s' "$SYNC_DATABASE_PASSWORD" > bundle/secrets/database_password
printf '%s' "$SYNC_ADMIN_PASSWORD" > bundle/secrets/admin_password
chmod 600 bundle/secrets/*
printf 'SYNC_DOMAIN=%s\nSYNC_ADMIN_USERNAME=%s\n' "$SYNC_DOMAIN" "$SYNC_ADMIN_USERNAME" >> bundle/runtime.env
cleanup() { rm -rf bundle/secrets "$RUNNER_TEMP/clipharbor-ssh"; }
trap cleanup EXIT
sshpass -e ssh "${options[@]}" -p "$port" "$destination" "umask 077; mkdir -p '$remote'"
sshpass -e scp "${options[@]}" -P "$port" -r bundle/. "$destination:$remote/"
sshpass -e ssh "${options[@]}" -p "$port" "$destination" "cd '$target' && bash 'releases/$GITHUB_SHA/deploy-server.sh' '$GITHUB_SHA'"
