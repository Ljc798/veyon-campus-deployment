const $ = id => document.getElementById(id);
const pairView = $("pair-view");
const controlView = $("control-view");
const profileSelect = $("profile-select");
const roomList = $("room-list");
const statusList = $("status-list");
const operationResult = $("operation-result");
const classroomModeResult = $("classroom-mode-result");
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
let activeClassroomTargets = [];
let activeClassroomMode = null;
let classroomTargetDefaultState = "unavailable";
let pendingReview = null;
let lastOperation = null;
let toastTimer = 0;
let classroomEventPollVersion = 0;
let classroomEventPollTask = null;
let classroomEventPollController = null;
let classroomEventExpiryTimer = 0;
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

function normalizeTarget(target) {
  return String(target || "").trim().toLowerCase();
}

function updateSelectionCount() {
  const targets = selectedTargets();
  const count = targets.length;
  const isClassroomDefault = activeClassroomTargets.length > 0 && sameTargets(targets, activeClassroomTargets);
  $("selection-count").textContent = isClassroomDefault
    ? "本堂课 · " + count + " 台电脑"
    : count ? "已选择 " + count + " 台电脑" : "未选择电脑";
  $("target-scope-status").textContent = classroomTargetDefaultState === "matched"
    ? isClassroomDefault ? "已默认选择本堂课全部电脑。" : "电脑范围已手动调整。"
    : classroomTargetDefaultState === "mismatch"
      ? count ? "本堂课电脑未能与机房清单完整匹配；请核对手动选择的范围。"
        : "本堂课电脑未能与机房清单完整匹配，未自动选择。请展开确认范围。"
      : count ? "当前没有活动课堂，请核对手动选择的电脑范围。"
        : "当前没有活动课堂。请选择电脑后查看或切换限制。";
  if (pendingReview?.kind !== "classroom" && pendingReview && (pendingReview.profileId !== currentProfile()?.id ||
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

function renderRooms(preserveSelection = false) {
  const selectedBeforeRefresh = preserveSelection
    ? new Set(selectedTargets().map(normalizeTarget))
    : new Set();
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
      input.checked = selectedBeforeRefresh.has(normalizeTarget(target));
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

function applyActiveClassroomTargetDefaults() {
  const targetPicker = $("target-picker");
  const requested = activeClassroomTargets.map(normalizeTarget);
  if (!requested.length) {
    classroomTargetDefaultState = "unavailable";
    targetPicker.open = true;
    updateSelectionCount();
    return;
  }

  const uniqueRequested = new Set(requested);
  const inputs = Array.from(roomList.querySelectorAll("input[data-target]"));
  const matched = inputs.filter(input => uniqueRequested.has(normalizeTarget(input.dataset.target)));
  const matchedUnique = new Set(matched.map(input => normalizeTarget(input.dataset.target)));
  if (uniqueRequested.size !== requested.length || matched.length !== uniqueRequested.size ||
      matchedUnique.size !== uniqueRequested.size) {
    classroomTargetDefaultState = "mismatch";
    targetPicker.open = true;
    updateSelectionCount();
    return;
  }

  for (const input of inputs) input.checked = uniqueRequested.has(normalizeTarget(input.dataset.target));
  classroomTargetDefaultState = "matched";
  targetPicker.open = false;
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
  const orderedProfiles = profiles
    .map((profile, index) => ({ profile, index }))
    .sort((left, right) => {
      // Keep temporary classroom policies ahead of persistent system baselines.
      const systemOrder = Number(left.profile.kind === "system") - Number(right.profile.kind === "system");
      if (systemOrder !== 0) return systemOrder;
      const leftUpdated = Date.parse(left.profile.updatedUtc || "");
      const rightUpdated = Date.parse(right.profile.updatedUtc || "");
      const leftTime = Number.isFinite(leftUpdated) ? leftUpdated : Number.NEGATIVE_INFINITY;
      const rightTime = Number.isFinite(rightUpdated) ? rightUpdated : Number.NEGATIVE_INFINITY;
      return rightTime - leftTime || left.index - right.index;
    })
    .map(item => item.profile);
  for (const [index, profile] of orderedProfiles.entries()) {
    const option = document.createElement("option");
    option.value = profile.id;
    const type = profile.kind === "website" ? "网站" : profile.kind === "application" ? "应用" : "系统";
    const mode = profile.mode === "Blocklist" ? "黑名单" :
      profile.mode === "Allowlist" ? "白名单" :
        profile.mode === "Enforce" ? "阻止" : profile.mode === "Audit" ? "审核" : "长期基线";
    const lifetime = profile.kind === "system" || profile.lifetimeMinutes === 0
      ? "不自动到期" : profile.lifetimeMinutes + " 分钟";
    option.textContent = (index === 0 ? "默认 · " : "") +
      profile.name + " · " + type + " " + mode + " · " + lifetime;
    profileSelect.append(option);
  }
  profileSelect.value = orderedProfiles[0].id;
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

function renderOperation(result, request, container = operationResult) {
  container.replaceChildren();
  pendingReview = null;
  const summary = document.createElement("p");
  summary.className = "muted";
  summary.textContent = result.message;
  container.append(summary);
  for (const item of result.results || []) {
    const card = document.createElement("article");
    card.className = "result-card";
    const title = document.createElement("h3");
    title.textContent = item.target;
    const state = document.createElement("span");
    state.className = "state " + (item.needsReview ? "warn" : item.agentAccepted ? "good" : "bad");
    state.textContent = item.needsReview ? "需核对" : item.agentAccepted ? "状态已确认" : "失败";
    card.append(title, state);
    appendParagraph(card, item.detail);
    container.append(card);
  }
  if (result.applicationReview?.length) {
    const reviewTitle = document.createElement("h3");
    reviewTitle.textContent = "应用审核/影响模拟";
    container.append(reviewTitle);
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
      container.append(card);
    }
  }
  if (result.requiresReview && result.reviewToken) {
    pendingReview = request.kind === "classroom"
      ? { kind: "classroom", mode: request.mode, token: result.reviewToken }
      : { kind: "policy", profileId: request.profileId, targets: [...request.targets], token: result.reviewToken };
    const confirm = document.createElement("button");
    confirm.className = "danger-button review-confirm";
    confirm.type = "button";
    confirm.textContent = request.kind === "classroom"
      ? "我已阅读，确认开始练习"
      : "我已阅读审核统计，确认启用阻止";
    confirm.addEventListener("click", completeReview);
    container.append(confirm);
  } else pendingReview = null;
  const failedTargets = (result.results || []).filter(item => !item.agentAccepted).map(item => item.target);
  if (request.kind === "policy" && !result.requiresReview && failedTargets.length &&
      lastOperation?.profileId === request.profileId) {
    const retry = document.createElement("button");
    retry.className = "secondary-button retry-failed";
    retry.type = "button";
    retry.textContent = "只重试 " + failedTargets.length + " 台未确认电脑";
    retry.addEventListener("click", () => retryFailed(failedTargets));
    container.append(retry);
  }
}

async function loadDashboard() {
  pairView.classList.add("hidden");
  controlView.classList.remove("hidden");
  $("logout-button").classList.remove("hidden");
  const session = await api("/api/session");
  $("welcome-title").textContent = "你好，" + session.device.displayName;
  activeClassroomTargets = Array.isArray(session.activeClassroomTargets)
    ? session.activeClassroomTargets.filter(target => typeof target === "string" && target.trim().length > 0)
    : [];
  activeClassroomMode = session.classroomMode || null;
  renderClassroomMode(true);
  const [nextRooms, nextProfiles] = await Promise.all([api("/api/rooms"), api("/api/profiles")]);
  rooms = nextRooms;
  profiles = nextProfiles;
  renderRooms();
  applyActiveClassroomTargetDefaults();
  renderProfiles();
  await refreshStatusIfSelected();
  await loadClassroomRestores();
  startClassroomEventPolling();
}

function renderClassroomMode(setDefaults = false) {
  const panel = $("classroom-mode-panel");
  const active = activeClassroomTargets.length > 0;
  panel.classList.toggle("hidden", !active);
  if (setDefaults) $("advanced-controls").open = !active;
  if (!active) return;
  const practice = activeClassroomMode === "practice";
  const pill = $("classroom-mode-value");
  pill.textContent = practice ? "练习" : "正常课堂";
  pill.className = "pill mode" + (practice ? " practice" : "");
  $("classroom-mode-toggle").textContent = practice ? "恢复正常" : "开始练习";
  $("classroom-mode-description").textContent = practice
    ? "恢复正常会解除本堂课仍由课堂拥有的网站和应用限制。"
    : "练习会自动使用最近保存的网站和应用预设；长期系统策略不会改变。";
}

async function runClassroomMode(mode, reviewToken = null) {
  const button = $("classroom-mode-toggle");
  button.disabled = true;
  button.textContent = "正在切换…";
  classroomModeResult.replaceChildren();
  try {
    const body = { mode };
    if (reviewToken) body.reviewToken = reviewToken;
    const result = await api("/api/classroom/mode", { method: "POST", body });
    renderOperation(result, { kind: "classroom", mode }, classroomModeResult);
    const session = await api("/api/session");
    activeClassroomTargets = Array.isArray(session.activeClassroomTargets)
      ? session.activeClassroomTargets.filter(target => typeof target === "string" && target.trim().length > 0)
      : [];
    activeClassroomMode = session.classroomMode || null;
    renderClassroomMode();
    await loadClassroomRestores();
  } catch (error) {
    toast(error.message);
  } finally {
    button.disabled = activeClassroomTargets.length === 0;
    renderClassroomMode();
  }
}

async function loadClassroomRestores() {
  const response = await api("/api/classroom/restores");
  if (!Number.isSafeInteger(response.count) || response.count < 0 || !Array.isArray(response.items) ||
      response.items.length > 50 || !Number.isSafeInteger(response.hiddenCount) || response.hiddenCount < 0)
    throw new Error("课堂待恢复列表无效。");
  const panel = $("classroom-restore-panel");
  panel.classList.toggle("hidden", response.count === 0);
  $("classroom-restore-count").textContent = response.count + " 项";
  const list = $("classroom-restore-list");
  list.replaceChildren();
  for (const item of response.items) {
    const row = document.createElement("article");
    row.className = "result-card";
    const title = document.createElement("h3");
    title.textContent = item.deviceLabel;
    row.append(title);
    appendParagraph(row, item.roomName + " · " + item.policy);
    list.append(row);
  }
  if (response.hiddenCount > 0) appendParagraph(list, "另有 " + response.hiddenCount + " 项，重试时会一并处理。");
  const button = $("retry-classroom-restores");
  button.disabled = response.count === 0 || activeClassroomTargets.length > 0;
  $("classroom-restore-description").textContent = activeClassroomTargets.length > 0
    ? "课堂进行中；下课后可重试。教师电脑会按原机房和电脑编号重新核对。"
    : "教师电脑会按原机房和电脑编号重新核对；无法唯一匹配的项目会保留。";
}

async function retryClassroomRestores() {
  const button = $("retry-classroom-restores");
  button.disabled = true;
  button.textContent = "正在核对…";
  try {
    const result = await api("/api/classroom/restores/retry", { method: "POST" });
    renderOperation(result, { kind: "classroom-restore" }, $("classroom-restore-result"));
  } catch (error) {
    toast(error.message);
  } finally {
    button.textContent = "重试待恢复项";
    await loadClassroomRestores().catch(error => toast(error.message));
  }
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
  clearTimeout(classroomEventExpiryTimer);
  classroomEventExpiryTimer = 0;
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
        activeClassroomTargets = [];
        activeClassroomMode = null;
        renderClassroomMode();
        await loadClassroomRestores();
      } else {
        $("classroom-events-panel").classList.remove("hidden");
        if (!Number.isSafeInteger(page.cursor) || page.cursor < classroomEventCursor ||
            !Array.isArray(page.events) || page.events.length > 50)
          throw new Error("课堂消息分页无效。");
        const knownIds = new Set(classroomEventItems.map(item => item.eventId));
        for (const signedEvent of page.events) {
          const classroomEvent = readSignedClassroomEvent(signedEvent);
          validateMobileClassroomEvent(classroomEvent, nextSessionId);
          if (Date.parse(classroomEvent.expiresUtc) <= Date.now()) continue;
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
      issuedUtc > Date.now() + 60000 || expiresUtc - issuedUtc > 120000)
    throw new Error("课堂消息与当前课堂不匹配。");
}

function pruneExpiredClassroomEvents(now = Date.now()) {
  const requests = classroomEventItems.filter(item => item.type === "helpRequested");
  const relatedEvents = classroomEventItems.filter(item =>
    ["helpAcknowledged", "teacherReply", "helpResolved"].includes(item.type) && item.correlationId);
  const relatedByRequest = new Map();
  for (const item of relatedEvents) {
    const related = relatedByRequest.get(item.correlationId) || [];
    related.push(item);
    relatedByRequest.set(item.correlationId, related);
  }

  const visibleRequestIds = new Set();
  const nextExpiries = [];
  for (const request of requests) {
    const related = relatedByRequest.get(request.eventId) || [];
    const expiresUtc = Math.max(Date.parse(request.expiresUtc),
      ...related.map(item => Date.parse(item.expiresUtc)));
    if (expiresUtc > now) {
      visibleRequestIds.add(request.eventId);
      nextExpiries.push(expiresUtc);
    }
  }

  classroomEventItems = classroomEventItems.filter(item => {
    if (item.type === "helpRequested") return visibleRequestIds.has(item.eventId);
    if (["helpAcknowledged", "teacherReply", "helpResolved"].includes(item.type))
      return visibleRequestIds.has(item.correlationId);
    const expiresUtc = Date.parse(item.expiresUtc);
    if (expiresUtc > now) nextExpiries.push(expiresUtc);
    return expiresUtc > now;
  });

  clearTimeout(classroomEventExpiryTimer);
  classroomEventExpiryTimer = 0;
  if (nextExpiries.length) {
    const nextExpiry = Math.min(...nextExpiries);
    classroomEventExpiryTimer = setTimeout(() => {
      classroomEventExpiryTimer = 0;
      renderClassroomEvents();
    }, Math.max(1, nextExpiry - now + 1));
  }
}

function renderClassroomEvents() {
  const list = $("classroom-event-list");
  if (!list) return;
  pruneExpiredClassroomEvents();
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
    appendParagraph(list, "当前没有待处理求助或有效通知。");
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
    title.textContent = "全班通知";
    card.append(title);
    appendParagraph(card, notice.message || "");
    list.append(card);
  }
}

async function sendClassroomNotice() {
  if (!classroomEventSessionId) {
    toast("当前没有活动课堂。");
    return;
  }
  const input = $("classroom-notice-input");
  const button = $("send-classroom-notice");
  const message = input.value.trim();
  if (!message) {
    toast("请先输入课堂通知。");
    input.focus();
    return;
  }
  button.disabled = true;
  button.textContent = "发送中…";
  try {
    const result = await api("/api/classroom/events/notice", {
      method: "POST",
      body: { message }
    });
    input.value = "";
    toast("通知已提交给本堂课 " + result.targetCount + " 台目标。");
  } catch (error) {
    toast(error.message);
  } finally {
    button.disabled = !classroomEventSessionId;
    button.textContent = "发给全班";
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
  if (review.kind === "classroom") {
    pendingReview = null;
    await runClassroomMode(review.mode, review.token);
    return;
  }
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
    renderRooms(true);
    toast("机房目录已刷新。");
  } catch (error) { toast(error.message); }
});
$("refresh-status").addEventListener("click", refreshStatusIfSelected);
$("enable-policy").addEventListener("click", enablePolicy);
$("disable-policy").addEventListener("click", disablePolicy);
$("logout-button").addEventListener("click", signOut);
$("send-classroom-notice").addEventListener("click", sendClassroomNotice);
$("classroom-mode-toggle").addEventListener("click", () =>
  runClassroomMode(activeClassroomMode === "practice" ? "normal" : "practice"));
$("retry-classroom-restores").addEventListener("click", retryClassroomRestores);
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
