# Cloud updates and installers design

## Boundaries

1. **CloudBase package distribution** stores small versioned campus configuration ZIPs (the current code and shared environment support schema v3–v6) and enforces the 64 KiB limit. The v6 migration and API/OPA deployment were completed after a pre-deployment backup; v5/v6 write/read/withdrawal E2E remains pending local administrator authentication.
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

`39174` remains the small-message control endpoint. The local file endpoint binds only to active private IPv4 interfaces, serves one opaque route, and is opened only for a rollout. The Student Agent does not accept a shell command, executable path, URL origin, or setup switches from the Teacher message. Source stages the new Agent in versioned ProgramData, switches the protected task, exits the old process and starts the new task through the bundled helper; on start or health failure, the helper restores the old task and verifies old-version health. The SYSTEM helper still uses its loopback `/health` check for local rollback. Teacher network readback instead sends a fresh campus-signed status request and verifies the Student Agent's RSA-PSS/SHA-256 response, request nonce, campus, and pinned per-host identity before reporting success.

Each Agent creates a persistent RSA identity on first SYSTEM startup and stores its private PKCS#8 key in `agent-identity.json` beside the protected campus configuration. Its DACL grants access only to SYSTEM and local Administrators; `SecureTree` preserves this private ACL across configuration repair. Updating the Agent binary leaves this file in place. The Student setup verifier displays the full public-key fingerprint. Teacher stores a canonical public key and fingerprint in the current user's LocalAppData, keyed by `(campusId, normalized target)`. A signed response carries the canonical public key, canonical payload, and signature; status payloads bind the teacher's nonce, while update payloads bind the signed `commandId`, campus, expected StudentSetup version, Agent version, and restart state. A changed key never replaces a pin automatically.

An update to an older Agent is still sent as a campus-signed command so that existing deployments can migrate. Plaintext legacy results are ignored. Teacher waits for the new Agent's signed status response; without a matching pinned identity the per-device result remains `needs-review` and includes the full candidate fingerprint. A Teacher-mediated first-use or rotation dialog lists each target and full fingerprint and requires explicit approval. Operators should compare against the fingerprint shown by Student setup where available. Trust-on-first-use over the same LAN cannot detect an active first-contact key substitution; subsequent substitutions are detected because the pinned key must match. Core checks cover response signature/canonicalization, nonce/command binding, key pin persistence, rejection of implicit key rotation, and unpinned legacy bootstrap handling. Windows SYSTEM identity, task/ACL operations, process restart, firewall, and multi-device acceptance remain open.

## Teacher heartbeat

The Teacher sends one record per campus per UTC+8 calendar day containing only a stable random local publisher instance identifier, `packageId`, Teacher app version, Student release version, and a bounded configured-computer count. The HTTP API verifies the campus identity through the package mapping and upserts by `(campus_id, day_hkt)`. The random identifier is HMACed server-side and is not retained in raw form. No user name, phone suffix, hostname, IP, or student-level identifier is accepted.

## Implementation order

1. Preserve and record the verified anonymous package API end-to-end evidence.
2. Add role-specific Inno Setup definitions and produce setup `.exe` artifacts on a Windows build runner while keeping a directory artifact for diagnosis.
3. Add release metadata/storage schema and anonymous latest-release read API; keep release writes private.
4. Add signed manifest verification, Teacher version comparison, download, and self-update handoff. Release manifest schema v3 declares Application Policy and Student System Policy capability versions; new clients query `/v3/releases/latest`, while `/v1` and `/v2` keep their earlier canonical signatures for installed clients.
5. Add Teacher LAN cache/server and signed update command; add Student Agent verification, SYSTEM Updater handoff, per-device health readback, and Agent binary rollover. The Agent now signs status and update results with a persistent per-machine identity key; Teacher pins the key by campus and target and fails closed on changed keys. Windows SYSTEM acceptance remains required before closing the task.
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
