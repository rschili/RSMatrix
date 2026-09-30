# Local homeserver notes

See [Testing](../../../README.md#testing) for the Make commands.
Requires Linux/rootless Podman, Bash, curl, and the .NET SDK.

- **Local testing only:** credentials are deliberately public. Do not expose the
  server beyond loopback or use it for real messages.
- All server state lives on tmpfs and is lost when stopped. Recreate with
  `make matrix-down` / `make matrix-up`, not `podman restart`, to reprovision users.
- Override `MATRIX_PORT`, `MATRIX_CONTAINER_NAME`, or `MATRIX_SYNAPSE_IMAGE` as
  needed; keep the same environment for startup, tests, and cleanup.
- `make test-matrix-scripts` runs the offline lifecycle checks (requires Python 3,
  no Podman or server).
