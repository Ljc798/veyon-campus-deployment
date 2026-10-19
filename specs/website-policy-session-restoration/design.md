# Website Policy Session Restoration Design

## Components

- `TeacherViewModel` exposes a curated domain list and merges it into `WebsiteDomains` without pushing it. `TeacherWindow` places the import control beside the existing editable domain field.
- After an effective LegacyRegistry Edge policy change, the SYSTEM Agent atomically persists the latest coalesced refresh request beside its protected configuration. Policy expiry and restoration also queue a request; ExtensionRealtime does not use the platform refresh queue.
- The Agent exposes the signed current request on a loopback-only HTTP endpoint. The persisted schema remains the existing version-1 signal, so older readers can still parse it.
- A separate no-UI `asInvoker` Edge Policy Executor starts for each logged-in user from All Users Startup. It is independent of Student Companion, checks the signed request, current user SID, SessionId, integrity level and a visible Edge window, then sends only the fixed `--refresh-platform-policy` argument from the existing Stable installation path.
- Edge Stable `142.0.0.0` and later is eligible for the platform refresh switch; the executor still requires the standard machine-wide Stable path, the same interactive user/session, and matching integrity. It starts `msedge.exe` directly with the fixed switch from the logged-in user's token. Edge is never navigated to `edge://restart`, focused, killed, or restarted by the strategy flow. Chrome retains its separate legacy restart path.
- `DispatchedUnverified` means Windows created the refresh request process; it is not evidence that Edge applied the policy. The Agent acknowledgement describes registry commit and a persisted refresh task, not browser completion.

## Compatibility and failure handling

- An older Agent may return 404 for the new endpoint; the executor treats that as no queued request and keeps polling. Companion no longer depends on Edge refresh polling.
- Requests coalesce to the latest Revision and survive Agent restart. A policy refresh task does not change the policy authority or write HKCU.
- If Edge is closed, the executor records `BrowserNotRunning` in its bounded per-user diagnostic log and leaves the Agent request pending. It does not start a browser; the next normal Edge start reads the machine policy.
- Versions older than `142.0.0.0` report `UnsupportedBrowserVersion`; later Stable patch updates do not need a package rebuild.
- A failed launch is retried at most three times with a delay, isolated from policy writes and expiry cleanup.

## Curated blacklist

The starter list groups short-video/social sites (`douyin.com`, `kuaishou.com`, `xiaohongshu.com`, `xhslink.com`, `weibo.com`, `weibo.cn`), video sharing (`bilibili.com`, `b23.tv`), live streaming (`douyu.com`, `huya.com`), and browser games (`4399.com`, `7k7k.com`). The existing compiler applies a root domain to its subdomains. The list avoids broad service/CDN domains that could break unrelated sites.

The Ministry of Education's [guidance on preventing student gaming addiction](https://www.moe.gov.cn/srcsite/A06/s3321/202110/t20211029_576140.html) and a [Beijing student-use survey](https://www.beijing.gov.cn/fuwu/bmfw/sy/jrts/202608/t20260813_4821554.html) discuss online games and short videos as student-use categories. Official service pages confirm the domain families represented here, including [Douyin](https://www.douyin.com/help), [Kuaishou](https://www.kuaishou.com/cn), [Bilibili](https://www.bilibili.com/), [Weibo](https://zh.ir.weibo.com/), [Douyu](https://www.douyu.com/), [Huya](https://www.huya.com/), [4399](https://www.4399.com/), and [7k7k](https://www.7k7k.com/); Bilibili also has an [announcement mentioning b23.tv links](https://www.bilibili.com/opus/1057023775486246912). These sources support the categories and domain selection; they do not establish that every school or class must block every listed site. Bilibili and social platforms may also contain educational material, so teachers should review the editable list against the lesson.

## Security and privacy

- Keep the Agent request endpoint loopback-only and sign its response with the existing SYSTEM Agent identity key.
- Keep the refresh executor `asInvoker`; do not make the Agent or executor accept an arbitrary command, executable path, parameter, browser profile, or LAN-triggered user action.
- Execute the fixed refresh request only in the same interactive user, session and integrity level as a visible, trusted-path Edge process; otherwise defer for review. The Agent remains SYSTEM for HKLM policy writes.
- Do not retrieve or persist page URLs, browsing history, session files, or form data.
- Keep restart outcome distinct from the Agent's registry-write acknowledgement.

## Validation plan

- Check import behavior for empty fields, duplicate/case-variant domains, and existing custom entries.
- Check persistent Agent request coalescing, Agent restart recovery, expiry, unsupported Edge versions, retry limits, and same-user/session/integrity isolation.
- On Windows, verify Edge 155 updates without a new window or restart; verify Edge 118 and mismatched user/session/integrity processes are skipped without a new window.
- Verify refresh continues with Companion exited, defers while Edge is closed, and handles installation from an elevated setup without an elevated user executor.
- Check that the Chrome compatibility path and Companion classroom status, messages, and help remain available.
- Record browser policy readback and navigation separately; process creation alone does not verify browser application.
