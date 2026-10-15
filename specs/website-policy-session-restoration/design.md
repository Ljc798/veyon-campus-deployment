# Website Policy Session Restoration Design

## Components

- `TeacherViewModel` exposes a curated domain list and merges it into `WebsiteDomains` without pushing it. `TeacherWindow` places the import control beside the existing editable domain field.
- After a successful website-policy registry transaction, the SYSTEM Agent updates an in-memory restart request ID. Policy expiry does the same only when expiry actually changes the registry.
- The Agent exposes the current request ID on a loopback-only HTTP endpoint and signs the response with its existing identity key.
- Each logged-in Student Companion polls that endpoint. A request seen on startup is handled only if it was published in the previous 15 seconds, covering an Agent startup expiry race without replaying old requests. Later request-ID changes trigger a restart only for Edge/Chrome windows in that user's Windows session.
- The Companion launches `edge://restart` or `chrome://restart` through the executable associated with an already open browser window. It never kills the browser process. The Agent response reports policy acceptance and publication of a restart request, not delivery to a Companion or browser completion.

## Compatibility and failure handling

- A Companion that does not recognize the new endpoint keeps running; an Agent that does not recognize the endpoint returns 404 and the new Companion skips the restart feature.
- Restart requests are in-memory and coalesce to the latest request ID. A Companion that starts within 15 seconds can process a fresh request; later sessions do not replay stale requests.
- If the browser is closed, the Companion does not launch it. The browser reads the registry policy the next time it starts.
- A failed process launch is isolated to that browser and does not change the already committed website policy.

## Curated blacklist

The starter list groups short-video/social sites (`douyin.com`, `kuaishou.com`, `xiaohongshu.com`, `xhslink.com`, `weibo.com`, `weibo.cn`), video sharing (`bilibili.com`, `b23.tv`), live streaming (`douyu.com`, `huya.com`), and browser games (`4399.com`, `7k7k.com`). The existing compiler applies a root domain to its subdomains. The list avoids broad service/CDN domains that could break unrelated sites.

The Ministry of Education's [guidance on preventing student gaming addiction](https://www.moe.gov.cn/srcsite/A06/s3321/202110/t20211029_576140.html) and a [Beijing student-use survey](https://www.beijing.gov.cn/fuwu/bmfw/sy/jrts/202608/t20260813_4821554.html) discuss online games and short videos as student-use categories. Official service pages confirm the domain families represented here, including [Douyin](https://www.douyin.com/help), [Kuaishou](https://www.kuaishou.com/cn), [Bilibili](https://www.bilibili.com/), [Weibo](https://zh.ir.weibo.com/), [Douyu](https://www.douyu.com/), [Huya](https://www.huya.com/), [4399](https://www.4399.com/), and [7k7k](https://www.7k7k.com/); Bilibili also has an [announcement mentioning b23.tv links](https://www.bilibili.com/opus/1057023775486246912). These sources support the categories and domain selection; they do not establish that every school or class must block every listed site. Bilibili and social platforms may also contain educational material, so teachers should review the editable list against the lesson.

## Security and privacy

- Keep the Agent restart endpoint loopback-only.
- Sign its response with the existing SYSTEM Agent identity key.
- Execute browser relaunch in the interactive Student Companion process, not from SYSTEM session 0.
- Do not retrieve or persist page URLs, browsing history, session files, or form data.
- Keep restart outcome distinct from the Agent's registry-write acknowledgement.

## Validation plan

- Check import behavior for empty fields, duplicate/case-variant domains, and existing custom entries.
- Check Agent signal creation after apply, disable, and expiry, and verify no request on a rejected policy.
- On Windows, verify Edge and Chrome restart in the active user session, reopen tabs, preserve separate user profiles, and leave a closed browser closed.
- On Windows, verify the response remains accurate when Companion is absent or browser launch fails.
