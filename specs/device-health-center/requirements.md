# Device Health Center: Requirements

## Problem and scope

Teachers currently read selected parts of student Agent state from several policy and update workflows. They need one place to tell whether a selected student computer is reachable, whether the SYSTEM Agent and Veyon service are healthy, whether policy work is pending, and whether an update still needs recovery.

The first release adds an on-demand, signed health snapshot and a TeacherConsole page for the selected classroom targets. It includes only fixed, bounded repair actions. It does not add cloud telemetry, a new device registry, remote shell access, or automatic repair loops.

## User stories

- As a teacher, I want a per-computer health summary so I can tell which subsystem needs attention.
- As a teacher, I want one-click diagnostics that return understandable checks instead of raw logs.
- As a teacher, I want to run a small set of safe repairs and see the signed result for each target.
- As a school administrator, I want offline or unreachable devices to remain distinguishable from devices that are confirmed powered off.

## Acceptance criteria

1. When a teacher refreshes health for selected targets, TeacherConsole shall request and verify each Student Agent's signed status and present a per-target result, including partial failures.
2. When the Agent responds, the health details shall distinguish Agent/task health, Veyon service state, website/application/system policy state, and the update transaction state.
3. When no fresh signed response arrives, TeacherConsole shall show “未收到回执 / 状态未知” and shall not claim that the computer is powered off.
4. When the update journal reports an in-progress or recovery-required transaction, the health result shall identify that condition and shall not offer repairs that conflict with update recovery.
5. When the Agent task identity, protected task ACL, or SYSTEM execution cannot be confirmed, the health result shall require manual review and shall not classify the device as healthy.
6. When the teacher requests a repair, the Agent shall accept only a fixed action enum in a campus-signed, short-lived command bound to a unique command ID and replay nonce; it shall never accept an executable path, arguments, or free-form command.
7. When a repair runs, the Agent shall enforce a per-action cooldown, a bounded timeout, and exclusion against the student update transaction lock; it shall return and locally audit a signed outcome.
8. The initial repair allowlist shall contain only (a) ensure the Veyon service is running, and (b) queue a refresh for the currently active website policy revision. A queued refresh shall not be described as verified browser application.
9. When a repair is unavailable, blocked by an update, rate-limited, or unsuccessful, the teacher shall see that result and the Agent shall stop without retry loops.
10. The page shall use the current selected classroom targets and existing TeacherConsole design system; health data shall remain local to the request and shall not be uploaded to CloudBase.

## Non-goals

- No inference of power state from a missing network response.
- No arbitrary SYSTEM commands, remote logs, file browsing, or automatic repair loop.
- No file-level or binary-delta update activation.
- No claim that code-level acceptance replaces Win10/Win11 field acceptance.
