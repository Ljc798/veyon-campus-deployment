const $ = id => document.getElementById(id);
const pairView = $("pair-view");
const controlView = $("control-view");
const profileSelect = $("profile-select");
const roomList = $("room-list");
const statusList = $("status-list");
const operationResult = $("operation-result");
const systemPolicyLabels = [
  ["lockWallpaper", "锁定 Windows 默认桌面壁纸"],
  ["prohibitTimeChanges", "禁止修改日期、时间和时区"],
  ["prohibitNetworkChanges", "限制网络连接设置"],
  ["prohibitSoftwareInstallation", "限制软件安装及学生可写位置中的程序"],
  ["prohibitAccountManagement", "禁止账户管理及本人修改登录密码"],
  ["prohibitControlPanel", "限制 Control Panel 和 Settings"]
];
let scannedPairingCode = null;
if (window.location.hash) {
  const match = /^#pair=([0-9]{8})$/.exec(window.location.hash);
  history.replaceState(null, "", window.location.pathname + window.location.search);
  if (match) scannedPairingCode = match[1];
}
let accessToken = null;
let rooms = [];
let profiles = [];
let pendingReview = null;
let lastOperation = null;
let toastTimer = 0;
let classroomEventPollVersion = 0;
let classroomEventPollTask = null;
let classroomEventPollController = null;
let classroomEventCursor = 0;
let classroomEventSessionId = null;
let classroomEventItems = [];

function toast(message) {
  const element = $("toast");
  element.textContent = message;
  element.classList.remove("hidden");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => element.classList.add("hidden"), 4200);
}

function openTokenDatabase() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open("veyon-campus-mobile", 1);
    request.onupgradeneeded = () => request.result.createObjectStore("session");
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function readToken() {
  const database = await openTokenDatabase();
  return new Promise((resolve, reject) => {
    const request = database.transaction("session", "readonly").objectStore("session").get("token");
    request.onsuccess = () => resolve(request.result || null);
    request.onerror = () => reject(request.error);
  });
}

async function saveToken(token) {
  const database = await openTokenDatabase();
  return new Promise((resolve, reject) => {
    const transaction = database.transaction("session", "readwrite");
    transaction.objectStore("session").put(token, "token");
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error);
  });
}

async function clearToken() {
  const database = await openTokenDatabase();
  return new Promise((resolve, reject) => {
    const transaction = database.transaction("session", "readwrite");
    transaction.objectStore("session").delete("token");
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error);
  });
}

async function api(path, { method = "GET", body, signal } = {}) {
  const headers = new Headers({ Accept: "application/json" });
  if (body !== undefined) headers.set("Content-Type", "application/json");
  if (accessToken) {
    headers.set("Authorization", "Bearer " + accessToken);
    headers.set("X-Veyon-Request-Nonce", crypto.randomUUID().replaceAll("-", ""));
    headers.set("X-Veyon-Request-Timestamp", new Date().toISOString());
  }
  const response = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    cache: "no-store",
    credentials: "omit",
    redirect: "error",
    referrerPolicy: "no-referrer",
    signal
  });
  const value = await response.json().catch(() => ({}));
  if (!response.ok) {
    const error = new Error(value.error || "请求失败（HTTP " + response.status + "）");
    error.status = response.status;
    throw error;
  }
  return value;
}

function currentProfile() {
  return profiles.find(profile => profile.id === profileSelect.value) || null;
}

function selectedTargets() {
  return Array.from(roomList.querySelectorAll("input[data-target]:checked"))
    .map(input => input.dataset.target)
    .filter(Boolean);
}

function updateSelectionCount() {
  const count = selectedTargets().length;
  $("selection-count").textContent = count ? "已选择 " + count + " 台电脑" : "未选择电脑";
  if (pendingReview && (pendingReview.profileId !== currentProfile()?.id ||
      !sameTargets(pendingReview.targets, selectedTargets()))) {
    pendingReview = null;
    $("operation-result").querySelector(".review-confirm")?.remove();
    const warning = document.createElement("p");
    warning.className = "fine-print review-expired";
    warning.textContent = "预设或目标已改变，原审核确认已取消；请重新读取审核统计。";
    $("operation-result").append(warning);
  }
  $("refresh-status").disabled = !count;
  $("enable-policy").disabled = !count || !currentProfile();
  $("disable-policy").disabled = !count || !currentProfile();
}

function sameTargets(left, right) {
  const normalize = values => [...values].sort((a, b) => a.localeCompare(b, "en", { sensitivity: "base" }));
  return JSON.stringify(normalize(left)) === JSON.stringify(normalize(right));
}

function renderRooms() {
  roomList.replaceChildren();
  for (const room of rooms) {
    const card = document.createElement("section");
    card.className = "room-card";
    const heading = document.createElement("div");
    heading.className = "room-heading";
    const name = document.createElement("strong");
    name.textContent = room.name || "未命名机房";
    const count = document.createElement("small");
    count.textContent = (room.targets || []).length + " 台";
    heading.append(name, count);
    const computers = document.createElement("div");
    computers.className = "computer-list";
    for (const target of room.targets || []) {
      const label = document.createElement("label");
      label.className = "computer-choice";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.dataset.target = target;
      input.addEventListener("change", updateSelectionCount);
      const value = document.createElement("span");
      value.className = "computer-name";
      value.textContent = target;
      label.append(input, value);
      computers.append(label);
    }
    card.append(heading, computers);
    roomList.append(card);
  }
  if (!rooms.length) {
    const empty = document.createElement("p");
    empty.className = "muted";
    empty.textContent = "教师电脑的 Veyon 目录中没有可选机房。";
    roomList.append(empty);
  }
  updateSelectionCount();
}

function renderProfiles() {
  profileSelect.replaceChildren();
  if (!profiles.length) {
    const option = document.createElement("option");
    option.value = "";
    option.textContent = "请先在教师电脑保存手机策略预设";
    profileSelect.append(option);
    renderProfileDescription();
    updateSelectionCount();
    return;
  }
  for (const profile of profiles) {
    const option = document.createElement("option");
    option.value = profile.id;
    const type = profile.kind === "website" ? "网站" : profile.kind === "application" ? "应用" : "系统";
    const mode = profile.mode === "Blocklist" ? "黑名单" :
      profile.mode === "Allowlist" ? "白名单" :
        profile.mode === "Enforce" ? "阻止" : profile.mode === "Audit" ? "审核" : "长期基线";
    const lifetime = profile.kind === "system" || profile.lifetimeMinutes === 0
      ? "不自动到期" : profile.lifetimeMinutes + " 分钟";
    option.textContent = profile.name + " · " + type + " " + mode + " · " + lifetime;
    profileSelect.append(option);
  }
  renderProfileDescription();
  updateSelectionCount();
}

function renderProfileDescription() {
  const profile = currentProfile();
  const description = $("profile-description");
  if (!profile) {
    description.textContent = "请先在教师电脑保存策略预设。";
    return;
  }
  const lifetime = profile.kind === "system" || profile.lifetimeMinutes === 0
    ? "无自动到期，需教师另行解除。" : "自动到期约 " + profile.lifetimeMinutes + " 分钟。";
  if (profile.kind === "system" && profile.systemSettings) {
    const enabled = systemPolicyLabels.filter(([key]) => profile.systemSettings[key])
      .map(([, label]) => label);
    description.textContent = "长期系统基线：" + enabled.join("、") + "。" + lifetime;
  } else {
    description.textContent = (profile.kind === "website" ? "网站策略" : "应用策略") + "；" + lifetime;
  }
}

function appendParagraph(parent, text) {
  const paragraph = document.createElement("p");
  paragraph.textContent = text;
  parent.append(paragraph);
}

function renderStatuses(items) {
  statusList.replaceChildren();
  for (const item of items) {
    const card = document.createElement("article");
    card.className = "result-card";
    const title = document.createElement("h3");
    title.textContent = item.target;
    const state = document.createElement("span");
    state.className = "state " + (item.online ? "warn" : "bad");
    state.textContent = item.state || (item.online ? "签名身份已验证" : "状态未知");
    card.append(title, state);
    if (item.agentVersion) appendParagraph(card, "Agent 版本：" + item.agentVersion);
    if (item.collectedUtc) appendParagraph(card, "最后响应：" + formatDateTime(item.collectedUtc));
    if (item.websiteMode) {
      const websiteMode = item.websiteMode === "disabled" ? "已解除" :
        item.websiteMode === "blocklist" ? "黑名单" : "白名单";
      appendParagraph(card, "网站限制：" + websiteMode + " · 版本 " +
        (item.websiteRevision ?? "未知") + formatExpiry(item.websiteExpiresUtc));
    } else appendParagraph(card, "网站限制：状态未知");
    if (!item.applicationSupported) appendParagraph(card, "应用限制：此学生包未启用应用策略");
    else if (item.applicationMode) {
      const mode = item.applicationMode === "disabled" ? "已解除" :
        item.applicationMode === "enforce" ? "阻止" : "审核";
      appendParagraph(card, "应用限制：" + mode + " · 版本 " +
        (item.applicationRevision ?? "未知") + formatExpiry(item.applicationExpiresUtc));
    } else appendParagraph(card, "应用限制：状态未知");
    const system = item.systemPolicy;
    if (!system) {
      appendParagraph(card, "长期系统限制：" + (item.online ? "此学生包未启用系统策略" : "状态未知"));
    } else if (!system.supported) {
      appendParagraph(card, "长期系统限制：此学生包未启用系统策略");
    } else if (system.pending) {
      appendParagraph(card, "长期系统限制：正在恢复或等待复核" + formatRevision(system.revision));
    } else if (!system.settings) {
      appendParagraph(card, "长期系统限制：尚未配置");
    } else {
      const active = systemPolicyLabels.some(([key]) => system.settings[key]);
      appendParagraph(card, "长期系统限制：" + (active ? "长期基线" : "已解除") +
        formatRevision(system.revision) + (active ? " · 不自动到期" : ""));
      for (const [key, label] of systemPolicyLabels) {
        appendParagraph(card, label + "：" + (system.settings[key] ? "启用" : "关闭"));
      }
    }
    if (item.needsReview) appendParagraph(card, "此设备结果需要复核；请查看详情并核对 Agent 身份和操作状态。");
    if (item.detail) appendParagraph(card, item.detail);
    statusList.append(card);
  }
  if (!items.length) {
    const empty = document.createElement("p");
    empty.className = "muted";
    empty.textContent = "没有返回状态。";
    statusList.append(empty);
  }
}

function formatExpiry(value) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return " · 到期时间未知";
  return " · 到期 " + new Intl.DateTimeFormat("zh-HK", { dateStyle: "short", timeStyle: "short" }).format(date);
}

function formatRevision(value) {
  return Number.isInteger(value) && value > 0 ? " · 版本 " + value : "";
}

function formatDateTime(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "未知" :
    new Intl.DateTimeFormat("zh-HK", { dateStyle: "short", timeStyle: "medium" }).format(date);
}

function renderOperation(result, request) {
  operationResult.replaceChildren();
  pendingReview = null;
  const summary = document.createElement("p");
  summary.className = "muted";
  summary.textContent = result.message;
  operationResult.append(summary);
  for (const item of result.results || []) {
    const card = document.createElement("article");
    card.className = "result-card";
    const title = document.createElement("h3");
    title.textContent = item.target;
    const state = document.createElement("span");
    state.className = "state " + (item.agentAccepted ? "warn" : item.needsReview ? "warn" : "bad");
    state.textContent = item.agentAccepted ? "Agent 已确认" : item.needsReview ? "待核对" : "失败";
    card.append(title, state);
    appendParagraph(card, item.detail);
    operationResult.append(card);
  }
  if (result.applicationReview?.length) {
    const reviewTitle = document.createElement("h3");
    reviewTitle.textContent = "应用审核/影响模拟";
    operationResult.append(reviewTitle);
    for (const target of result.applicationReview) {
      const card = document.createElement("article");
      card.className = "result-card";
      const title = document.createElement("h3");
      title.textContent = target.target;
      card.append(title);
      if (target.isSimulation) {
        appendParagraph(card, "这是已登记程序影响模拟，不含启动历史或实际阻止记录。");
        if (target.coverageNote) appendParagraph(card, target.coverageNote);
      }
      if (!target.rules?.length) appendParagraph(card, target.isSimulation
        ? "已登记程序清单中没有发现匹配规则的条目。"
        : "最近没有匹配的审核事件。");
      for (const rule of target.rules || []) {
        appendParagraph(card, target.isSimulation
          ? rule.displayName + "：预计命中 " + rule.wouldBlockCount + " 个已登记程序"
          : rule.displayName + "：审核命中 " + rule.wouldBlockCount + "，阻止记录 " + rule.blockedCount);
      }
      operationResult.append(card);
    }
  }
  if (result.requiresReview && result.reviewToken) {
    pendingReview = {
      profileId: request.profileId,
      targets: [...request.targets],
      token: result.reviewToken
    };
    const confirm = document.createElement("button");
    confirm.className = "danger-button review-confirm";
    confirm.type = "button";
    confirm.textContent = "我已阅读审核统计，确认启用阻止";
    confirm.addEventListener("click", completeReview);
    operationResult.append(confirm);
  } else pendingReview = null;
  const failedTargets = (result.results || []).filter(item => !item.agentAccepted).map(item => item.target);
  if (!result.requiresReview && failedTargets.length && lastOperation?.profileId === request.profileId) {
    const retry = document.createElement("button");
    retry.className = "secondary-button retry-failed";
    retry.type = "button";
    retry.textContent = "只重试 " + failedTargets.length + " 台未确认电脑";
    retry.addEventListener("click", () => retryFailed(failedTargets));
    operationResult.append(retry);
  }
}

async function loadDashboard() {
  pairView.classList.add("hidden");
  controlView.classList.remove("hidden");
  $("logout-button").classList.remove("hidden");
  const session = await api("/api/session");
  $("welcome-title").textContent = "你好，" + session.device.displayName;
  const [nextRooms, nextProfiles] = await Promise.all([api("/api/rooms"), api("/api/profiles")]);
  rooms = nextRooms;
  profiles = nextProfiles;
  renderRooms();
  renderProfiles();
  await refreshStatusIfSelected();
  startClassroomEventPolling();
}

function startClassroomEventPolling() {
  if (classroomEventPollTask || !accessToken) return;
  const version = ++classroomEventPollVersion;
  classroomEventPollTask = pollClassroomEvents(version).finally(() => {
    if (version === classroomEventPollVersion) classroomEventPollTask = null;
  });
}

function stopClassroomEventPolling() {
  classroomEventPollVersion++;
  classroomEventPollController?.abort();
  classroomEventPollController = null;
  classroomEventPollTask = null;
  classroomEventSessionId = null;
  classroomEventCursor = 0;
  classroomEventItems = [];
  renderClassroomEvents();
}

async function pollClassroomEvents(version) {
  while (version === classroomEventPollVersion && accessToken) {
    const controller = new AbortController();
    classroomEventPollController = controller;
    try {
      const page = await api("/api/classroom/events?after=" + classroomEventCursor,
        { signal: controller.signal });
      if (version !== classroomEventPollVersion) return;
      const nextSessionId = page.sessionId || null;
      if (nextSessionId !== classroomEventSessionId) {
        classroomEventSessionId = nextSessionId;
        classroomEventCursor = 0;
        classroomEventItems = [];
      }
      if (!nextSessionId) {
        classroomEventCursor = 0;
        $("classroom-events-panel").classList.add("hidden");
      } else {
        $("classroom-events-panel").classList.remove("hidden");
        if (!Number.isSafeInteger(page.cursor) || page.cursor < classroomEventCursor ||
            !Array.isArray(page.events) || page.events.length > 50)
          throw new Error("课堂消息分页无效。");
        const knownIds = new Set(classroomEventItems.map(item => item.eventId));
        for (const signedEvent of page.events) {
          const classroomEvent = readSignedClassroomEvent(signedEvent);
          validateMobileClassroomEvent(classroomEvent, nextSessionId);
          if (!knownIds.has(classroomEvent.eventId)) {
            knownIds.add(classroomEvent.eventId);
            classroomEventItems.push(classroomEvent);
          }
        }
        classroomEventItems = classroomEventItems.slice(-512);
        classroomEventCursor = page.cursor;
        $("classroom-event-status").textContent = "课堂进行中 · 新求助会自动显示。";
      }
      renderClassroomEvents();
    } catch (error) {
      if (version !== classroomEventPollVersion) return;
      $("classroom-event-status").textContent = error.status === 401
        ? "手机授权暂不可用；请重新连接教师电脑。"
        : "课堂消息连接暂时中断，正在自动重试。";
      renderClassroomEvents();
    } finally {
      if (classroomEventPollController === controller) classroomEventPollController = null;
    }
    await delay(5000);
  }
}

function readSignedClassroomEvent(signedJson) {
  if (typeof signedJson !== "string" || signedJson.length > 16384)
    throw new Error("课堂消息超过大小限制。");
  const envelope = JSON.parse(signedJson);
  if (typeof envelope.payload !== "string" || typeof envelope.signature !== "string" ||
      typeof envelope.publicKeyPem !== "string")
    throw new Error("课堂消息签名封装无效。");
  const payloadBytes = Uint8Array.from(atob(envelope.payload), character => character.charCodeAt(0));
  return JSON.parse(new TextDecoder().decode(payloadBytes));
}

function validateMobileClassroomEvent(classroomEvent, sessionId) {
  const allowedTypes = new Set(["helpRequested", "helpAcknowledged", "teacherReply", "helpResolved", "classroomNotice"]);
  const expiresUtc = Date.parse(classroomEvent.expiresUtc);
  const issuedUtc = Date.parse(classroomEvent.issuedUtc);
  if (classroomEvent.schemaVersion !== 1 || classroomEvent.purpose !== "VeyonCampus.ClassroomEvent.v1" ||
      classroomEvent.sessionId !== sessionId || !classroomEvent.eventId ||
      !["student", "teacher"].includes(classroomEvent.sender) || !allowedTypes.has(classroomEvent.type) ||
      typeof classroomEvent.target !== "string" || !classroomEvent.target ||
      !Number.isFinite(expiresUtc) || !Number.isFinite(issuedUtc) || expiresUtc <= issuedUtc ||
      expiresUtc - issuedUtc > 120000)
    throw new Error("课堂消息与当前课堂不匹配。");
}

function renderClassroomEvents() {
  const list = $("classroom-event-list");
  if (!list) return;
  list.replaceChildren();
  if (!classroomEventSessionId) {
    $("classroom-events-panel").classList.add("hidden");
    return;
  }
  $("classroom-events-panel").classList.remove("hidden");
  const currentEvents = classroomEventItems;
  const replies = new Map(currentEvents
    .filter(item => item.type === "teacherReply" && item.correlationId)
    .map(item => [item.correlationId, item]));
  const resolutions = new Set(currentEvents
    .filter(item => item.type === "helpResolved" && item.correlationId)
    .map(item => item.correlationId));
  const requests = currentEvents.filter(item => item.type === "helpRequested");
  const notices = currentEvents.filter(item => item.type === "classroomNotice");
  if (!requests.length && !notices.length) {
    appendParagraph(list, "暂无学生求助。");
    return;
  }
  for (const request of requests) {
    const card = document.createElement("article");
    card.className = "result-card";
    const title = document.createElement("h3");
    title.textContent = request.target + " 需要帮助";
    card.append(title);
    const reply = replies.get(request.eventId);
    const resolved = resolutions.has(request.eventId);
    const state = document.createElement("span");
    state.className = "state " + (resolved ? "good" : "warn");
    state.textContent = resolved ? "已解决" : reply ? "等待学生确认" : "等待教师回复";
    card.append(state);
    appendParagraph(card, "收到时间：" + formatDateTime(request.issuedUtc));
    if (reply) {
      appendParagraph(card, "教师回复：" + reply.message);
      if (resolved) appendParagraph(card, "学生已确认解决。");
    }
    else {
      const input = document.createElement("textarea");
      input.rows = 2;
      input.maxLength = 500;
      input.placeholder = "回复学生";
      input.className = "classroom-reply-input";
      input.setAttribute("aria-label", request.target + " 的回复");
      const button = document.createElement("button");
      button.className = "primary-button";
      button.type = "button";
      button.textContent = "发送回复";
      button.addEventListener("click", async () => {
        const message = input.value.trim();
        if (!message) {
          toast("请先输入回复内容。");
          return;
        }
        button.disabled = true;
        try {
          await api("/api/classroom/events/reply", {
            method: "POST",
            body: { helpEventId: request.eventId, message }
          });
          toast("回复已发送给学生。");
        } catch (error) {
          if (error.status === 400) {
            classroomEventItems = classroomEventItems.filter(item => item.eventId !== request.eventId);
            renderClassroomEvents();
          }
          toast(error.message);
          button.disabled = false;
        }
      });
      card.append(input, button);
    }
    list.append(card);
  }
  for (const notice of notices) {
    const card = document.createElement("article");
    card.className = "result-card";
    const title = document.createElement("h3");
    title.textContent = "课堂通知";
    card.append(title);
    appendParagraph(card, notice.message || "");
    list.append(card);
  }
}

async function refreshStatusIfSelected() {
  const targets = selectedTargets();
  if (!targets.length) return;
  $("refresh-status").disabled = true;
  statusList.replaceChildren();
  const loading = document.createElement("p");
  loading.className = "muted";
  loading.textContent = "正在读取学生电脑状态……";
  statusList.append(loading);
  try {
    renderStatuses(await api("/api/status", {
      method: "POST",
      body: { targets }
    }));
  } catch (error) {
    toast(error.message);
  } finally {
    updateSelectionCount();
  }
}

async function enablePolicy() {
  const profile = currentProfile();
  const targets = selectedTargets();
  if (!profile || !targets.length) return;
  const action = profile.kind === "system"
    ? "即将对 " + targets.length + " 台电脑启用长期系统限制：" +
      systemPolicyLabels.filter(([key]) => profile.systemSettings?.[key]).map(([, label]) => label).join("、") +
      "。策略不会自动到期；未选中的系统设置不会由此预设启用。是否继续？"
    : profile.kind === "application" && profile.mode === "Enforce"
    ? "先将所选电脑置于审核模式并读取审核统计。回执会核验 Agent 身份签名；请核对每台电脑的身份、策略版本和统计，教师确认前不会启用阻止。是否开始？"
    : "是否向 " + targets.length + " 台电脑发送“" + profile.name + "”策略？";
  if (!window.confirm(action)) return;
  await runPolicy({ profileId: profile.id, targets, enabled: true });
}

async function disablePolicy() {
  const profile = currentProfile();
  const targets = selectedTargets();
  if (!profile || !targets.length) return;
  const message = profile.kind === "system"
    ? "确认向 " + targets.length + " 台电脑解除长期系统策略管理？学生端会尝试恢复本工具应用前的原设置；检测到外部修改的值会保留。"
    : "确认向 " + targets.length + " 台电脑解除“" + profile.name + "”限制？";
  if (!window.confirm(message)) return;
  await runPolicy({ profileId: profile.id, targets, enabled: false });
}

async function runPolicy(body) {
  $("enable-policy").disabled = true;
  $("disable-policy").disabled = true;
  operationResult.replaceChildren();
  try {
    lastOperation = { ...body, targets: [...body.targets] };
    renderOperation(await api("/api/policy", { method: "POST", body }), body);
  } catch (error) {
    pendingReview = null;
    toast(error.message);
  } finally {
    updateSelectionCount();
  }
}

async function completeReview() {
  if (!pendingReview) return;
  const review = pendingReview;
  if (review.profileId !== currentProfile()?.id || !sameTargets(review.targets, selectedTargets())) {
    pendingReview = null;
    toast("预设或目标已改变，请重新审核。");
    return;
  }
  pendingReview = null;
  await runPolicy({
    profileId: review.profileId,
    targets: review.targets,
    enabled: true,
    reviewToken: review.token
  });
}

async function retryFailed(targets) {
  if (!lastOperation?.profileId || !targets.length) return;
  if (!window.confirm("只重试以下 " + targets.length + " 台未确认电脑：" + targets.join("、") + "？")) return;
  const request = { ...lastOperation, targets: [...targets] };
  const profile = profiles.find(item => item.id === request.profileId);
  if (profile?.kind === "application" && profile.mode === "Enforce" && request.enabled) {
    delete request.reviewToken;
  }
  await runPolicy(request);
}

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

async function waitForPairingApproval(ticket, expiresUtc) {
  const expires = Date.parse(expiresUtc);
  while (Number.isFinite(expires) && Date.now() < expires) {
    const result = await api("/api/pair-status", { method: "POST", body: { ticket } });
    if (result.state === "approved" && result.accessToken) return result;
    if (result.state === "rejected") throw new Error("教师拒绝了这次配对请求。请联系教师重新生成配对码。");
    if (result.state === "expired") throw new Error("配对请求已过期。请让教师重新生成配对码。");
    await delay(1100);
  }
  throw new Error("等待教师批准超时。请重新生成配对码并提交。");
}

async function pair(event) {
  event.preventDefault();
  const code = $("pair-code").value.trim();
  const deviceName = $("device-name").value.trim();
  const button = $("pair-form").querySelector("button[type=submit]");
  button.disabled = true;
  button.textContent = "等待教师批准…";
  try {
    const request = await api("/api/pair", {
      method: "POST",
      body: { pairingCode: code, deviceName }
    });
    toast("请求已发送，请在教师电脑核对并批准此手机。");
    const result = await waitForPairingApproval(request.ticket, request.expiresUtc);
    await saveToken(result.accessToken);
    accessToken = result.accessToken;
    $("pair-code").value = "";
    await loadDashboard();
    toast("手机已配对。请保管好这部设备；教师可以随时撤销访问。");
  } catch (error) {
    toast(error.message);
  } finally {
    button.disabled = false;
    button.textContent = "配对这部手机";
  }
}

async function signOut() {
  stopClassroomEventPolling();
  try { await api("/api/logout", { method: "POST", body: {} }); } catch {}
  await clearToken();
  accessToken = null;
  pendingReview = null;
  controlView.classList.add("hidden");
  pairView.classList.remove("hidden");
  $("logout-button").classList.add("hidden");
  showScannedPairingInvite();
}

function showScannedPairingInvite() {
  if (!scannedPairingCode) return;
  $("pair-code").value = scannedPairingCode;
  const status = $("pair-invite-status");
  status.textContent = "已读取教师端二维码。填写手机名称后点“配对这部手机”，教师仍需在电脑上批准。";
  status.classList.remove("hidden");
}

function setAll(checked) {
  for (const input of roomList.querySelectorAll("input[data-target]")) input.checked = checked;
  $("toggle-all").textContent = checked ? "取消全选" : "全选";
  updateSelectionCount();
}

$("pair-form").addEventListener("submit", pair);
$("refresh-rooms").addEventListener("click", async () => {
  try {
    rooms = await api("/api/rooms");
    renderRooms();
    toast("机房目录已刷新。");
  } catch (error) { toast(error.message); }
});
$("refresh-status").addEventListener("click", refreshStatusIfSelected);
$("enable-policy").addEventListener("click", enablePolicy);
$("disable-policy").addEventListener("click", disablePolicy);
$("logout-button").addEventListener("click", signOut);
$("toggle-all").addEventListener("click", () => {
  const inputs = Array.from(roomList.querySelectorAll("input[data-target]"));
  setAll(!inputs.length || inputs.some(input => !input.checked));
});
profileSelect.addEventListener("change", () => {
  renderProfileDescription();
  updateSelectionCount();
});

async function start() {
  if (!window.isSecureContext || !window.indexedDB || !window.crypto?.randomUUID) {
    toast("请在已信任教师根证书的 HTTPS 地址打开本页。");
    return;
  }
  if ("serviceWorker" in navigator) {
    navigator.serviceWorker.addEventListener("controllerchange", () => window.location.reload(), { once: true });
    navigator.serviceWorker.register("/service-worker.js").catch(() => {});
  }
  try {
    accessToken = await readToken();
    if (accessToken) {
      await loadDashboard();
      if (scannedPairingCode) toast("当前浏览器已有配对。退出后可继续使用刚扫描的邀请。");
      return;
    }
  } catch (error) {
    await clearToken().catch(() => {});
    accessToken = null;
    controlView.classList.add("hidden");
    pairView.classList.remove("hidden");
    $("logout-button").classList.add("hidden");
    toast(error.message || "无法读取手机配对凭据。");
  }
  showScannedPairingInvite();
}

start();
