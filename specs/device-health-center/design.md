# Device Health Center: Design

## Component boundaries

- VeyonCampus.Core owns the health DTOs, signed repair command/receipt, fixed action enum, Veyon service inspection, and response validation.
- WebsitePolicyRuntime adds optional health fields to the existing signed v4 status response and handles one fixed repair endpoint. Existing policy and update endpoints keep their current semantics.
- The Agent reads its own SYSTEM identity, the managed scheduled-task action and ACL, the Veyon service state, existing policy snapshots, and the update journal. It returns only bounded enum/status fields; no raw task XML, log body, filesystem path, or exception text crosses the network.
- TeacherViewModel maps verified delivery results into health rows and aggregate counts. A failed request is “no response”, not a power-state conclusion.
- TeacherWindow adds a “设备健康” page that reuses the selected classroom targets, existing color tokens, controls, spacing, and typography.

## Status model

The health snapshot is an additive optional field in the signed v4 response, so older Agents that already support v4 remain readable. Missing new health fields are displayed as “此 Agent 版本未提供此项” instead of invalidating the existing policy/update status.

The snapshot includes:

- Agent process identity (runningAsSystem) and managed task checks (taskMatchesAgent, taskAclRestricted).
- Veyon service state: running, stopped, notInstalled, or unknown.
- Collection timestamp, while relying on the signed response's existing timestamp and nonce for freshness.
- Existing website, application, system, Edge, Agent/StudentSetup version, and update transaction facts are mapped directly; they are not duplicated into a second source of truth.

Teacher classification precedence:

1. needsManualReview: pinned identity review, invalid task identity/ACL, or update journal needsReview.
2. attention: Agent responds but Veyon service is stopped/unknown, a reported policy is pending/degraded, or an update is still in progress.
3. healthy: Agent and task checks pass, Veyon service is running, and no reported policy/update issue needs attention.
4. noResponse: no valid signed response; show the last successful collection time if this view already has one, otherwise say it is unknown. Never infer shutdown.

## Repair protocol and safety

StudentAgentHealthRepairCommand contains schema/purpose, campus ID, command ID, nonce, issue/expiry times, fixed action enum, and an optional expected website-policy revision. It is signed with the campus key. The request has the normal replay nonce header; the Agent verifies campus, signature, freshness, and command binding before acting. StudentAgentHealthRepairReceipt is signed with the pinned Agent key and contains only command ID, action, outcome enum, and completion time.

The endpoint accepts only ensureVeyonServiceRunning and refreshActiveWebsitePolicy. It rejects stale policy revisions, empty/invalid IDs, replayed requests, unsupported actions, and requests outside the cooldown. It acquires the existing StudentUpdateAtomicTransaction machine lock with zero wait; if an update/recovery owns the lock, no repair runs. Service work is time-bounded and verifies the final service state. Website-policy refresh only queues the current revision for the existing user-session executor and reports queued, not applied.

The Agent persists a bounded cooldown/recent-command record under its already protected per-campus ProgramData directory and uses existing protected-file helpers. Its audit record contains only command ID, action, result enum, and UTC time. No secrets, endpoint tokens, domains, user names, IP addresses, paths, or free-form errors are stored in that record.

## Teacher interaction

- The page header shows selected count and aggregate healthy / attention / manual-review / no-response counts.
- A refresh button runs a bounded fan-out against selected targets.
- Each row shows the computer label, classification, Agent/task state, Veyon service, policy/update summary, collection time, and concise reason codes.
- Repair controls appear only when the verified snapshot meets that action's preconditions. Service repair requires explicit confirmation because screen-control connectivity can be interrupted. Policy refresh requires an active revision and is labeled as a queued request.
- After a repair receipt, TeacherConsole performs a fresh signed status read; it does not treat an HTTP acceptance alone as proof of repair.

## Verification strategy

- Unit checks for status classification, old v4 response compatibility, repair signature/freshness/replay, action allowlist, cooldown, update-lock exclusion, and receipt binding.
- Windows Agent checks for service states, task action/SYSTEM identity/task ACL reads, and bounded Veyon start behavior.
- Teacher UI checks for target selection, aggregate counts, no-response wording, manual-review precedence, confirmation, and per-target repair receipts.
- Win10/Win11 field testing remains required for service restart, actual Edge executor refresh, permissions, and network timeout behavior.
