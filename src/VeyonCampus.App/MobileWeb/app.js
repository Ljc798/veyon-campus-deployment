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
let serverClockOffsetMs = 0;
let rooms = [];
let profiles = [];
let activeClassroomTargets = [];
let activeClassroomMode = null;
let activeClassroomSessionId = null;
let activeClassroomSeatLocations = new Map();
let activeClassroomCountdownDeadline = null;
let classroomScreenPreviewLease = null;
let classroomScreenPreviewTargets = [];
let classroomScreenPreviewStartController = null;
let classroomScreenPreviewObserver = null;
const classroomScreenPreviewVisibleTargets = new Set();
const classroomScreenPreviewControllers = new Map();
const classroomScreenPreviewTimers = new Map();
const classroomScreenPreviewObjectUrls = new Map();
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

function createRequestNonce() {
  if (typeof crypto.randomUUID === "function") return crypto.randomUUID().replaceAll("-", "");
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, value => value.toString(16).padStart(2, "0")).join("");
}

function toast(message) {
  const element = $("toast");
  element.textContent = message;
  element.classList.remove("hidden");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => element.classList.add("hidden"), 4200);
}

function setActiveControlTab(tab) {
  const classroom = tab === "classroom";
  const classroomButton = $("classroom-tab-button");
  const strategyButton = $("strategy-tab-button");
  classroomButton.classList.toggle("selected", classroom);
  strategyButton.classList.toggle("selected", !classroom);
  classroomButton.setAttribute("aria-selected", String(classroom));
  strategyButton.setAttribute("aria-selected", String(!classroom));
  $("classroom-tab-panel").classList.toggle("hidden", !classroom);
  $("strategy-tab-panel").classList.toggle("hidden", classroom);
}

function setConnectionState(connected) {
  const pill = $("connection-pill");
  pill.textContent = connected ? "已连接" : "连接中断";
  pill.className = "pill " + (connected ? "good" : "warn");
  $("reconnect-button").classList.toggle("hidden", connected);
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

async function api(path, { method = "GET", body, signal, keepalive = false } = {}) {
  for (let attempt = 0; attempt < 2; attempt++) {
    const headers = new Headers({ Accept: "application/json" });
    if (body !== undefined) headers.set("Content-Type", "application/json");
    if (accessToken) {
      headers.set("Authorization", "Bearer " + accessToken);
      headers.set("X-Veyon-Request-Nonce", createRequestNonce());
      headers.set("X-Veyon-Request-Timestamp", new Date(Date.now() + serverClockOffsetMs).toISOString());
    }
    const requestStartedAt = Date.now();
    const response = await fetch(path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      cache: "no-store",
      credentials: "omit",
      redirect: "error",
      referrerPolicy: "no-referrer",
      signal,
      keepalive
    });
    const receivedAt = Date.now();
    const serverTime = Date.parse(response.headers.get("X-Veyon-Server-Time") || "");
    if (Number.isFinite(serverTime))
      serverClockOffsetMs = serverTime - ((requestStartedAt + receivedAt) / 2);
    const value = await response.json().catch(() => ({}));
    if (!response.ok) {
      if (attempt === 0 && response.status === 401 &&
          String(value.error || "").includes("请求时间无效或随机数重复") && Number.isFinite(serverTime))
        continue;
      const error = new Error(value.error || "请求失败（HTTP " + response.status + "）");
      error.status = response.status;
      error.apiMessage = value.error || "";
      throw error;
    }
    return value;
  }
  throw new Error("请求失败，请重试。");
}

async function startClassroomScreenPreview() {
  const button = $("classroom-screen-preview-toggle");
  const status = $("classroom-screen-preview-status");
  if (classroomScreenPreviewLease) {
    await stopClassroomScreenPreview(true);
    return;
  }
  if (!activeClassroomSessionId || activeClassroomTargets.length === 0) {
    status.textContent = "当前没有活动课堂。";
    return;
  }

  button.disabled = true;
  button.textContent = "正在打开…";
  status.textContent = "正在连接课堂电脑。";
  const startController = new AbortController();
  classroomScreenPreviewStartController = startController;
  try {
    const result = await api("/api/classroom/screen-preview/start", {
      method: "POST", body: {}, signal: startController.signal
    });
    if (startController.signal.aborted || document.hidden) {
      if (typeof result?.leaseId === "string") {
        try {
          await api("/api/classroom/screen-preview/stop", {
            method: "POST", body: { leaseId: result.leaseId }, keepalive: true
          });
        } catch { /* The server-side lease expires if the response cannot be reconciled. */ }
      }
      return;
    }
    const targets = Array.isArray(result?.targets) ? result.targets : [];
    const valid = typeof result?.leaseId === "string" && typeof result?.sessionId === "string" &&
      result.sessionId === activeClassroomSessionId && targets.length > 0 && targets.length <= 5 &&
      Number.isSafeInteger(result.totalTargets) && result.totalTargets >= targets.length &&
      new Set(targets.map(normalizeTarget)).size === targets.length &&
      targets.every(target => typeof target === "string" &&
        activeClassroomTargets.some(active => normalizeTarget(active) === normalizeTarget(target)));
    if (!valid) {
      if (typeof result?.leaseId === "string") {
        try {
          await api("/api/classroom/screen-preview/stop", {
            method: "POST", body: { leaseId: result.leaseId }, keepalive: true
          });
        } catch { /* The server-side lease expires if the response cannot be reconciled. */ }
      }
      throw new Error("屏幕巡视范围无效，请刷新课堂状态后重试。");
    }

    classroomScreenPreviewLease = { id: result.leaseId, sessionId: result.sessionId };
    classroomScreenPreviewTargets = targets;
    renderClassroomScreenPreviewTiles(targets);
    button.textContent = "停止巡视";
    button.setAttribute("aria-expanded", "true");
    status.textContent = result.totalTargets > targets.length
      ? `当前 PoC 显示前 ${targets.length} 台，共 ${result.totalTargets} 台；只刷新手机屏幕上可见的画面。`
      : "只刷新手机屏幕上可见的画面；每台电脑最多每 2 秒更新一次。";
    observeClassroomScreenPreviewTiles();
  } catch (error) {
    if (error.name === "AbortError") return;
    if (classroomScreenPreviewLease) await stopClassroomScreenPreview(false);
    status.textContent = error.message || "屏幕巡视暂时无法开启。";
    button.textContent = "查看屏幕";
  } finally {
    if (classroomScreenPreviewStartController === startController)
      classroomScreenPreviewStartController = null;
    button.disabled = false;
  }
}

function renderClassroomScreenPreviewTiles(targets) {
  const grid = $("classroom-screen-preview-grid");
  grid.replaceChildren();
  classroomScreenPreviewVisibleTargets.clear();
  for (const target of targets) {
    const key = normalizeTarget(target);
    const seat = activeClassroomSeatLocations.get(key);
    const label = seat ? `第 ${seat.row} 排 · 第 ${seat.column} 位` : target;
    const tile = document.createElement("article");
    tile.className = "classroom-screen-preview-tile";
    tile.dataset.target = target;

    const heading = document.createElement("div");
    heading.className = "classroom-screen-preview-tile-heading";
    const name = document.createElement("strong");
    name.textContent = label;
    const timestamp = document.createElement("small");
    timestamp.textContent = "等待画面";
    heading.append(name, timestamp);

    const image = document.createElement("img");
    image.className = "classroom-screen-preview-image";
    image.alt = `${label} 的屏幕预览`;
    image.width = 320;
    image.height = 180;
    image.loading = "lazy";
    image.decoding = "async";

    const frameStatus = document.createElement("p");
    frameStatus.className = "classroom-screen-preview-frame-status";
    frameStatus.textContent = "滚动到画面时开始读取。";
    tile.append(heading, image, frameStatus);
    grid.append(tile);
  }
  grid.classList.remove("hidden");
}

function observeClassroomScreenPreviewTiles() {
  classroomScreenPreviewObserver?.disconnect();
  const tiles = Array.from($("classroom-screen-preview-grid").querySelectorAll(".classroom-screen-preview-tile"));
  if (!("IntersectionObserver" in window)) {
    for (const tile of tiles) makeClassroomScreenPreviewVisible(tile);
    return;
  }
  classroomScreenPreviewObserver = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (entry.isIntersecting) makeClassroomScreenPreviewVisible(entry.target);
      else makeClassroomScreenPreviewHidden(entry.target);
    }
  }, { threshold: 0.15 });
  for (const tile of tiles) classroomScreenPreviewObserver.observe(tile);
}

function makeClassroomScreenPreviewVisible(tile) {
  const target = tile.dataset.target;
  if (!target || classroomScreenPreviewVisibleTargets.has(target)) return;
  classroomScreenPreviewVisibleTargets.add(target);
  void refreshClassroomScreenPreviewFrame(tile);
}

function makeClassroomScreenPreviewHidden(tile) {
  const target = tile.dataset.target;
  if (!target) return;
  classroomScreenPreviewVisibleTargets.delete(target);
  clearTimeout(classroomScreenPreviewTimers.get(target));
  classroomScreenPreviewTimers.delete(target);
  classroomScreenPreviewControllers.get(target)?.abort();
  classroomScreenPreviewControllers.delete(target);
  releaseClassroomScreenPreviewUrl(target);
  tile.querySelector("img")?.removeAttribute("src");
  const frameStatus = tile.querySelector(".classroom-screen-preview-frame-status");
  if (frameStatus) frameStatus.textContent = "滚动到画面时自动更新。";
  const timestamp = tile.querySelector("small");
  if (timestamp) timestamp.textContent = "等待画面";
}

async function refreshClassroomScreenPreviewFrame(tile) {
  const target = tile.dataset.target;
  const lease = classroomScreenPreviewLease;
  if (!target || !lease || !classroomScreenPreviewVisibleTargets.has(target) || document.hidden ||
      classroomScreenPreviewControllers.has(target)) return;
  const controller = new AbortController();
  classroomScreenPreviewControllers.set(target, controller);
  const frameStatus = tile.querySelector(".classroom-screen-preview-frame-status");
  if (frameStatus && !tile.querySelector("img")?.hasAttribute("src")) frameStatus.textContent = "正在读取画面…";
  try {
    const frame = await fetchClassroomScreenPreviewFrame(target, lease.id, controller.signal);
    const objectUrl = URL.createObjectURL(frame.blob);
    if (classroomScreenPreviewLease?.id !== lease.id ||
        !classroomScreenPreviewVisibleTargets.has(target) || document.hidden) {
      URL.revokeObjectURL(objectUrl);
      return;
    }
    releaseClassroomScreenPreviewUrl(target);
    classroomScreenPreviewObjectUrls.set(target, objectUrl);
    const image = tile.querySelector("img");
    if (image) image.src = objectUrl;
    const captured = Date.parse(frame.capturedUtc || "");
    const time = Number.isFinite(captured)
      ? new Date(captured).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
      : "刚刚";
    const timestamp = tile.querySelector("small");
    if (timestamp) timestamp.textContent = time;
    if (frameStatus) frameStatus.textContent = frame.cached ? "画面缓存" : "实时单帧";
  } catch (error) {
    if (error.name !== "AbortError" && frameStatus)
      frameStatus.textContent = error.message || "画面暂时不可用。";
    if (error.status === 401 || error.status === 403) void stopClassroomScreenPreview(false);
  } finally {
    if (classroomScreenPreviewControllers.get(target) === controller)
      classroomScreenPreviewControllers.delete(target);
    if (classroomScreenPreviewLease?.id === lease.id &&
        classroomScreenPreviewVisibleTargets.has(target) && !document.hidden) {
      const timer = setTimeout(() => void refreshClassroomScreenPreviewFrame(tile), 2000);
      classroomScreenPreviewTimers.set(target, timer);
    }
  }
}

async function fetchClassroomScreenPreviewFrame(target, leaseId, signal) {
  const headers = new Headers({ Accept: "image/png" });
  headers.set("Authorization", "Bearer " + accessToken);
  headers.set("X-Veyon-Request-Nonce", createRequestNonce());
  headers.set("X-Veyon-Request-Timestamp", new Date(Date.now() + serverClockOffsetMs).toISOString());
  headers.set("X-Veyon-Screen-Preview-Lease", leaseId.replaceAll("-", ""));
  const response = await fetch("/api/classroom/screen-preview/" + encodeURIComponent(target), {
    method: "GET",
    headers,
    cache: "no-store",
    credentials: "omit",
    redirect: "error",
    referrerPolicy: "no-referrer",
    signal
  });
  if (!response.ok) {
    const value = await response.json().catch(() => ({}));
    const serverTime = Date.parse(response.headers.get("X-Veyon-Server-Time") || "");
    if (Number.isFinite(serverTime)) serverClockOffsetMs = serverTime - Date.now();
    const error = new Error(value.error || "屏幕读取失败（HTTP " + response.status + "）");
    error.status = response.status;
    throw error;
  }
  if (response.headers.get("Content-Type")?.split(";")[0] !== "image/png")
    throw new Error("屏幕响应格式无效。");
  const blob = await response.blob();
  if (blob.size === 0 || blob.size > 1024 * 1024) throw new Error("屏幕缩略图大小无效。");
  return {
    blob,
    capturedUtc: response.headers.get("X-Captured-Utc"),
    cached: response.headers.get("X-Screen-Frame") === "cached"
  };
}

async function stopClassroomScreenPreview(showStatus) {
  const lease = classroomScreenPreviewLease;
  classroomScreenPreviewLease = null;
  classroomScreenPreviewStartController?.abort();
  classroomScreenPreviewStartController = null;
  classroomScreenPreviewObserver?.disconnect();
  classroomScreenPreviewObserver = null;
  for (const controller of classroomScreenPreviewControllers.values()) controller.abort();
  classroomScreenPreviewControllers.clear();
  for (const timer of classroomScreenPreviewTimers.values()) clearTimeout(timer);
  classroomScreenPreviewTimers.clear();
  for (const target of classroomScreenPreviewObjectUrls.keys()) releaseClassroomScreenPreviewUrl(target);
  classroomScreenPreviewVisibleTargets.clear();
  classroomScreenPreviewTargets = [];
  const grid = $("classroom-screen-preview-grid");
  grid.replaceChildren();
  grid.classList.add("hidden");
  const button = $("classroom-screen-preview-toggle");
  button.textContent = "查看屏幕";
  button.setAttribute("aria-expanded", "false");
  if (showStatus) $("classroom-screen-preview-status").textContent = "屏幕巡视已停止。";
  if (lease && accessToken) {
    try {
      await api("/api/classroom/screen-preview/stop", {
        method: "POST", body: { leaseId: lease.id }, keepalive: true
      });
    } catch { /* The server-side lease still expires and capture only runs on requests. */ }
  }
}

function releaseClassroomScreenPreviewUrl(target) {
  const url = classroomScreenPreviewObjectUrls.get(target);
  if (url) URL.revokeObjectURL(url);
  classroomScreenPreviewObjectUrls.delete(target);
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
  const scopeStatus = $("target-scope-status");
  if (classroomTargetDefaultState === "mismatch") {
    scopeStatus.textContent = count
      ? "本堂课电脑未能与机房清单完整匹配；请核对手动选择的范围。"
      : "本堂课电脑未能与机房清单完整匹配，未自动选择。请展开确认范围。";
  } else if (classroomTargetDefaultState === "unavailable") {
    scopeStatus.textContent = count
      ? "当前没有活动课堂，请核对手动选择的电脑范围。"
      : "当前没有活动课堂。请选择电脑后查看或切换限制。";
  } else {
    scopeStatus.textContent = "";
  }
  scopeStatus.classList.toggle("hidden", !scopeStatus.textContent);
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
    empty.textContent = "没有可选机房，请先在教师电脑添加。";
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
    option.textContent = "教师电脑还没有可用策略。";
    profileSelect.append(option);
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
        profile.mode === "Enforce" ? "限制" : profile.mode === "Audit" ? "检查影响" : "长期限制";
    const lifetime = profile.kind === "system" || profile.lifetimeMinutes === 0
      ? "不自动到期" : profile.lifetimeMinutes + " 分钟";
    option.textContent = (index === 0 ? "默认 · " : "") +
      profile.name + " · " + type + " " + mode + " · " + lifetime;
    profileSelect.append(option);
  }
  profileSelect.value = orderedProfiles[0].id;
  updateSelectionCount();
}

function appendParagraph(parent, text) {
  const paragraph = document.createElement("p");
  paragraph.textContent = text;
  parent.append(paragraph);
}

function extractSingleHttpLink(message) {
  if (typeof message !== "string") return null;
  const matches = [...message.matchAll(/https?:\/\/[^\s<>]+/gi)];
  if (matches.length !== 1) return null;
  const match = matches[0];
  let value = match[0];
  while (isTrailingClassroomLinkPunctuation(value))
    value = value.slice(0, -1);
  if (!value || value.length > 2048) return null;
  try {
    const url = new URL(value);
    if ((url.protocol !== "http:" && url.protocol !== "https:") || !url.hostname ||
        url.username || url.password || url.href.length > 2048) return null;
    return { href: url.href, start: match.index, end: match.index + value.length };
  } catch {
    return null;
  }
}

function isTrailingClassroomLinkPunctuation(value) {
  const character = value.charAt(value.length - 1);
  const pairs = { ")": "(", "]": "[", "}": "{" };
  if (pairs[character]) {
    const count = target => [...value].filter(item => item === target).length;
    return count(character) > count(pairs[character]);
  }
  return /[.,!?;:，。！？：；”’'"]$/u.test(character || "");
}

function appendClassroomNoticeMessage(parent, message) {
  const paragraph = document.createElement("p");
  const link = extractSingleHttpLink(message);
  if (!link) {
    paragraph.textContent = message;
    parent.append(paragraph);
    return;
  }

  const anchor = document.createElement("a");
  anchor.href = link.href;
  anchor.target = "_blank";
  anchor.rel = "noopener noreferrer";
  anchor.textContent = message.slice(link.start, link.end);
  paragraph.append(document.createTextNode(message.slice(0, link.start)), anchor,
    document.createTextNode(message.slice(link.end)));
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
    state.textContent = item.state || (item.online ? "在线" : "状态未知");
    card.append(title, state);
    if (item.collectedUtc) appendParagraph(card, "最后响应：" + formatDateTime(item.collectedUtc));
    if (item.websiteMode) {
      const websiteMode = item.websiteMode === "disabled" ? "已解除" :
        item.websiteMode === "blocklist" ? "黑名单" : "白名单";
      appendParagraph(card, "网站限制：" + websiteMode + formatExpiry(item.websiteExpiresUtc));
    } else appendParagraph(card, "网站限制：状态未知");
    if (!item.applicationSupported) appendParagraph(card, "应用限制：未设置");
    else if (item.applicationMode) {
      const mode = item.applicationMode === "disabled" ? "已解除" :
        item.applicationMode === "enforce" ? "阻止" : "审核";
      appendParagraph(card, "应用限制：" + mode + formatExpiry(item.applicationExpiresUtc));
    } else appendParagraph(card, "应用限制：状态未知");
    const system = item.systemPolicy;
    if (!system) {
      appendParagraph(card, "长期系统限制：" + (item.online ? "未设置" : "状态未知"));
    } else if (!system.supported) {
      appendParagraph(card, "长期系统限制：未设置");
    } else if (system.pending) {
      appendParagraph(card, "长期系统限制：正在恢复或待核对");
    } else if (!system.settings) {
      appendParagraph(card, "长期系统限制：未设置");
    } else {
      const active = systemPolicyLabels.some(([key]) => system.settings[key]);
      appendParagraph(card, "长期系统限制：" + (active ? "已启用" : "已解除") +
        (active ? " · 不自动到期" : ""));
      for (const [key, label] of systemPolicyLabels) {
        appendParagraph(card, label + "：" + (system.settings[key] ? "启用" : "关闭"));
      }
    }
    if (item.needsReview) appendParagraph(card, "需要核对；请查看教师电脑上的设备结果。");
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
  const formatted = formatDateTime(value);
  return formatted === "未知" ? " · 到期时间未知" : " · 到期 " + formatted;
}

function formatDateTime(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "未知";
  const parts = Object.fromEntries(new Intl.DateTimeFormat("zh-CN", {
    year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit",
    timeZone: "Asia/Shanghai", hourCycle: "h23"
  }).formatToParts(date).map(part => [part.type, part.value]));
  return `${parts.year}-${parts.month}-${parts.day} ${parts.hour}:${parts.minute}`;
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
    reviewTitle.textContent = "影响检查结果";
    container.append(reviewTitle);
    for (const target of result.applicationReview) {
      const card = document.createElement("article");
      card.className = "result-card";
      const title = document.createElement("h3");
      title.textContent = target.target;
      card.append(title);
      if (target.isSimulation) {
        appendParagraph(card, "影响检查只预测结果，不会实际拦截。");
        if (target.coverageNote) appendParagraph(card, target.coverageNote);
      }
      if (!target.rules?.length) appendParagraph(card, target.isSimulation
        ? "没有发现可能受影响的软件。"
        : "最近没有相关记录。");
      for (const rule of target.rules || []) {
        appendParagraph(card, target.isSimulation
          ? rule.displayName + "：预计限制 " + rule.wouldBlockCount + " 个程序"
          : rule.displayName + "：匹配 " + rule.wouldBlockCount + " 次，阻止 " + rule.blockedCount + " 次");
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
      : "我已查看影响检查，确认启用限制";
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
  applyClassroomSession(session);
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

async function connectDashboard() {
  try {
    await loadDashboard();
    setConnectionState(true);
    return true;
  } catch (error) {
    if (error.status === 401 &&
        ["手机配对已撤销或凭据无效。", "需要手机配对凭据。"].includes(error.apiMessage)) {
      await clearToken().catch(() => {});
      accessToken = null;
      controlView.classList.add("hidden");
      pairView.classList.remove("hidden");
      $("logout-button").classList.add("hidden");
      showScannedPairingInvite();
      toast("教师端已撤销此手机的访问，需要重新配对。");
      return false;
    }
    pairView.classList.add("hidden");
    controlView.classList.remove("hidden");
    $("logout-button").classList.remove("hidden");
    setConnectionState(false);
    toast("教师端暂时拒绝连接，点击“重连”再试。" + (error.message ? " " + error.message : ""));
    return false;
  }
}

function applyClassroomSession(session) {
  const nextTargets = Array.isArray(session?.activeClassroomTargets)
    ? session.activeClassroomTargets.filter(target => typeof target === "string" && target.trim().length > 0)
    : [];
  const nextSessionId = typeof session?.activeClassroomSessionId === "string"
    ? session.activeClassroomSessionId
    : null;
  if (classroomScreenPreviewLease &&
      (classroomScreenPreviewLease.sessionId !== nextSessionId ||
       !sameTargets(classroomScreenPreviewTargets, nextTargets.slice(0, 5))))
    void stopClassroomScreenPreview(false);
  activeClassroomTargets = nextTargets;
  activeClassroomSessionId = nextSessionId;
  $("classroom-screen-preview").classList.toggle("hidden", !nextSessionId || nextTargets.length === 0);
  activeClassroomMode = session?.classroomMode || null;
  activeClassroomSeatLocations = new Map();
  const rawLocations = session?.activeClassroomSeatLocations;
  if (Array.isArray(rawLocations) && rawLocations.length <= 150) {
    const validated = new Map();
    let valid = rawLocations.length > 0;
    for (const location of rawLocations) {
      const key = normalizeTarget(location?.target);
      if (!key || !Number.isSafeInteger(location?.row) || location.row < 1 || location.row > 150 ||
          !Number.isSafeInteger(location?.column) || location.column < 1 || location.column > 150 ||
          validated.has(key)) {
        valid = false;
        break;
      }
      validated.set(key, { target: location.target, row: location.row, column: location.column });
    }
    if (valid) activeClassroomSeatLocations = validated;
  }
  const deadline = Date.parse(session?.activeClassroomCountdown?.deadlineUtc || "");
  activeClassroomCountdownDeadline = activeClassroomTargets.length && Number.isFinite(deadline)
    ? deadline
    : null;
  renderClassroomCountdown();
  renderClassroomTaskProgress(session?.activeClassroomTaskProgress);
}

function renderClassroomTaskProgress(progress) {
  const panel = $("classroom-task-progress");
  const summary = $("classroom-task-progress-summary");
  const list = $("classroom-task-progress-list");
  if (!panel || !summary || !list) return;
  list.replaceChildren();
  const tasks = progress?.tasks;
  const valid = activeClassroomTargets.length > 0 && Array.isArray(tasks) &&
    tasks.length > 0 && tasks.length <= 20 && progress.totalCount === tasks.length &&
    tasks.every(task => typeof task?.title === "string" && task.title.length > 0 &&
      task.title.length <= 120 && task.title === task.title.trim() &&
      !/[\u0000-\u001f\u007f-\u009f\u2028\u2029]/u.test(task.title) && typeof task.isCompleted === "boolean");
  if (!valid) {
    panel.classList.add("hidden");
    return;
  }
  const completedCount = tasks.filter(task => task.isCompleted).length;
  if (progress.completedCount !== completedCount) {
    panel.classList.add("hidden");
    return;
  }

  summary.textContent = `${completedCount}/${tasks.length} 已完成`;
  for (const task of tasks) {
    const item = document.createElement("li");
    item.className = task.isCompleted ? "classroom-task-progress-item completed" : "classroom-task-progress-item";
    const mark = document.createElement("span");
    mark.className = "classroom-task-progress-mark";
    mark.textContent = task.isCompleted ? "✓" : "○";
    const title = document.createElement("span");
    title.textContent = task.title;
    item.append(mark, title);
    list.append(item);
  }
  panel.classList.remove("hidden");
}

function renderClassroomCountdown() {
  const panel = $("classroom-countdown");
  const value = $("classroom-countdown-value");
  if (!panel || !value) return;
  const visible = activeClassroomTargets.length > 0 && activeClassroomCountdownDeadline !== null;
  panel.classList.toggle("hidden", !visible);
  if (!visible) return;
  const seconds = Math.max(0, Math.ceil((activeClassroomCountdownDeadline - Date.now()) / 1000));
  if (seconds === 0) {
    value.textContent = "时间到";
    return;
  }
  const remaining = new Date(seconds * 1000);
  const hours = Math.floor(seconds / 3600);
  value.textContent = hours > 0
    ? `${String(hours).padStart(2, "0")}:${String(remaining.getUTCMinutes()).padStart(2, "0")}:${String(remaining.getUTCSeconds()).padStart(2, "0")}`
    : `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(remaining.getUTCSeconds()).padStart(2, "0")}`;
}

function renderClassroomMode(setDefaults = false) {
  const panel = $("classroom-mode-panel");
  const active = activeClassroomTargets.length > 0;
  panel.classList.toggle("hidden", !active);
  if (setDefaults) setActiveControlTab(active ? "classroom" : "strategy");
  if (!active) return;
  const practice = activeClassroomMode === "practice";
  const pill = $("classroom-mode-value");
  pill.textContent = practice ? "练习" : "正常课堂";
  pill.className = "pill mode" + (practice ? " practice" : "");
  $("classroom-mode-toggle").textContent = practice ? "恢复正常" : "开始练习";
  $("classroom-mode-description").textContent = practice
    ? "练习策略已应用到本堂课。"
    : "使用已保存的网站和应用策略。";
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
    applyClassroomSession(session);
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
        activeClassroomSeatLocations = new Map();
        activeClassroomCountdownDeadline = null;
        renderClassroomCountdown();
        renderClassroomMode();
        await loadClassroomRestores();
      } else {
        $("classroom-events-panel").classList.remove("hidden");
        applyClassroomSession(await api("/api/session"));
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
      nextExpiries.push(Date.parse(request.expiresUtc), expiresUtc,
        ...related.map(item => Date.parse(item.expiresUtc)));
    }
  }

  classroomEventItems = classroomEventItems.filter(item => {
    if (item.type === "helpRequested") return visibleRequestIds.has(item.eventId);
    if (["helpAcknowledged", "teacherReply", "helpResolved"].includes(item.type)) {
      if (!visibleRequestIds.has(item.correlationId)) return false;
      if (item.type === "teacherReply" && Date.parse(item.expiresUtc) <= now && item.message)
        item.message = "";
      return true;
    }
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
    const seat = activeClassroomSeatLocations.get(normalizeTarget(request.target));
    if (seat) appendParagraph(card, `座位：第 ${seat.row} 排 · 第 ${seat.column} 位`);
    const reply = replies.get(request.eventId);
    const resolved = resolutions.has(request.eventId);
    const state = document.createElement("span");
    state.className = "state " + (resolved ? "good" : "warn");
    state.textContent = resolved ? "已解决" : reply ? "等待学生确认" : "等待教师回复";
    card.append(state);
    appendParagraph(card, "收到时间：" + formatDateTime(request.issuedUtc));
    if (reply) {
      appendParagraph(card, reply.message
        ? "教师回复：" + reply.message
        : "教师回复已过期，等待学生状态。");
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
    appendClassroomNoticeMessage(card, notice.message || "");
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

async function waitForPairingApproval(ticket) {
  while (true) {
    const result = await api("/api/pair-status", { method: "POST", body: { ticket } });
    if (result.state === "approved" && result.accessToken) return result;
    if (result.state === "rejected") throw new Error("教师拒绝了这次配对请求。请联系教师重新生成配对码。");
    if (result.state === "expired") throw new Error("配对请求已过期。请让教师重新生成配对码。");
    await delay(1100);
  }
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
    const result = await waitForPairingApproval(request.ticket);
    await saveToken(result.accessToken);
    accessToken = result.accessToken;
    $("pair-code").value = "";
    if (await connectDashboard()) toast("手机已配对。");
  } catch (error) {
    toast(error.message);
  } finally {
    button.disabled = false;
    button.textContent = "配对这部手机";
  }
}

async function signOut() {
  await stopClassroomScreenPreview(false);
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
  status.textContent = "二维码已读取。填写手机名称并配对，等待教师批准。";
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
$("reconnect-button").addEventListener("click", () => void connectDashboard());
$("classroom-tab-button").addEventListener("click", () => setActiveControlTab("classroom"));
$("strategy-tab-button").addEventListener("click", () => setActiveControlTab("strategy"));
$("send-classroom-notice").addEventListener("click", sendClassroomNotice);
$("classroom-screen-preview-toggle").addEventListener("click", () => void startClassroomScreenPreview());
$("classroom-mode-toggle").addEventListener("click", () =>
  runClassroomMode(activeClassroomMode === "practice" ? "normal" : "practice"));
$("retry-classroom-restores").addEventListener("click", retryClassroomRestores);
$("toggle-all").addEventListener("click", () => {
  const inputs = Array.from(roomList.querySelectorAll("input[data-target]"));
  setAll(!inputs.length || inputs.some(input => !input.checked));
});
profileSelect.addEventListener("change", updateSelectionCount);

setInterval(renderClassroomCountdown, 1000);
document.addEventListener("visibilitychange", () => {
  if (document.hidden && (classroomScreenPreviewLease || classroomScreenPreviewStartController))
    void stopClassroomScreenPreview(true);
});
window.addEventListener("pagehide", () => {
  if (classroomScreenPreviewLease || classroomScreenPreviewStartController)
    void stopClassroomScreenPreview(false);
});

async function start() {
  if (!window.isSecureContext || !window.indexedDB || !window.crypto?.getRandomValues) {
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
      await connectDashboard();
      if (scannedPairingCode) toast("当前浏览器已有配对。退出后可继续使用刚扫描的邀请。");
      return;
    }
  } catch (error) {
    accessToken = null;
    controlView.classList.add("hidden");
    pairView.classList.remove("hidden");
    $("logout-button").classList.add("hidden");
    toast(error.message || "无法读取手机配对凭据。");
  }
  showScannedPairingInvite();
}

start();
