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
- Encryption tests register new users through the registration shared secret, so cross-signing identities from earlier runs don't get in the way. `LocalHomeserver` keeps the recovery key a user's first encrypted client created and gives it to the user's later devices.
- Set `RSMATRIX_TEST_LOG` to a log level like `Warning` or `Debug` to see the clients' logs.
