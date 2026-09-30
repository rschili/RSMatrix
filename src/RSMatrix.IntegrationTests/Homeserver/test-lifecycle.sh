#!/usr/bin/env bash
# Offline regression tests: execute the real lifecycle script using PATH shims.
set -euo pipefail
HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
WORK=$(mktemp -d)
trap 'rm -rf -- "$WORK"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

python3 - "$HERE" "$WORK" <<'PY'
import json
import os
from pathlib import Path
import subprocess
import sys

homeserver, work = map(Path, sys.argv[1:])
bin_dir = work / "bin"
bin_dir.mkdir()
log = work / "commands.jsonl"
container_id = "created-container-id"
name = "offline-lifecycle-test"
port = "18008"
image = "example.invalid/synapse:offline-test"

# Every external service command is intercepted. Unknown commands fail closed;
# the shim never delegates to a real Podman, curl, or dotnet executable.
shim = bin_dir / "shim"
shim.write_text("#!" + sys.executable + "\n" + r'''
import json
import os
from pathlib import Path
import sys

tool = Path(sys.argv[0]).name
args = sys.argv[1:]
with open(os.environ["MOCK_LOG"], "a") as stream:
    stream.write(json.dumps({"tool": tool, "args": args,
                             "url": os.environ.get("RSMATRIX_INTEGRATION_URL")}) + "\n")
if tool == "curl":
    sys.exit(int(os.environ.get("MOCK_CURL_STATUS", "0")))
if tool == "dotnet":
    sys.exit(int(os.environ.get("MOCK_DOTNET_STATUS", "0")))
if tool == "podman":
    if args[:2] == ["container", "exists"]:
        sys.exit(int(os.environ.get("MOCK_EXISTS_STATUS", "1")))
    if args[:1] == ["inspect"]:
        key = "MOCK_RUNNING" if "{{.State.Running}}" in args else "MOCK_OWNER"
        print(os.environ.get(key, "true"))
        sys.exit(0)
    if args[:1] == ["info"]:
        sys.exit(0)
    if args[:1] == ["create"]:
        status = int(os.environ.get("MOCK_CREATE_STATUS", "0"))
        if status:
            sys.exit(status)
        print("created-container-id")
        sys.exit(0)
    if args[:1] == ["start"]:
        sys.exit(int(os.environ.get("MOCK_START_STATUS", "0")))
    if args[:1] == ["exec"]:
        if args[args.index("--user") + 1] == os.environ.get("MOCK_FAIL_USER"):
            sys.exit(23)
        sys.exit(0)
    if args[:1] in (["logs"], ["rm"]):
        sys.exit(0)
print("Unexpected mock command: " + repr([tool] + args), file=sys.stderr)
sys.exit(99)
''')
shim.chmod(0o755)
for tool in ("podman", "curl", "dotnet"):
    (bin_dir / tool).symlink_to(shim)

base_env = {key: value for key, value in os.environ.items()
            if not key.startswith(("MATRIX_", "MOCK_", "RSMATRIX_INTEGRATION_"))}
base_env.update(PATH=str(bin_dir) + os.pathsep + os.environ["PATH"],
                MOCK_LOG=str(log), MATRIX_CONTAINER_NAME=name,
                MATRIX_PORT=port, MATRIX_SYNAPSE_IMAGE=image)


def run(action, expected=0, **overrides):
    log.write_text("")
    result = subprocess.run(
        ["bash", str(homeserver / "matrix-test-server.sh"), action],
        env={**base_env, **overrides}, cwd=work, text=True, capture_output=True, timeout=10,
    )
    assert result.returncode == expected, (
        f"{action}: expected exit {expected}, got {result.returncode}\n"
        f"stdout: {result.stdout}\nstderr: {result.stderr}"
    )
    calls = [json.loads(line) for line in log.read_text().splitlines()]
    return calls, result


def commands(calls, tool="podman"):
    return [call["args"] for call in calls if call["tool"] == tool]


def selected(calls, command):
    return [args for args in commands(calls) if args[0] == command]


def assert_cleanup(calls, failed):
    assert selected(calls, "rm") == [["rm", "--force", "--volumes", container_id]]
    assert commands(calls)[-1] == ["rm", "--force", "--volumes", container_id]
    expected_logs = [["logs", "--tail", "100", container_id]] if failed else []
    assert selected(calls, "logs") == expected_logs


def assert_up(calls):
    podman = commands(calls)
    assert podman[:2] == [["container", "exists", name], ["info"]]
    create, = selected(calls, "create")
    for option, value in (
        ("--name", name),
        ("--label", "io.rsmatrix.test-server=true"),
        ("--publish", f"127.0.0.1:{port}:8008"),
        ("--user", "991:991"),
        ("--cap-drop", "ALL"),
        ("--security-opt", "no-new-privileges"),
        ("--tmpfs", "/data:rw,nosuid,nodev,noexec,size=256m,mode=1777"),
        ("--entrypoint", "/bin/sh"),
    ):
        assert create.count(option) == 1, create
        assert create[create.index(option) + 1] == value, create
    assert "--http-proxy=false" in create
    # uid=/gid= are Linux tmpfs options but are rejected by Podman's parser.
    tmpfs_options = create[create.index("--tmpfs") + 1].split(":", 1)[1].split(",")
    assert not any(option.startswith(("uid=", "gid=")) for option in tmpfs_options)
    assert "mode=1777" in tmpfs_options  # Writable by the non-root container user.
    assert not any(arg in ("-v", "--volume", "--mount", "-P", "-p", "--publish-all")
                   or arg.startswith(("-v", "--volume=", "--mount=", "--publish="))
                   for arg in create), create
    config = (homeserver / "homeserver.yaml").read_text().rstrip("\n")
    config = config.replace("http://localhost:8008", f"http://127.0.0.1:{port}")
    assert create[create.index("--env") + 1] == "RSMATRIX_SYNAPSE_CONFIG=" + config
    assert f"public_baseurl: http://127.0.0.1:{port}/" in config
    assert create[create.index("--entrypoint") + 2:][:2] == [image, "-ec"]
    entrypoint_script = create[-1]
    assert entrypoint_script.strip().splitlines()[0].strip() == "umask 077"
    assert selected(calls, "start") == [["start", container_id]]
    assert commands(calls, "curl") == [[
        "--noproxy", "*", "--fail", "--silent", "--max-time", "2",
        f"http://127.0.0.1:{port}/_matrix/client/versions",
    ]]
    assert selected(calls, "exec") == [
        ["exec", container_id, "register_new_matrix_user",
         "--config", "/data/homeserver.yaml", "--user", user,
         "--password", "rsmatrix-test-password", "--no-admin",
         "http://127.0.0.1:8008"]
        for user in ("alice", "bob")
    ]


def assert_dotnet(calls):
    assert commands(calls, "dotnet") == [[
        "run", "--configuration", "Release", "--project",
        str(homeserver.parent / "RSMatrix.IntegrationTests.csproj"),
        "--", "--report-trx",
    ]]
    dotnet_index = next(i for i, call in enumerate(calls) if call["tool"] == "dotnet")
    assert calls[dotnet_index]["url"] == f"http://127.0.0.1:{port}"
    assert calls[dotnet_index - 1]["args"][0] == "exec"
    assert calls[dotnet_index + 1]["args"][0] in ("logs", "rm")


calls, _ = run("up")
assert_up(calls)
assert not selected(calls, "rm")
assert not commands(calls, "dotnet")
print("PASS: up provisions two non-admins with loopback publishing and tmpfs only")

calls, result = run("up", expected=1, MOCK_EXISTS_STATUS="0")
assert commands(calls) == [["container", "exists", name]]
assert len(calls) == 1
assert "already exists" in result.stderr
print("PASS: up refuses an existing name without modifying it")

calls, _ = run("down")
assert commands(calls) == [["container", "exists", name]]
assert len(calls) == 1
print("PASS: down succeeds when absent")

calls, result = run("down", expected=1, MOCK_EXISTS_STATUS="0", MOCK_OWNER="false")
assert commands(calls) == [
    ["container", "exists", name],
    ["inspect", "--format", '{{ index .Config.Labels "io.rsmatrix.test-server" }}', name],
]
assert len(calls) == 2
assert "Refusing to touch" in result.stderr
print("PASS: down refuses a foreign container")

calls, _ = run("down", MOCK_EXISTS_STATUS="0")
assert selected(calls, "rm") == [["rm", "--force", "--volumes", name]]
print("PASS: down removes an owned container")

for action in ("up", "down"):
    calls, result = run(action, expected=1, MOCK_EXISTS_STATUS="125")
    assert commands(calls) == [["container", "exists", name]]
    assert len(calls) == 1
    assert "Cannot query Podman" in result.stderr
print("PASS: Podman exists error 125 is not absence for up or down")

calls, _ = run("up", expected=125, MOCK_CREATE_STATUS="125")
assert len(selected(calls, "create")) == 1
assert not selected(calls, "start")
assert not selected(calls, "rm")
assert not selected(calls, "logs")
print("PASS: failed creation preserves status and does not remove an uncreated container")

calls, _ = run("up", expected=17, MOCK_START_STATUS="17")
assert len(selected(calls, "create")) == 1
assert selected(calls, "start") == [["start", container_id]]
assert not commands(calls, "curl")
assert not selected(calls, "exec")
assert_cleanup(calls, failed=True)
print("PASS: failed start removes the created container by ID")

calls, result = run("up", expected=1, MOCK_CURL_STATUS="7", MOCK_RUNNING="false")
assert "Synapse exited before becoming ready" in result.stderr
assert not selected(calls, "exec")
assert_cleanup(calls, failed=True)
print("PASS: readiness detects an exited server and cleans up")

calls, _ = run("up", expected=23, MOCK_FAIL_USER="bob")
assert_up(calls)
assert_cleanup(calls, failed=True)
print("PASS: failed provisioning removes the created container by ID")

for status in (37, 0):
    calls, _ = run("test", expected=status, MOCK_DOTNET_STATUS=str(status))
    assert_up(calls)
    assert_dotnet(calls)
    assert_cleanup(calls, failed=status != 0)
    print(f"PASS: one-shot test preserves dotnet exit {status} and cleans up")

for invalid_port in ("0", "65536", "abc", "-1", "08008"):
    calls, result = run("up", expected=1, MATRIX_PORT=invalid_port)
    assert calls == [], calls
    assert "MATRIX_PORT must be 1..65535" in result.stderr
print("PASS: invalid ports are rejected before any service command")
print("All offline lifecycle tests passed.")
PY
