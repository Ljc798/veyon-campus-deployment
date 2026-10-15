# Website Policy Session Restoration Requirements

## Goal

After a website policy is applied or removed, let an active student session restart Edge and Chrome gracefully and reopen the pages that were open before the restart. Also let teachers populate a starter blacklist with common entertainment and game sites, then edit it before sending.

## User stories

- As a teacher, I want the student's open Edge and Chrome pages to return after the browser restart needed to refresh a website policy.
- As a teacher, I want a curated list of common entertainment sites inserted into the blacklist field, while keeping my own entries editable.
- As a student, I want an open browser to restart without the management agent forcibly terminating it.

## Acceptance criteria

1. When a validated website policy is applied, the Student Agent shall queue a browser-restart request after the registry transaction commits.
2. When a website policy expires or is disabled, the Student Agent shall publish a browser-restart request after the registry transaction commits.
3. When a Student Companion is running in a logged-in user session and Edge or Chrome has an open window, the Companion shall restart that browser through its internal restart URL and let the browser restore its prior tabs. The Companion shall not force-terminate browser processes or launch a browser that was not already open.
4. The Agent acknowledgement shall state that it published the restart request, not that a Companion received it or that the browser restarted. A Companion starting shortly after publication may process the fresh request; later Companion sessions do not replay stale requests. If no browser window is open, the Agent does not launch one, and the next normal browser start shall read the current policy.
5. The teacher website-policy page shall state that active Edge and Chrome sessions receive a graceful restart request and that Firefox still needs a manual restart.
6. When the teacher imports the common entertainment blacklist, the app shall merge it with existing valid domains, remove duplicates case-insensitively, select blacklist mode, and leave the result editable. Importing shall not send a policy to student computers.
7. The starter list shall include: `douyin.com`, `kuaishou.com`, `bilibili.com`, `b23.tv`, `xiaohongshu.com`, `xhslink.com`, `weibo.com`, `weibo.cn`, `douyu.com`, `huya.com`, `4399.com`, and `7k7k.com`.
8. The starter list and browser restart flow shall not collect browsing history, current page URLs, form contents, or other browser data.

## Scope limits

- Page restoration covers browser tabs/windows handled by the browser's graceful restart flow. Unsaved form state is not guaranteed.
- The preset is a starting point for school policy, not a claim that every listed site must be blocked in every class. Teachers can remove domains before applying it.
- Firefox remains governed by the existing website policy but is not automatically restarted in this change.
