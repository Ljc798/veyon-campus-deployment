# Cloud updates and installers requirements

## Scope

This spec continues the CloudBase distribution work in `docs/开发路线与任务清单.md` and follows the signed update and LAN distribution model in `docs/veyon_architecture_summary.md` and the archived detailed protocol in `docs/archive/2026-09-29/veyon_architecture_summary.md` §§7–21. It covers role-specific Inno Setup installers, public update discovery for Teacher and Student releases, Teacher self-update, Teacher-to-Student silent updates over the campus LAN, and anonymous campus-level Teacher heartbeat. It does not mark Windows-only operational tasks complete without Windows evidence.

The existing no-login package API is part of the release gate. Its synthetic end-to-end path passes against a local CloudBase contract double. After the 2026-10-07 schema-v5/release-v2/v3 migration and function deployment to the backed-up shared domestic CloudBase environment, live health and anonymous v1/v2/v3 release lookups returned HTTP 200; all release lookups returned `release: null`, and row counts were unchanged. A later local retry could not resolve the API hostname, but the domestic DNS wrapper subsequently returned HTTP 200 for health. Live v5/v6 synthetic package publish/search/download/withdrawal acceptance remains open. Its runner reads the protected local `CloudBase__ApiKey`, obtains a short-lived administrator token through interactive CloudBase Auth sign-in, and checks withdrawal permission before writing. No Developer Release public key was supplied to the current local builds, so their updater remains fail-closed until the approved key pair and first signed release are configured. Current environment evidence belongs in [P13-05](../../docs/开发路线与任务清单.md) and the dated [CloudBase operations record](../../docs/records/2026-10/续作核查记录-20261008.md).

## User stories

- As a teacher, I can install the Teacher Console from one Windows setup executable and keep the app updated without signing into CloudBase.
- As a teacher, I can retrieve the current Student installer once, then send a signed update command to selected student Agents on the local network.
- As a student computer, I can verify the campus command and developer release, install an approved newer version silently, and report the result to the Teacher.
- As a campus operator, I can see one anonymous daily heartbeat for the campus with the Teacher version, target Student version, and configured computer count, without a teacher account or per-student identity collection.
- As a student, I can continue to use the existing no-login package search and teacher-phone-suffix download flow independently of software updates.

## Acceptance criteria

1. When a Windows x64 StudentSetup or TeacherConsole release is packaged, the build shall produce a role-specific Inno Setup executable with a fixed application identity, Program Files destination, standard uninstall entry, and role-specific shortcuts.
2. When a client requests the latest release metadata, the API shall return only the published release for the requested role and architecture, without requiring CloudBase Auth.
3. When the Teacher compares the returned semantic version to its local version, it shall offer an update only for a strictly newer release and shall reject an invalid signature, wrong role, wrong architecture, invalid URL, size mismatch, or SHA-256 mismatch.
4. When the Teacher updates itself, it shall stage and validate the setup executable, launch Inno Setup silently through a fixed updater command, exit before replacement, and restart only after the installer succeeds.
5. When the Teacher deploys a Student update, it shall fetch and validate the Student release once, host the file only on the local network, sign a time-limited command with the campus key, and send commands only to the Teacher-selected devices.
6. When a Student Agent receives an update command, it shall validate campus signature, developer signature, release version, expiry, replay protection, role, architecture, download origin, size and digest before starting a fixed silent installer flow; it shall never execute an arbitrary command or path from the message.
7. When a client checks or stages an update, it shall validate the signed release's declared application-policy and system-policy capabilities. If protected local state has an active or pending policy that the candidate does not support, Teacher/StudentSetup UI, the Teacher rollout path, the offline installer, and the Student Agent shall refuse replacement before installation.
8. When a Teacher sends its campus heartbeat, the API shall validate the published package mapping, accept anonymous requests without login, store UTC+8 daily deduplicated campus aggregates, and avoid storing raw teacher names, phone digits, computer names, IP addresses, or per-student installation IDs.
9. When a package or update request is interrupted or invalid, the client shall retain the previous working installation, record a failure/needs-review result, and avoid reporting success before installed-version readback.
10. While these flows are implemented, Windows 10/11 installer, permissions, service/task migration, failure recovery, and one-teacher/one-student acceptance shall remain open until tested on Windows.
11. When the Teacher receives a Student Agent status or update result, it shall verify an RSA-PSS signature bound to the fresh status nonce or update command ID, campus, and expected version. It shall report success only when the response also matches a previously pinned per-target Agent identity; first use or key change shall require explicit Teacher approval, and an unsigned legacy response shall never be reported as success.

## Constraints and non-goals

- Teacher package publishing and Student package download remain no-login; Teacher does not need a CloudBase user account.
- The configuration package API preserves versioned schemas v1–v6 and a 64 KiB archive limit. Schema v6 carries advisory first-deployment choices; it does not change the signed release manifest. Installer binaries must use a separate update distribution channel and must not be passed through that endpoint.
- Release metadata may be fetched anonymously, but release publication is a developer/admin operation and must not be exposed as an anonymous write API.
- CloudBase API secrets, developer release private keys, and campus private keys must never be placed in app packages or public responses.
- Student update files travel over the campus LAN after the Teacher downloads the release; CloudBase is not used as a per-student installer relay.
- Updates must not silently downgrade, change role, or target unsupported architecture.
- Each Student Agent shall keep a per-install signing key in its protected SYSTEM configuration directory; binary updates shall preserve the identity key, ordinary Users shall not be able to read it, and uninstall/reinstall or key rotation shall require a new Teacher trust decision.
- Teacher trust records shall be local to the Teacher user and bound to both campus and selected host/IP. First-contact trust is teacher-mediated TOFU; the UI shall show the complete fingerprint and explain that first-contact trust over the LAN cannot detect an active key substitution.
