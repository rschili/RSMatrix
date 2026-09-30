#!/usr/bin/env bash
# Disposable local Synapse. Requires rootless Podman, Bash and curl (no Compose).
set -euo pipefail

HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
PROJECT_DIR=$(cd -- "$HERE/.." && pwd)
NAME=${MATRIX_CONTAINER_NAME:-rsmatrix-test-server}
PORT=${MATRIX_PORT:-8008}
IMAGE=${MATRIX_SYNAPSE_IMAGE:-docker.io/matrixdotorg/synapse:v1.162.0@sha256:6b84a7bbac36f080b2d2e51e0289cf1b08b349598ea44a558df38d558f2c2311}
LABEL=io.rsmatrix.test-server
URL="http://127.0.0.1:${PORT}"
CREATED_ID=
KEEP_SERVER=false

fail() { printf '%s\n' "$*" >&2; exit 1; }

usage() {
    printf '%s\n' \
        "Usage: bash ${BASH_SOURCE[0]} {up|down|logs|status|test}" \
        '  up      Start a fresh server and provision alice/bob; fail if it exists.' \
        '  down    Remove this test container and all its data (idempotent).' \
        '  logs    Show server logs.' \
        '  status  Show container status.' \
        '  test    Start fresh, run integration tests, always remove the server.' \
        'Overrides: MATRIX_PORT, MATRIX_CONTAINER_NAME, MATRIX_SYNAPSE_IMAGE.'
}

exists() {
    local result=0
    podman container exists "$NAME" || result=$?
    # Podman uses 1 for absent, 125 for errors. Do not hide sandbox/runtime errors.
    if (( result > 1 )); then
        fail 'Cannot query Podman. Check its error above (and sandbox access).'
    fi
    return "$result"
}

check_owner() {
    local owner
    owner=$(podman inspect --format '{{ index .Config.Labels "io.rsmatrix.test-server" }}' "$NAME")
    [[ "$owner" == true ]] || fail "Refusing to touch $NAME: not an RSMatrix test container."
}

cleanup() {
    local result=$?
    trap - EXIT
    if [[ -n "$CREATED_ID" && "$KEEP_SERVER" != true ]]; then
        if (( result != 0 )); then
            podman logs --tail 100 "$CREATED_ID" >&2 || true
        fi
        # Remove by the exact ID we created, never an unrelated same-name container.
        if ! podman rm --force --volumes "$CREATED_ID" >/dev/null; then
            printf 'Cleanup failed; run make matrix-down.\n' >&2
            result=1
        fi
    fi
    exit "$result"
}

up() {
    command -v curl >/dev/null || fail 'curl is required.'
    if exists; then
        fail "$NAME already exists. Use make matrix-down first, or choose MATRIX_CONTAINER_NAME."
    fi
    # Detect storage/sandbox errors before doing any work.
    podman info >/dev/null
    local config
    config=$(< "$HERE/homeserver.yaml")
    config=${config//http:\/\/localhost:8008/http:\/\/127.0.0.1:$PORT}
    # A tmpfs plus an explicit non-root user avoids host bind mounts, SELinux
    # relabeling, persistent volumes and rootless UID-mapping/chown problems.
    # Podman rejects uid=/gid= tmpfs options. A sticky, writable mount lets UID
    # 991 create files; the entrypoint's umask keeps those files private. This
    # permission mode applies only inside this container, not to any host path.
    # Configuration is non-secret test data, passed via env rather than a mount.
    CREATED_ID=$(podman create --name "$NAME" \
        --label "$LABEL=true" \
        --publish "127.0.0.1:${PORT}:8008" --http-proxy=false \
        --user 991:991 --cap-drop ALL --security-opt no-new-privileges \
        --tmpfs /data:rw,nosuid,nodev,noexec,size=256m,mode=1777 \
        --env "RSMATRIX_SYNAPSE_CONFIG=$config" \
        --entrypoint /bin/sh \
        "$IMAGE" -ec '
            umask 077
            printf "%s\n" "$RSMATRIX_SYNAPSE_CONFIG" > /data/homeserver.yaml
            python -m synapse.app.homeserver --config-path /data/homeserver.yaml --generate-keys
            exec python /start.py run
        ')
    podman start "$CREATED_ID" >/dev/null

    local ready=false
    for ((attempt = 0; attempt < 60; attempt++)); do
        if curl --noproxy '*' --fail --silent --max-time 2 "$URL/_matrix/client/versions" >/dev/null; then
            ready=true
            break
        fi
        [[ $(podman inspect --format '{{.State.Running}}' "$CREATED_ID") == true ]] \
            || fail 'Synapse exited before becoming ready.'
        sleep 1
    done
    [[ "$ready" == true ]] || fail "Synapse did not become ready at $URL."

    for user in alice bob; do
        podman exec "$CREATED_ID" register_new_matrix_user \
            --config /data/homeserver.yaml \
            --user "$user" --password rsmatrix-test-password --no-admin \
            http://127.0.0.1:8008
    done
    printf '\nSynapse ready: %s\n' "$URL"
    printf 'Users: @alice:localhost and @bob:localhost\nPassword: rsmatrix-test-password\n'
    printf 'Local test credentials only. Stop and erase: make matrix-down\n'
}

[[ $# == 1 ]] || { usage; exit 2; }
case "$1" in
    -h|--help|help) usage; exit 0 ;;
    up|down|logs|status|test) ;;
    *) usage; exit 2 ;;
esac
[[ "$PORT" =~ ^[1-9][0-9]{0,4}$ ]] && (( PORT <= 65535 )) || fail 'MATRIX_PORT must be 1..65535.'
command -v podman >/dev/null || fail 'Podman is required (https://podman.io/docs/installation).'
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

case "$1" in
    up)
        up
        KEEP_SERVER=true
        ;;
    down)
        if exists; then
            check_owner
            podman rm --force --volumes "$NAME"
        else
            printf '%s is already absent.\n' "$NAME"
        fi
        ;;
    logs|status)
        exists || fail "$NAME is not running. Use make matrix-up."
        check_owner
        if [[ "$1" == logs ]]; then
            podman logs --tail 100 "$NAME"
        else
            podman inspect --format '{{.Name}}: {{.State.Status}}' "$NAME"
        fi
        ;;
    test)
        command -v dotnet >/dev/null || fail 'The .NET SDK is required.'
        up
        RSMATRIX_INTEGRATION_URL="$URL" dotnet run --configuration Release \
            --project "$PROJECT_DIR/RSMatrix.IntegrationTests.csproj" \
            -- --report-trx
        ;;
esac
