# Student InputGuard

The StudentSetup package includes this as an idle helper started by Student Companion inside the logged-in user's session. It does not lock input at startup and it never replays a previous lock after a restart.

In operational mode (`--guard-server`), the helper accepts bounded commands only through a per-session named pipe. The pipe ACL allows SYSTEM and the current user; the Guard checks the connecting process path and Session 0 before accepting Agent requests. A dedicated STA thread owns `BlockInput(TRUE/FALSE)`. Leases expire within 20 seconds, and the same thread releases input on renewal loss. If the helper exits while input is blocked, Windows is expected to recover input when the blocking thread exits; this still requires supervised Windows field testing.

The Teacher UI has no remote InputGuard controls in this build. Agent status is supported only when the protected helper is installed; without a matching running session or verified pipe peer, status remains unknown and no action is claimed.

## Isolated local feasibility mode

For a standalone manual feasibility run on a disposable Windows test PC, start `VeyonCampus.StudentInputGuard.exe --run-prototype-once` from a normal interactive PowerShell window. Read the warning, save work, start a video and headphone audio test if desired, then type the exact confirmation phrase shown by the program. The lease is hard-coded to at most 20 seconds. Windows `Ctrl+Alt+Delete` remains available as a recovery path.

`BlockInput(TRUE)` and the corresponding `BlockInput(FALSE)` call run on the same dedicated STA thread. The prototype refuses Session 0 and non-interactive execution. It does not claim that video, audio, UAC, session switching, or all recovery behavior have passed; those require supervised real-machine checks.

Neither mode has passed field acceptance for UAC, desktop switching, remote desktop, video/audio continuity, lease renewal, or installer update and uninstall. Keep teacher controls disabled until supervised testing is complete.
