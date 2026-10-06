# Cloud updates and installers design

## Boundaries

1. **CloudBase package distribution** continues to store only small schema v3 campus configuration ZIPs and enforces the current 64 KiB limit.
2. **Release discovery** is a separate public read API. Release rows contain role, semantic version, architecture, signed manifest, artifact object key, size, SHA-256, and publication time. Writes are limited to a developer/admin path with service credentials.
3. **Release artifacts** use a dedicated private object bucket or an explicitly configured release host. The update API returns short-lived download access; large installer bytes do not pass through the HTTP function.
4. **Teacher self-update** compares semantic versions, verifies the pinned developer RSA signature and the downloaded artifact digest, then delegates replacement to a fixed Updater/Inno Setup command and restarts after success.
5. **Student rollout** follows the documented two-signature model: Developer signature authenticates the installer; the campus Teacher key authenticates the selected rollout command. Teacher downloads each release once and serves it through a LAN-only file endpoint. Student Agent reuses TCP 39174 for small command and result messages; a distinct bounded LAN file endpoint carries the installer.
6. **Teacher heartbeat** is campus-level, daily, and keyed by the published package identity. The server derives campus ownership from `packageId`, uses Asia/Hong_Kong calendar dates, and performs an idempotent daily upsert rather than incrementing a global counter.

## Release manifest

The signed canonical payload binds:

```json
{
  "schemaVersion": 1,
  "product": "VeyonCampus.StudentSetup",
  "role": "StudentSetup",
  "version": "0.4.41",
  "architecture": "win-x64",
  "fileName": "VeyonCampus-Student-Setup-0.4.41-win-x64.exe",
  "sizeBytes": 123456789,
  "sha256": "UPPERCASE_HEX",
  "downloadUrl": "https://..."
}
```

The API adds `signature` and publication time outside the signed canonical fields. The app pins the public key; the private key is held only by the release process. The client accepts only HTTPS download URLs from the configured release origin, expected role/product, `win-x64`, positive bounded size, well-formed SemVer, and a valid signature. Stable latest-version lookup does not perform a downgrade.

## Teacher and Student update flow

```mermaid
sequenceDiagram
    participant T as Teacher Console
    participant C as CloudBase update API
    participant L as Teacher LAN file endpoint
    participant A as Student Agent
    participant U as Student Updater / Inno Setup
    T->>C: GET published Student release metadata
    C-->>T: signed manifest + short-lived download URL
    T->>C: HTTPS download once; verify release and digest
    T->>L: cache validated setup executable
    T->>A: campus-signed update-command (version, digest, expiry, nonce)
    A->>T: GET bounded artifact from Teacher LAN
    A->>A: verify both signatures, digest, role, version, expiry and replay state
    A->>U: fixed silent setup command
    U-->>A: installed version / failure result
    A-->>T: authenticated update result
```

`39174` remains the small-message control endpoint. The local file endpoint binds only to active private IPv4 interfaces, serves one opaque route, and is opened only for a rollout. The Student Agent does not accept a shell command, executable path, URL origin, or setup switches from the Teacher message. Source stages the new Agent in versioned ProgramData, switches the protected task, exits the old process and starts the new task through the bundled helper; on start or health failure, the helper restores the old task and verifies old-version health. The SYSTEM helper checks the expected version and config fingerprint locally, while Teacher polls `/health` for the expected Agent version only. Core and UpdateHelper compile in a .NET 10 Linux/amd64 container; portable rollback tests cover the Student update orchestration's success and rollback sequencing, but not Windows SYSTEM identity, task/ACL operations, or process restart. Windows failure injection remains open, and the HTTP result is not cryptographically authenticated.

## Teacher heartbeat

The Teacher sends one record per campus per UTC+8 calendar day containing only a stable random local publisher instance identifier, `packageId`, Teacher app version, Student release version, and a bounded configured-computer count. The HTTP API verifies the campus identity through the package mapping and upserts by `(campus_id, day_hkt)`. The random identifier is HMACed server-side and is not retained in raw form. No user name, phone suffix, hostname, IP, or student-level identifier is accepted.

## Implementation order

1. Preserve and record the verified anonymous package API end-to-end evidence.
2. Add role-specific Inno Setup definitions and produce setup `.exe` artifacts on a Windows build runner while keeping a directory artifact for diagnosis.
3. Add release metadata/storage schema and anonymous latest-release read API; keep release writes private.
4. Add signed manifest verification, Teacher version comparison, download, and self-update handoff. Release manifest schema v3 declares Application Policy and Student System Policy capability versions; new clients query `/v3/releases/latest`, while `/v1` and `/v2` keep their earlier canonical signatures for installed clients.
5. Add Teacher LAN cache/server and signed update command; add Student Agent verification, SYSTEM Updater handoff, per-device health readback, and Agent binary rollover. Add authenticated result signing and complete Windows acceptance before closing the task.
6. Add campus-level Teacher heartbeat and UTC+8 idempotent aggregation.
7. Run code-level checks here, then require Windows installer and two-machine acceptance before marking the corresponding roadmap tasks complete.

## Security and recovery checks

- RSA-PSS/SHA-256 release signatures are verified against a pinned public key; TLS and SHA-256 are also checked.
- Teacher and StudentSetup updates require both policy capabilities. Before replacing a StudentSetup or SYSTEM Agent, the updater checks protected local application/system-policy state and refuses a release missing any capability required by active or pending state.
- Package IDs, file names, URLs, version strings, sizes, command lifetimes, and nonces are validated before use.
- Update metadata and downloads are no-store; short-lived URLs are not written into logs.
- Staging and updater files live under a protected ProgramData directory; writes use a temporary file and atomic rename.
- Inno Setup receives only constant silent switches and a validated setup path. The updater moves the prior role directory to a protected recovery location before setup; installer failure or role/version readback mismatch quarantines the new files and restores the verified prior directory. Student Agent startup/health failure restores the prior SYSTEM task, ACL, and executable path. Windows failure-injection and real ACL verification remain required.
- Cloud package publishing credentials and developer signing secrets remain server-side; no CloudBase login is needed for package consumers or campus heartbeat.
