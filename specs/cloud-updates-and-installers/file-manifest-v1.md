# File update manifest v1 and package ownership

This specification supports read-only inventory and planning while the Windows atomic-update acceptance gate remains open. A valid plan is diagnostic only. It cannot authorize an install or modify a target directory. The manifest and detached signature are defined by [file-manifest-v1.schema.json](file-manifest-v1.schema.json) and [file-manifest-signature-v1.schema.json](file-manifest-signature-v1.schema.json).

## Packaging inventory

The package scripts and Inno Setup source define two fixed roles. Manifest root IDs map to fixed application directories; a manifest never supplies an absolute path or a destination chosen by a release server.

| Role / root ID | Installed location | Contents and owner | Managed by a future file transaction |
|---|---|---|---|
| `TeacherApp` | `{ProgramFiles}/Veyon Campus/Teacher` | The complete `PublishDirectory` tree from `TeacherConsole`, including nested `Worker`; component ownership is recorded per file. | Yes, after the atomic Windows gate passes. |
| `TeacherUpdateHelperVersioned` | `{ProgramFiles}/Veyon Campus/Updater/Teacher/{version}` | The separately published single-file `VeyonCampus.UpdateHelper.exe`. It is versioned so the helper running a transaction is never overwritten. | Stage a new version; keep the active and rollback helper until transaction commit. |
| `StudentApp` | `{ProgramFiles}/Veyon Campus/Student` | The complete `PublishDirectory` tree: StudentSetup, Worker, Agent payload, Companion, Edge policy executor, Edge Native Messaging Host, Edge extension and third-party notices. Per-file component IDs distinguish those owners. | Yes, inside the same future StudentSetup + Agent transaction. |
| `StudentAgentVersioned` | `{ProgramData}/VeyonCampus/WebsitePolicyAgent/{agentVersion}` | The version-isolated SYSTEM Agent payload copied from the signed `StudentAgentPayload` files. | Stage by version and switch the protected task only inside the same transaction. |
| `StudentUpdateHelperVersioned` | `{ProgramFiles}/Veyon Campus/Updater/Student/{version}` | The separately published single-file Student UpdateHelper. | Stage a new version; keep the active and rollback helper until transaction commit. |

`StudentAgentPayload` and `StudentAgentRuntime` are separate component IDs because the installer payload under `StudentApp/WebsitePolicyAgent/` and the SYSTEM process under the versioned ProgramData root have different update lifecycles. Their identical bytes use the same content hash. `StudentCompanion`, `EdgePolicyExecutor`, `EdgeNativeHost`, `EdgeExtension`, and `StudentWorker` are distinct components under `StudentApp`; replacing their files still requires the G0 transaction to coordinate running processes and health checks.

### Excluded paths and side effects

These are not file-manifest roots and must never be inferred as owned from their presence on disk:

- CloudBase/campus configuration, teacher private keys, Agent identity private keys, public-key pins, student/user data, logs, caches and transaction journals.
- Veyon installation/service files and shared .NET / ASP.NET runtimes. The manifest may state required runtime versions, but this file updater cannot install, replace or uninstall them.
- Windows services, scheduled-task definitions/actions/ACLs, registry values, firewall rules, shortcuts, Startup entries, file associations and uninstall records. Changes to these use an explicitly versioned, rollback-capable migration.
- Runtime installer EXEs and the Inno Setup `dontcopy` files in `{tmp}`. They are installer inputs, not application files.
- Unknown files and directories. A scanner finding one inside a managed root marks the source `unsupported-source`; it never silently deletes it.

The current StudentSetup cleanup manifest (`veyon-campus-student-setup.json`) and marker (`.veyon-campus-student-setup`) are bootstrap/uninstall metadata, not a signed file-update baseline. Their existence cannot establish trusted file ownership. Existing installations without a trusted file manifest use the complete signed installer to migrate or require administrator review.

## Manifest and detached signature

The v1 JSON shape is frozen by [file-manifest-v1.schema.json](file-manifest-v1.schema.json); the detached signature wrapper is frozen by [file-manifest-signature-v1.schema.json](file-manifest-signature-v1.schema.json). Cross-field checks that JSON Schema cannot express (role/product agreement, unique root IDs, unique component IDs, root existence, component/root compatibility, case-insensitive path collisions, migration references, and exact direct-source digests) are mandatory in the parser.

- A manifest describes the complete target state for exactly one role, `win-x64`, channel and version. It does not contain arbitrary URLs, commands, absolute paths or installer switches.
- Each file has a Windows-relative canonical path, byte length and lowercase SHA-256. Paths use `/`, NFC Unicode, reject `.`/`..`, roots, drive letters, colons, control characters, trailing dots/spaces, Windows device names, alternate data streams, reparse points and case-insensitive collisions.
- The manifest is at most 16 MiB; it contains at most 50,000 files and each file is at most 2 GiB. Parsers reject duplicate JSON property names and unknown fields before semantic validation.
- The detached `manifest.sig` is a strict JSON object with `schemaVersion: 1`, `algorithm: "RSA-PSS-SHA256"`, a 64-character lowercase SHA-256 SPKI `keyId`, and canonical Base64 `signature`. Verification signs the exact UTF-8 bytes of `manifest.json` prefixed by the domain separator `VeyonCampus.FileManifest.v1\0`. The publisher emits a deterministic UTF-8 JSON file with no BOM and one final LF; clients verify the original downloaded bytes and never reserialize before signature verification.
- `releaseSequence` is monotonically increasing per `(role, channel)` and is compared with protected local state. A lower sequence, lower target version, expired target manifest, wrong role/channel/architecture, unknown key, or expired trust metadata is rejected. Expired manifests may be retained as historical source ownership evidence, but cannot authorize a new update.
- `createdUtc` and `expiresUtc` are UTC `Z` timestamps. A target is usable only while current; the publisher rotates the target metadata before expiry. Release artifact URLs and mirror selection are supplied by the signed release index / fixed allowlisted origins; the file manifest binds objects by SHA-256, not by an attacker-controlled URL.
- `minimumUpdaterVersion` and `requiredFileUpdateProtocolVersion` are separate from the existing `StudentUpdateProtocolVersion=1`, which only describes full-installer transaction status. File-level capability is negotiated independently. Unknown or missing capability selects the complete signed installer.
- Key IDs are SHA-256 hashes of the DER SubjectPublicKeyInfo. The app's pinned trust set authorizes rotation: the current key signs the next key and its activation/retirement window during an overlap release. A manifest cannot introduce its own trust key. If the currently trusted key is compromised, rotation requires an out-of-band trusted app update or administrator action; no trust-on-first-use fallback is allowed.

## Direct source support and safe deletion

`directSources` is an explicit allowlist of source versions and exact source manifest digests. A source version absent from this list, a legacy install without a signed baseline, or any actual source file that does not match its signed manifest is `unsupported-source`; the caller chooses a complete signed installer or administrator repair. The planner never guesses ownership from an untrusted directory scan.

For each supported source, `managedDeletes` lists exact `(rootId, path, previousSha256)` triples. A deletion is planned only when the trusted source manifest digest matches, the actual file hash matches `previousSha256`, the root is the fixed allowlisted root, and the target manifest does not contain the path. An absent/mismatched delete record, modified file, unknown file, or case-insensitive path collision blocks the file plan. Configuration, identity, logs and other external roots can never be deleted by this manifest.

`migrationCatalog` is data only, never a script. `directSources.requiredMigrations` refers to migration IDs in the catalog. The planner outputs `migration-required` and the exact capability/reversibility/reboot metadata; it does not run migrations. A source with a non-reversible migration, unsupported runner capability, or no tested rollback handler is not eligible for file-level update.

## Read-only planning contract

The planner produces per-file `unchanged`, `download`, `add`, `managed-delete`, and `migration-required` actions plus a top-level `unsupported-source` status when any trust, version, ownership, path or compatibility condition fails. Each action includes role, component, root ID, path, source and target hashes, byte size and reason. A plan reports `readOnly: true`, `deployable: false` until G0 acceptance, signatures and object distribution are integrated, and file-transaction implementation passes its own Windows gate.

The planning CLI may read the installation roots and signed manifest cache. It must not open files for writing, launch an installer/helper, create directories, acquire privileges, register tasks, modify the registry, or clean caches. Machine changes are tested only by a later separate implementation after G0; this CLI is never reused as an apply command.

