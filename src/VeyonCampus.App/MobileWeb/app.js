const $ = id => document.getElementById(id);
const pairView = $("pair-view");
const controlView = $("control-view");
const controlTabs = $("control-tabs");
const roomList = $("room-list");
const statusList = $("status-list");
const operationResult = $("operation-result");
const systemPolicyLabels = [
  ["lockWallpaper", "固定桌面背景"],
  ["prohibitTimeChanges", "不允许修改日期、时间和时区"],
  ["prohibitNetworkChanges", "不允许更改网络设置"],
  ["prohibitSoftwareInstallation", "不允许安装软件"],
  ["prohibitAccountManagement", "不允许管理账户或修改本人密码"],
  ["prohibitControlPanel", "限制控制面板和系统设置"]
];
const recentDirectoryRoomStorageKey = "veyon-campus-mobile.recent-directory-room";
function readRecentDirectoryRoomName() {
  try { return localStorage.getItem(recentDirectoryRoomStorageKey); }
  catch { return null; }
}
function rememberDirectoryRoomName(name) {
  try {
    if (name) localStorage.setItem(recentDirectoryRoomStorageKey, name);
    else localStorage.removeItem(recentDirectoryRoomStorageKey);
  } catch { /* Room memory is optional; a blocked storage API must not stop the page. */ }
}
let scannedPairingCode = null;
if (window.location.hash) {
  const match = /^#pair=([0-9]{8})$/.exec(window.location.hash);
  history.replaceState(null, "", window.location.pathname + window.location.search);
  if (match) scannedPairingCode = match[1];
}
let accessToken = null;
let connectionAttemptInFlight = false;
let serverClockOffsetMs = 0;
let rooms = [];
let policyPageMode = "directory";
let selectedComputerTarget = null;
let computerDirectoryScrollY = null;
let selectedDirectoryRoomName = readRecentDirectoryRoomName();
let websiteListsAvailable = false;
let savedPolicyProfiles = [];
let savedPolicyProfilesAvailable = false;
let savedPolicyProfilesLoading = false;
let pendingSavedPolicyReview = null;

let websiteListsUpdatedUtc = null;
let websiteListsSavedSnapshot = "";
let websiteListsSaving = false;
let statusReadGeneration = 0;
const latestStatusByTarget = new Map();
let activeClassroomTargets = [];
let activeClassroomSessionId = null;
let classroomQuickControlSnapshot = null;
let classroomQuickControlUnavailable = false;
let classroomQuickControlBusy = false;
let activeClassroomSeatLocations = new Map();
let activeClassroomScreenTargets = [];
let activeClassroomCountdownDeadline = null;
let classroomScreenPreviewLease = null;
let classroomScreenPreviewMode = "screenshot";
let classroomScreenPreviewPaused = false;
let classroomScreenPreviewTargets = [];
let classroomScreenPreviewQuality = "low";
let classroomScreenPreviewRefreshSeconds = 10;
let classroomScreenPreviewSettingsPending = false;
let classroomScreenPreviewStartController = null;
let singleTargetPreviewPending = false;
let singleTargetPreviewId = null;
let singleTargetPreviewStartController = null;
let classroomScreenPreviewObserver = null;
const classroomScreenPreviewVisibleTargets = new Set();
const classroomScreenPreviewControllers = new Map();
const classroomScreenPreviewTimers = new Map();
const classroomScreenPreviewObjectUrls = new Map();
let classroomTargetDefaultState = "unavailable";
let policyOperationInFlight = false;
let toastTimer = 0;
let classroomEventPollVersion = 0;
let classroomEventPollTask = null;
let classroomEventPollController = null;
let classroomEventExpiryTimer = 0;
let classroomEventCursor = 0;
let classroomEventSessionId = null;
let classroomEventItems = [];
let activeClassroomChatTargets = [];
let selectedClassroomChatTargetId = null;
let classroomChatMessages = [];
let classroomChatRevision = 0;
let classroomChatPollVersion = 0;
let classroomChatPollTask = null;
let classroomChatPollController = null;
let classroomChatExpiryTimer = 0;
let classroomChatDraftEventId = null;
let classroomChatDraftMessage = null;
let classroomChatSending = false;
let classroomChatStatusText = "";
const classroomChatDrafts = new Map();

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
  const pages = [
    ["classroom", "classroom-tab-button", "classroom-tab-panel"],
    ["policy", "policy-tab-button", "policy-tab-panel"],
    ["monitor", "monitor-tab-button", "monitor-tab-panel"],
    ["settings", "settings-tab-button", "settings-tab-panel"]
  ];
  const selected = pages.some(([name]) => name === tab) ? tab : "classroom";
  for (const [name, buttonId, panelId] of pages) {
    const active = name === selected;
    const button = $(buttonId);
    button.classList.toggle("selected", active);
    button.setAttribute("aria-selected", String(active));
    $(panelId).classList.toggle("hidden", !active);
  }
  if (selected === "monitor") renderMonitorTargets();
  else {
    classroomScreenPreviewStartController?.abort();
    singleTargetPreviewStartController?.abort();
    if (classroomScreenPreviewLease) void stopClassroomScreenPreview(false);
  }
}

function isClassroomMonitorVisible() {
  return !document.hidden && $("monitor-tab-button").getAttribute("aria-selected") === "true";
}

function setConnectionState(connected) {
  const pill = $("connection-pill");
  pill.textContent = connected ? "已连接" : "连接中断";
  pill.className = "pill " + (connected ? "good" : "warn");
  $("reconnect-button").classList.toggle("hidden", connected);
}

function setControlVisibility(visible) {
  controlView.classList.toggle("hidden", !visible);
  controlTabs.classList.toggle("hidden", !visible);
  $("mobile-messages-open").disabled = !visible || !activeClassroomSessionId;
  if (!visible) closeMobileMessages();
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

async function resumeTokenFromCookie() {
  const response = await fetch("/api/mobile-resume", {
    method: "POST",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json" },
    body: "{}"
  });
  if (response.status === 401) return null;
  if (!response.ok) throw new Error("暂时无法连接教师电脑，请确认手机控制已启动后重试。");
  const result = await response.json();
  if (typeof result.accessToken !== "string" || result.accessToken.length < 32)
    throw new Error("教师电脑暂时无法确认手机连接，请重试。");
  await saveToken(result.accessToken).catch(() => {});
  return result.accessToken;
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
    let response;
    try {
      response = await fetch(path, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
        cache: "no-store",
        credentials: path === "/api/pair-status" ? "same-origin" : "omit",
        redirect: "error",
        referrerPolicy: "no-referrer",
        signal,
        keepalive
      });
    } catch (error) {
      if (error?.name === "AbortError") throw error;
      if (accessToken) setConnectionState(false);
      throw new Error("无法连接教师电脑，请检查网络后重试。");
    }
    const receivedAt = Date.now();
    const serverTime = Date.parse(response.headers.get("X-Veyon-Server-Time") || "");
    if (Number.isFinite(serverTime))
      serverClockOffsetMs = serverTime - ((requestStartedAt + receivedAt) / 2);
    const value = await response.json().catch(() => ({}));
    if (!response.ok) {
      if (attempt === 0 && response.status === 401 &&
          String(value.error || "").includes("请求时间无效或随机数重复") && Number.isFinite(serverTime))
        continue;
      const serverMessage = typeof value.error === "string" ? value.error.trim() : "";
      const error = new Error(mobileApiErrorMessage(path, response.status, serverMessage));
      error.status = response.status;
      error.pairingRevoked = response.status === 401 &&
        ["手机配对已撤销或凭据无效。", "需要手机配对凭据。",
          "此手机配对已撤销或凭据失效。"].includes(value.error);
      throw error;
    }
    if (accessToken) setConnectionState(true);
    return value;
  }
  throw new Error("请求失败，请重试。");
}

function mobileApiErrorMessage(path, status, serverMessage = "") {
  if (path === "/api/pair" && (status === 400 || status === 401))
    return "连接码不正确或已过期，请向教师重新获取。";
  if ((path === "/api/pair" || path === "/api/pair-status") && status === 403)
    return "请把手机连接到教师电脑所在的校园网后重试。";
  if (path === "/api/pair-status" && status === 404)
    return "连接请求已失效，请让教师重新生成连接码。";
  if ((path === "/api/status" || path === "/api/policy" || path.startsWith("/api/policy/")) &&
      status >= 400 && status < 600 && serverMessage)
    return serverMessage.slice(0, 300);
  if (path === "/api/website-policy-lists" && status >= 500 && status < 600 && serverMessage)
    return serverMessage.slice(0, 300);
  if (status === 401) return "手机连接已失效，请重新连接教师电脑后重试。";
  if (status === 403) return "教师电脑暂时无法处理这项操作，请刷新后重试。";
  if (status === 409) return serverMessage.slice(0, 300) || "电脑或课堂状态已变化，请刷新后重试。";
  if (path === "/api/website-policy-lists" && status === 400)
    return "名单保存失败：请检查域名格式，并确认每份名单不超过 1000 个域名。";
  if (path.startsWith("/api/classroom/screen-preview") &&
      status >= 400 && status < 500 && serverMessage)
    return serverMessage.slice(0, 300);
  if (status === 429) return "操作较频繁，请稍后重试。";
  if (status === 501) return serverMessage.slice(0, 300) || "此功能暂不可用，请更新教师电脑程序。";
  if (status === 503 && serverMessage) return serverMessage.slice(0, 300);
  if (status >= 500) return "教师电脑暂时无法处理，请稍后重试。";
  return "操作未完成，请检查信息后重试。";
}

function screenPreviewErrorMessage(error, fallback) {
  if (Number.isInteger(error?.status))
    return mobileApiErrorMessage("/api/classroom/screen-preview", error.status,
      typeof error.message === "string" ? error.message : "");
  const message = typeof error?.message === "string" ? error.message.trim() : "";
  if (!message || /^(failed to fetch|load failed|networkerror|the operation was aborted)$/i.test(message))
    return fallback;
  return message.length > 300 ? `${message.slice(0, 297)}…` : message;
}

async function startClassroomScreenPreview() {
  const button = $("monitor-preview-toggle");
  const status = $("classroom-screen-preview-status");
  if (classroomScreenPreviewSettingsPending || singleTargetPreviewPending ||
      classroomScreenPreviewStartController || !isClassroomMonitorVisible()) return;
  if (classroomScreenPreviewLease) {
    await stopClassroomScreenPreview(true);
    return;
  }
  if (!activeClassroomSessionId || activeClassroomTargets.length === 0) {
    status.textContent = "课堂模式尚未在教师端确认教室范围。";
    return;
  }
  const startingTargets = classroomScreenTargets();
  const startingSessionId = activeClassroomSessionId;
  const requestedTargets = startingTargets.map(target => target.targetId);
  if (requestedTargets.length === 0 || requestedTargets.length > 150) {
    status.textContent = requestedTargets.length > 150
      ? "当前可查看座位数量超过上限，请联系管理员。"
      : "请先在教师电脑为学生座位选好学生电脑。";
    toast(status.textContent);
    return;
  }

  button.disabled = true;
  button.textContent = "正在打开…";
  status.textContent = "正在连接学生电脑。";
  const startController = new AbortController();
  classroomScreenPreviewStartController = startController;
  try {
    const result = await api("/api/classroom/screen-preview/start", {
      method: "POST", body: {
        targets: requestedTargets,
        quality: classroomScreenPreviewQuality,
        refreshIntervalSeconds: classroomScreenPreviewRefreshSeconds
      }, signal: startController.signal
    });
    if (startController.signal.aborted || !isClassroomMonitorVisible()) {
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
    const contextStillCurrent = !startController.signal.aborted && isClassroomMonitorVisible() &&
      activeClassroomSessionId === startingSessionId &&
      sameClassroomScreenTargets(startingTargets, classroomScreenTargets());
    const valid = typeof result?.leaseId === "string" && typeof result?.sessionId === "string" &&
      result.sessionId === startingSessionId && targets.length === requestedTargets.length &&
      targets.length <= 150 &&
      sameTargets(targets, requestedTargets) &&
      Number.isSafeInteger(result.totalTargets) && result.totalTargets === startingTargets.length &&
      ["low", "standard", "high"].includes(result.quality) &&
      [3, 5, 10, 15, 30, 60].includes(result.refreshIntervalSeconds) &&
      new Set(targets.map(normalizeTarget)).size === targets.length &&
      targets.every(target => typeof target === "string" &&
        startingTargets.some(active => normalizeTarget(active.targetId) === normalizeTarget(target))) &&
      contextStillCurrent;
    if (!valid) {
      if (typeof result?.leaseId === "string") {
        try {
          await api("/api/classroom/screen-preview/stop", {
            method: "POST", body: { leaseId: result.leaseId }, keepalive: true
          });
        } catch { /* The server-side lease expires if the response cannot be reconciled. */ }
      }
      if (!contextStillCurrent) {
        status.textContent = "课堂或座位信息已变化，屏幕查看已停止。";
        return;
      }
      throw new Error("屏幕查看范围无效，请刷新学生互动状态后重试。");
    }

    classroomScreenPreviewLease = { id: result.leaseId, sessionId: result.sessionId };
    classroomScreenPreviewQuality = result.quality;
    classroomScreenPreviewRefreshSeconds = result.refreshIntervalSeconds;
    classroomScreenPreviewTargets = targets;
    renderClassroomScreenPreviewTiles(targets);
    button.textContent = "停止查看";
    button.setAttribute("aria-expanded", "true");
    renderMonitorTargets();
    status.textContent = "";
    $("monitor-preview-guidance").textContent = `画面每 ${classroomScreenPreviewRefreshSeconds} 秒更新；停止查看或离开此页后会自动停止。`;
    observeClassroomScreenPreviewTiles();
  } catch (error) {
    if (error.name === "AbortError") return;
    if (classroomScreenPreviewLease) await stopClassroomScreenPreview(false);
    const message = screenPreviewErrorMessage(error,
      "暂时无法查看学生屏幕，请确认课堂仍在进行后重试。");
    status.textContent = message;
    toast(message);
    button.textContent = "开始查看全部";
  } finally {
    if (classroomScreenPreviewStartController === startController)
      classroomScreenPreviewStartController = null;
    button.disabled = false;
    renderMonitorTargets();
  }
}

async function pauseClassroomScreenPreview() {
  const lease = classroomScreenPreviewLease;
  if (!lease || classroomScreenPreviewPaused) return;
  classroomScreenPreviewPaused = true;
  for (const timer of classroomScreenPreviewTimers.values()) clearTimeout(timer);
  classroomScreenPreviewTimers.clear();
  for (const controller of classroomScreenPreviewControllers.values()) controller.abort();
  classroomScreenPreviewControllers.clear();
  for (const tile of $("classroom-screen-preview-grid").querySelectorAll(".classroom-screen-preview-tile")) {
    const hasFrame = tile.querySelector("img")?.hasAttribute("src");
    const frameStatus = tile.querySelector(".classroom-screen-preview-frame-status");
    if (frameStatus) frameStatus.textContent = hasFrame ? "已暂停，保留最近画面。" : "已暂停查看。";
    tile.querySelector(".monitor-tile-retry-button")?.classList.add("hidden");
  }
  $("classroom-screen-preview-status").textContent = "屏幕查看已暂停。";
  $("monitor-preview-guidance").textContent = "已暂停采集，最近画面会保留在此页；继续时会重新打开巡视授权。";
  renderMonitorTargets();
  try {
    await api("/api/classroom/screen-preview/stop", {
      method: "POST", body: { leaseId: lease.id }, keepalive: true
    });
  } catch { /* No frames are requested while paused; any unreachable lease expires on the server. */ }
}

async function resumeClassroomScreenPreview() {
  const previousLease = classroomScreenPreviewLease;
  if (!classroomScreenPreviewPaused || !previousLease || classroomScreenPreviewStartController ||
      singleTargetPreviewPending || classroomScreenPreviewSettingsPending || !isClassroomMonitorVisible()) return;
  const requestedTargets = [...classroomScreenPreviewTargets];
  const startingTargets = classroomScreenTargets();
  const sessionId = activeClassroomSessionId;
  const refreshIntervalSeconds = classroomScreenPreviewMode === "single-target"
    ? 3 : classroomScreenPreviewRefreshSeconds;
  if (!sessionId || previousLease.sessionId !== sessionId || requestedTargets.length === 0 ||
      requestedTargets.length > 150 || requestedTargets.some(target =>
        !startingTargets.some(active => normalizeTarget(active.targetId) === normalizeTarget(target)))) {
    await stopClassroomScreenPreview(false);
    $("classroom-screen-preview-status").textContent = "课堂或座位信息已变化，请停止后重新打开屏幕查看。";
    return;
  }

  const controller = new AbortController();
  classroomScreenPreviewStartController = controller;
  $("classroom-screen-preview-status").textContent = "正在恢复屏幕查看…";
  renderMonitorTargets();
  try {
    const result = await api("/api/classroom/screen-preview/start", {
      method: "POST", body: {
        targets: requestedTargets,
        quality: classroomScreenPreviewQuality,
        refreshIntervalSeconds
      }, signal: controller.signal
    });
    const targets = Array.isArray(result?.targets) ? result.targets : [];
    const contextStillCurrent = !controller.signal.aborted && isClassroomMonitorVisible() &&
      activeClassroomSessionId === sessionId && sameClassroomScreenTargets(startingTargets, classroomScreenTargets());
    const valid = typeof result?.leaseId === "string" && result.sessionId === sessionId &&
      targets.length === requestedTargets.length && sameTargets(targets, requestedTargets) &&
      targets.length <= 150 && Number.isSafeInteger(result.totalTargets) &&
      result.totalTargets === startingTargets.length && result.quality === classroomScreenPreviewQuality &&
      result.refreshIntervalSeconds === refreshIntervalSeconds && contextStillCurrent;
    if (!valid) {
      if (typeof result?.leaseId === "string") {
        try {
          await api("/api/classroom/screen-preview/stop", {
            method: "POST", body: { leaseId: result.leaseId }, keepalive: true
          });
        } catch { /* Any unconfirmed lease expires without frame requests. */ }
      }
      if (!contextStillCurrent) {
        await stopClassroomScreenPreview(false);
        $("classroom-screen-preview-status").textContent = "课堂或座位信息已变化，屏幕查看已停止。";
        return;
      }
      throw new Error("屏幕查看范围已变化，请刷新课堂状态后重试。");
    }
    classroomScreenPreviewLease = { id: result.leaseId, sessionId: result.sessionId };
    classroomScreenPreviewTargets = targets;
    classroomScreenPreviewRefreshSeconds = result.refreshIntervalSeconds;
    classroomScreenPreviewPaused = false;
    classroomScreenPreviewVisibleTargets.clear();
    $("classroom-screen-preview-status").textContent = "";
    observeClassroomScreenPreviewTiles();
  } catch (error) {
    if (error.name === "AbortError") return;
    const message = screenPreviewErrorMessage(error, "暂时无法恢复屏幕查看，请检查连接后重试。");
    $("classroom-screen-preview-status").textContent = message;
    toast(message);
  } finally {
    if (classroomScreenPreviewStartController === controller)
      classroomScreenPreviewStartController = null;
    renderMonitorTargets();
  }
}

async function updateClassroomScreenPreviewSettings() {
  if (classroomScreenPreviewSettingsPending || singleTargetPreviewPending ||
      classroomScreenPreviewStartController ||
      classroomScreenPreviewMode === "single-target") return;
  const quality = $("monitor-preview-quality").value;
  const refreshSeconds = Number($("monitor-preview-refresh").value);
  if (!["low", "standard", "high"].includes(quality) ||
      ![3, 5, 10, 15, 30, 60].includes(refreshSeconds)) {
    toast("画面设置无效，请重新选择。");
    return;
  }
  const restartPreview = Boolean(classroomScreenPreviewLease && classroomScreenPreviewMode === "screenshot");
  const previousTargets = restartPreview ? [...classroomScreenPreviewTargets] : [];
  classroomScreenPreviewQuality = quality;
  classroomScreenPreviewRefreshSeconds = refreshSeconds;
  if (!restartPreview) {
    renderMonitorTargets();
    return;
  }

  classroomScreenPreviewSettingsPending = true;
  renderMonitorTargets();
  try {
    await stopClassroomScreenPreview(false);
    if (previousTargets.some(target =>
        !classroomScreenTargets().some(active => normalizeTarget(active.targetId) === normalizeTarget(target)))) {
      $("classroom-screen-preview-status").textContent = "座位关联已变化，请刷新课堂状态后重新查看。";
      return;
    }
    classroomScreenPreviewSettingsPending = false;
    await startClassroomScreenPreview();
  } finally {
    classroomScreenPreviewSettingsPending = false;
    renderMonitorTargets();
  }
}

function classroomScreenTargets() {
  return [...activeClassroomScreenTargets].sort((left, right) => left.seatNumber - right.seatNumber);
}

function sameClassroomScreenTargets(left, right) {
  if (left.length !== right.length) return false;
  const rightById = new Map(right.map(target => [normalizeTarget(target.targetId), target]));
  return left.every(previous => {
    const current = rightById.get(normalizeTarget(previous.targetId));
    return current && previous.seatNumber === current.seatNumber &&
      previous.row === current.row && previous.column === current.column &&
      previous.studentName === current.studentName && previous.computerName === current.computerName;
  });
}

async function openSingleTargetScreenPreview(targetId) {
  if (singleTargetPreviewPending || classroomScreenPreviewSettingsPending ||
      classroomScreenPreviewStartController || !isClassroomMonitorVisible()) return;
  const target = classroomScreenTargets().find(item => normalizeTarget(item.targetId) === normalizeTarget(targetId));
  if (!target) {
    toast("学生电脑信息已变化，请刷新课堂状态后重试。");
    return;
  }
  const stopCurrentView = classroomScreenPreviewLease && classroomScreenPreviewMode === "single-target" &&
    normalizeTarget(classroomScreenPreviewTargets[0]) === normalizeTarget(target.targetId);
  const resumeScreenPreview = Boolean(classroomScreenPreviewLease && classroomScreenPreviewMode === "screenshot");
  const resumeSessionId = activeClassroomSessionId;
  const startingTargets = classroomScreenTargets();
  const startController = new AbortController();
  singleTargetPreviewStartController = startController;
  singleTargetPreviewPending = true;
  singleTargetPreviewId = target.targetId;
  renderMonitorTargets();
  let failed = false;
  try {
    if (stopCurrentView) {
      await stopClassroomScreenPreview(true);
      return;
    }
    if (classroomScreenPreviewLease) await stopClassroomScreenPreview(false);
    if (startController.signal.aborted || !isClassroomMonitorVisible() ||
        activeClassroomSessionId !== resumeSessionId ||
        !sameClassroomScreenTargets(startingTargets, classroomScreenTargets())) return;

    const result = await api("/api/classroom/screen-preview/start", {
      method: "POST", body: {
        targets: [target.targetId],
        quality: classroomScreenPreviewQuality,
        refreshIntervalSeconds: 3
      }, signal: startController.signal
    });
    const targets = Array.isArray(result?.targets) ? result.targets : [];
    const currentTargets = classroomScreenTargets();
    const currentTarget = currentTargets.find(item =>
      normalizeTarget(item.targetId) === normalizeTarget(target.targetId));
    const contextStillCurrent = !startController.signal.aborted && isClassroomMonitorVisible() &&
      activeClassroomSessionId === resumeSessionId &&
      sameClassroomScreenTargets(startingTargets, currentTargets);
    if (targets.length !== 1 || normalizeTarget(targets[0]) !== normalizeTarget(target.targetId) ||
        result.sessionId !== resumeSessionId || typeof result?.leaseId !== "string" ||
        result.totalTargets !== startingTargets.length || result.quality !== classroomScreenPreviewQuality ||
        result.refreshIntervalSeconds !== 3 ||
        !contextStillCurrent || !currentTarget) {
      if (typeof result?.leaseId === "string") {
        try {
          await api("/api/classroom/screen-preview/stop", {
            method: "POST", body: { leaseId: result.leaseId }, keepalive: true
          });
        } catch { /* The server-side lease expires if the response cannot be reconciled. */ }
      }
      if (!contextStillCurrent) return;
      throw new Error("学生电脑信息已变化，请刷新课堂状态后重试。");
    }
    classroomScreenPreviewLease = { id: result.leaseId, sessionId: result.sessionId };
    classroomScreenPreviewMode = "single-target";
    classroomScreenPreviewRefreshSeconds = result.refreshIntervalSeconds;
    classroomScreenPreviewTargets = [target.targetId];
    renderClassroomScreenPreviewTiles(classroomScreenPreviewTargets);
    const message = `已打开 ${screenTargetDisplayName(currentTarget)} 的单台实时画面。`;
    $("classroom-screen-preview-status").textContent = message;
    $("monitor-preview-guidance").textContent = "手机画面约每 3 秒更新；停止查看或离开此页后会自动停止。";
    observeClassroomScreenPreviewTiles();
    toast(message);
  } catch (error) {
    if (error.name === "AbortError") return;
    failed = true;
    const detail = screenPreviewErrorMessage(error, "请确认课堂仍在进行后重试。");
    const message = `${screenTargetDisplayName(target)}：${detail}`;
    $("classroom-screen-preview-status").textContent = message;
    toast(message);
  } finally {
    if (singleTargetPreviewStartController === startController)
      singleTargetPreviewStartController = null;
    singleTargetPreviewPending = false;
    singleTargetPreviewId = null;
    renderMonitorTargets();
  }
  if (failed && resumeScreenPreview && activeClassroomSessionId === resumeSessionId &&
      isClassroomMonitorVisible()) {
    await startClassroomScreenPreview();
    $("classroom-screen-preview-status").textContent = classroomScreenPreviewLease
      ? "实时画面暂时无法打开，已恢复全部座位的普通屏幕查看。"
      : "实时画面暂时无法打开，普通屏幕查看也未能恢复；请重新开始查看。";
  }
}

function renderMonitorTargets() {
  const active = Boolean(activeClassroomSessionId && activeClassroomTargets.length);
  const screenTargets = classroomScreenTargets();
  const settingsLocked = !active || classroomScreenPreviewSettingsPending ||
    Boolean(classroomScreenPreviewStartController) || singleTargetPreviewPending ||
    classroomScreenPreviewMode === "single-target" || classroomScreenPreviewPaused;
  $("monitor-preview-quality").value = classroomScreenPreviewQuality;
  $("monitor-preview-refresh").value = String(classroomScreenPreviewRefreshSeconds);
  $("monitor-preview-quality").disabled = settingsLocked;
  $("monitor-preview-refresh").disabled = settingsLocked;
  $("monitor-preview-settings-guidance").textContent = classroomScreenPreviewMode === "single-target"
    ? "单台画面每 3 秒更新；停止后可调整设置。"
    : "画面越清楚，用的流量越多。调整后会重新读取全部座位的画面。";
  $("monitor-empty").classList.toggle("hidden", active);
  $("monitor-content").classList.toggle("hidden", !active);
  $("monitor-active-count").textContent = `${screenTargets.length} 个座位`;
  const guidance = $("monitor-preview-guidance");
  if (active && screenTargets.length === 0) {
    guidance.textContent = "请先在教师电脑为学生座位选好学生电脑，再回来查看。";
  } else if (classroomScreenPreviewPaused && classroomScreenPreviewLease) {
    guidance.textContent = "屏幕查看已暂停；最近画面保留在卡片中，继续时会重新打开巡视授权。";
  } else if (classroomScreenPreviewMode === "single-target" && classroomScreenPreviewLease) {
    guidance.textContent = "正在手机上查看单台电脑；画面约每 3 秒更新。离开页面后会自动停止。";
  } else if (classroomScreenPreviewLease) {
    guidance.textContent = `正在查看 ${classroomScreenPreviewTargets.length} 个座位；手机只更新当前可见的画面。离开页面后会自动停止。`;
  } else {
    guidance.textContent = "开始后，手机只更新当前可见的画面。离开页面后会自动停止。";
  }
  for (const liveButton of $("classroom-screen-preview-grid").querySelectorAll(".monitor-tile-live-button")) {
    const targetId = liveButton.dataset.target;
    const isLiveTarget = classroomScreenPreviewMode === "single-target" &&
      normalizeTarget(classroomScreenPreviewTargets[0]) === normalizeTarget(targetId);
    liveButton.disabled = classroomScreenPreviewSettingsPending ||
      Boolean(classroomScreenPreviewStartController) || singleTargetPreviewPending;
    liveButton.textContent = singleTargetPreviewPending && normalizeTarget(singleTargetPreviewId) === normalizeTarget(targetId)
      ? "正在打开…" : isLiveTarget ? "停止实时画面" : "看实时画面";
  }
  const toggle = $("monitor-preview-toggle");
  const startPending = Boolean(classroomScreenPreviewStartController);
  toggle.disabled = classroomScreenPreviewSettingsPending || startPending || singleTargetPreviewPending ||
    (!classroomScreenPreviewLease && (!active || screenTargets.length === 0));
  toggle.textContent = startPending ? "正在连接…" : classroomScreenPreviewLease ? "停止查看" : "开始查看全部";
  toggle.setAttribute("aria-expanded", String(Boolean(classroomScreenPreviewLease)));
  const pause = $("monitor-preview-pause");
  pause.classList.toggle("hidden", !classroomScreenPreviewLease);
  pause.disabled = startPending || classroomScreenPreviewSettingsPending || singleTargetPreviewPending;
  pause.textContent = classroomScreenPreviewPaused ? "继续查看" : "暂停查看";
  pause.setAttribute("aria-pressed", String(classroomScreenPreviewPaused));
  pause.setAttribute("aria-label", classroomScreenPreviewPaused ? "继续查看学生屏幕" : "暂停查看学生屏幕");
  $("monitor-preview-actions").classList.toggle("is-active", Boolean(classroomScreenPreviewLease));
}

function renderClassroomScreenPreviewTiles(targets) {
  const grid = $("classroom-screen-preview-grid");
  grid.replaceChildren();
  grid.classList.toggle("is-live", classroomScreenPreviewMode === "single-target");
  classroomScreenPreviewVisibleTargets.clear();
  for (const targetId of targets) {
    const target = classroomScreenTargets().find(item => normalizeTarget(item.targetId) === normalizeTarget(targetId));
    if (!target) continue;
    const tile = document.createElement("article");
    tile.className = "classroom-screen-preview-tile";
    tile.dataset.target = targetId;

    const heading = document.createElement("div");
    heading.className = "classroom-screen-preview-tile-heading";
    const name = document.createElement("strong");
    name.textContent = screenTargetDisplayName(target);
    name.title = name.textContent;
    const timestamp = document.createElement("small");
    timestamp.textContent = "等待画面";
    heading.append(name, timestamp);
    const position = document.createElement("p");
    position.className = "classroom-screen-preview-tile-position";
    position.textContent = screenTargetSeatDescription(target);

    const image = document.createElement("img");
    image.className = "classroom-screen-preview-image";
    image.alt = `${screenTargetDisplayName(target)} 的屏幕画面`;
    image.width = 320;
    image.height = 180;
    image.loading = "lazy";
    image.decoding = "async";

    const frameStatus = document.createElement("p");
    frameStatus.className = "classroom-screen-preview-frame-status";
    frameStatus.textContent = "开始查看后自动加载画面。";
    const retryButton = document.createElement("button");
    retryButton.className = "secondary-button compact-button monitor-tile-retry-button hidden";
    retryButton.type = "button";
    retryButton.textContent = "重试读取";
    retryButton.setAttribute("aria-label", `重试读取${screenTargetDisplayName(target)}的画面`);
    retryButton.addEventListener("click", () => void refreshClassroomScreenPreviewFrame(tile));
    const liveButton = document.createElement("button");
    liveButton.className = "secondary-button compact-button monitor-tile-live-button";
    liveButton.type = "button";
    liveButton.dataset.target = target.targetId;
    const isLiveTarget = classroomScreenPreviewMode === "single-target" &&
      normalizeTarget(classroomScreenPreviewTargets[0]) === normalizeTarget(target.targetId);
    liveButton.textContent = singleTargetPreviewPending &&
      normalizeTarget(singleTargetPreviewId) === normalizeTarget(target.targetId)
      ? "正在打开…" : isLiveTarget ? "停止实时画面" : "看实时画面";
    liveButton.disabled = singleTargetPreviewPending;
    liveButton.setAttribute("aria-label", `${isLiveTarget ? "停止" : "打开"}${screenTargetDisplayName(target)}的实时画面`);
    liveButton.addEventListener("click", () => void openSingleTargetScreenPreview(target.targetId));
    tile.append(heading, position, image, frameStatus, retryButton, liveButton);
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
  if (frameStatus) frameStatus.textContent = "查看时自动更新画面。";
  tile.querySelector(".monitor-tile-retry-button")?.classList.add("hidden");
  const timestamp = tile.querySelector("small");
  if (timestamp) timestamp.textContent = "等待画面";
}

async function refreshClassroomScreenPreviewFrame(tile) {
  const target = tile.dataset.target;
  const lease = classroomScreenPreviewLease;
  if (!target || !lease || classroomScreenPreviewPaused ||
      !classroomScreenPreviewVisibleTargets.has(target) || document.hidden ||
      classroomScreenPreviewControllers.has(target)) return;
  const controller = new AbortController();
  classroomScreenPreviewControllers.set(target, controller);
  const frameStatus = tile.querySelector(".classroom-screen-preview-frame-status");
  const image = tile.querySelector("img");
  const retryButton = tile.querySelector(".monitor-tile-retry-button");
  let timedOut = false;
  let capturedSuccessfully = false;
  const requestTimeout = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, 50_000);
  if (retryButton) retryButton.classList.add("hidden");
  if (frameStatus) frameStatus.textContent = image?.hasAttribute("src")
    ? "正在更新画面…" : "正在读取画面…";
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
    if (image) image.src = objectUrl;
    const captured = Date.parse(frame.capturedUtc || "");
    const time = Number.isFinite(captured) ? formatTimeOfDay(new Date(captured)) : "刚刚";
    const timestamp = tile.querySelector("small");
    if (timestamp) timestamp.textContent = time;
    if (frameStatus) frameStatus.textContent = frame.cached ? "显示上次画面" : "画面已更新";
    capturedSuccessfully = true;
  } catch (error) {
    if (timedOut) {
      if (frameStatus) frameStatus.textContent = image?.hasAttribute("src")
        ? "画面更新超时，保留上次画面。请检查连接后重试。"
        : "读取画面超时，请检查教师机与学生电脑的连接后重试。";
      retryButton?.classList.remove("hidden");
    } else if (error.name !== "AbortError") {
      const detail = screenPreviewErrorMessage(error, "请检查教师机与学生电脑的连接后重试。");
      if (frameStatus) frameStatus.textContent = image?.hasAttribute("src")
        ? `画面更新失败，保留上次画面。${detail}`
        : detail;
      retryButton?.classList.remove("hidden");
    }
    if (error.status === 401 || error.status === 403) void stopClassroomScreenPreview(false);
  } finally {
    clearTimeout(requestTimeout);
    if (classroomScreenPreviewControllers.get(target) === controller)
      classroomScreenPreviewControllers.delete(target);
    if (classroomScreenPreviewLease?.id === lease.id && !classroomScreenPreviewPaused &&
        capturedSuccessfully && classroomScreenPreviewVisibleTargets.has(target) && !document.hidden) {
      const refreshDelay = classroomScreenPreviewRefreshSeconds * 1000;
      const timer = setTimeout(() => void refreshClassroomScreenPreviewFrame(tile), refreshDelay);
      classroomScreenPreviewTimers.set(target, timer);
    }
  }
}

async function fetchClassroomScreenPreviewFrame(target, leaseId, signal) {
  const contentType = "image/jpeg";
  const headers = new Headers({ Accept: contentType });
  headers.set("Authorization", "Bearer " + accessToken);
  headers.set("X-Veyon-Request-Nonce", createRequestNonce());
  headers.set("X-Veyon-Request-Timestamp", new Date(Date.now() + serverClockOffsetMs).toISOString());
  headers.set("X-Veyon-Screen-Preview-Lease", leaseId.replaceAll("-", ""));
  const endpoint = "/api/classroom/screen-preview/";
  const response = await fetch(endpoint + encodeURIComponent(target), {
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
  if (response.headers.get("Content-Type")?.split(";")[0] !== contentType)
    throw new Error("屏幕响应格式无效。");
  const blob = await response.blob();
  const maximumBytes = 1024 * 1024;
  if (blob.size === 0 || blob.size > maximumBytes) throw new Error("屏幕画面大小无效。");
  return {
    blob,
    capturedUtc: response.headers.get("X-Captured-Utc"),
    cached: response.headers.get("X-Screen-Frame") === "cached"
  };
}

async function stopClassroomScreenPreview(showStatus) {
  const lease = classroomScreenPreviewLease;
  const stoppedSingleTarget = classroomScreenPreviewMode === "single-target";
  classroomScreenPreviewLease = null;
  classroomScreenPreviewPaused = false;
  classroomScreenPreviewMode = "screenshot";
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
  grid.classList.remove("is-live");
  const button = $("monitor-preview-toggle");
  button.textContent = "开始查看全部";
  button.setAttribute("aria-expanded", "false");
  $("monitor-preview-guidance").textContent = "停止查看或离开此页后，画面会自动停止更新。";
  renderMonitorTargets();
  if (showStatus) {
    const message = stoppedSingleTarget ? "实时查看已停止。" : "屏幕查看已停止。";
    $("classroom-screen-preview-status").textContent = message;
  }
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

function customWebsitePolicyInput() {
  const mode = $("custom-website-mode").value;
  const blockedDomains = readDomainList("custom-website-blocked");
  const allowedDomains = readDomainList("custom-website-allowed");
  const dateValue = $("custom-website-deadline-date").value;
  const timeValue = $("custom-website-deadline-time").value;
  const deadlineValue = dateValue && timeValue ? `${dateValue}T${timeValue}` : "";
  const deadline = deadlineValue ? new Date(deadlineValue) : null;
  const remaining = deadline ? deadline.getTime() - Date.now() : NaN;
  const validDeadline = Number.isFinite(remaining) && remaining > 0 && remaining <= 24 * 60 * 60 * 1000;
  const hasRequiredDomains = mode === "allowlist" ? allowedDomains.length > 0 :
    mode === "blocklist" ? blockedDomains.length > 0 : blockedDomains.length > 0 || allowedDomains.length > 0;
  const domainValidationError = mode === "allowlist"
    ? websiteDomainValidationError(allowedDomains, "允许的网站名单")
    : mode === "blocklist"
      ? websiteDomainValidationError(blockedDomains, "要阻止的网站名单")
      : websiteDomainValidationError(blockedDomains, "要阻止的网站名单") ||
        websiteDomainValidationError(allowedDomains, "允许的网站名单");
  return {
    mode,
    blockedDomains,
    allowedDomains,
    hasRequiredDomains,
    domainValidationError,
    expiresUtc: deadline && Number.isFinite(deadline.getTime()) ? deadline.toISOString() : null,
    deadlineText: deadline && Number.isFinite(deadline.getTime()) ? formatDateTime(deadline.toISOString()) : "未选择",
    validDeadline,
    valid: hasRequiredDomains && !domainValidationError && validDeadline
  };
}

function websiteDomainValidationError(domains, listName) {
  for (const entry of domains) {
    try {
      const hasScheme = entry.includes("://");
      if (!hasScheme && /[*/\\:?#%]/u.test(entry)) throw new Error("invalid domain characters");
      const address = new URL(hasScheme ? entry : `https://${entry}`);
      if (!["http:", "https:"].includes(address.protocol) || address.username || address.password ||
        address.port || address.pathname !== "/" || address.search || address.hash)
        throw new Error("invalid website address");
      const hostname = address.hostname.toLowerCase().replace(/\.+$/u, "");
      const labels = hostname.split(".");
      const isIpv4 = /^(?:\d{1,3}\.){3}\d{1,3}$/u.test(hostname);
      if (!hostname || hostname.length > 253 || hostname.startsWith("[") || hostname.includes(":") || isIpv4 ||
        labels.some(label => !/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/u.test(label)))
        throw new Error("invalid domain labels");
    } catch {
      return `${listName}中的“${entry}”格式不正确；请输入域名，不能包含通配符、路径、端口或 IP 地址。`;
    }
  }
  return "";
}

function updateCustomWebsiteModeGuidance(targets = selectedTargets(), websiteStatusReady = undefined) {
  const guidance = $("custom-website-mode-guidance");
  const input = customWebsitePolicyInput();
  const mode = input.mode;
  const action = $("apply-custom-website-policy");
  if (action) action.textContent = mode === "allowlist" ? "应用白名单限制" :
    mode === "combined" ? "应用阻止名单和例外" : "应用阻止名单";
  if (guidance) {
    guidance.textContent = mode === "allowlist"
      ? "只允许“允许的网站”名单中的域名及其子域名；其他网站都会被阻止。阻止名单会保留，切换方式后可继续使用。"
      : mode === "combined"
        ? "先阻止名单中的网站，再放行允许名单中的例外；名单外网站仍可访问。"
        : "只阻止“要阻止的网站”名单中的网站；其他网站仍可访问。允许名单会保留，切换方式后可继续使用。";
  }
  const nextStep = $("custom-website-next-step-guidance");
  if (!nextStep) return;
  if (policyOperationInFlight) {
    nextStep.textContent = "下一步：等待网站限制处理完成，再查看下方逐台结果。";
  } else if (!websiteListsAvailable) {
    nextStep.textContent = "下一步：先读取教师电脑上当前校区的网站名单。";
  } else if (!targets.length) {
    nextStep.textContent = "下一步：选择至少一台学生电脑，再核对名单和截止时间。";
  } else if (!input.hasRequiredDomains) {
    nextStep.textContent = mode === "allowlist"
      ? "下一步：打开“编辑阻止与允许的网站”，至少填写一个允许访问的域名。"
      : mode === "combined"
        ? "下一步：打开“编辑阻止与允许的网站”，填写要阻止的网站或允许例外。"
        : "下一步：打开“编辑阻止与允许的网站”，至少填写一个要阻止的域名。";
  } else if (input.domainValidationError) {
    nextStep.textContent = `下一步：打开“编辑阻止与允许的网站”并修正域名。${input.domainValidationError}`;
  } else if (!input.validDeadline) {
    nextStep.textContent = "下一步：设置未来 24 小时内的限制截止时间。";
  } else if (!(websiteStatusReady ?? hasRequiredPolicyStatus(targets))) {
    nextStep.textContent = "下一步：刷新所选学生电脑状态，再核对规则并应用。";
  } else {
    nextStep.textContent = "下一步：核对所选电脑和截止时间，再点击下方应用按钮。";
  }
}

function readDomainList(id) {
  return $(id).value.split(/[\r\n]+/).map(value => value.trim()).filter(Boolean);
}

function initializeCustomWebsiteDeadline() {
  const dateInput = $("custom-website-deadline-date");
  const timeInput = $("custom-website-deadline-time");
  if (!dateInput || !timeInput || (dateInput.value && timeInput.value)) return;
  const local = new Date(Date.now() + 60 * 60 * 1000);
  local.setSeconds(0, 0);
  const pad = value => String(value).padStart(2, "0");
  dateInput.value = `${local.getFullYear()}-${pad(local.getMonth() + 1)}-${pad(local.getDate())}`;
  timeInput.value = `${pad(local.getHours())}:${pad(local.getMinutes())}`;
}

function websiteListSnapshot() {
  return JSON.stringify({
    mode: $("custom-website-mode").value,
    blockedDomains: $("custom-website-blocked").value,
    allowedDomains: $("custom-website-allowed").value
  });
}

function websiteListsDirty() {
  return websiteListsAvailable && websiteListSnapshot() !== websiteListsSavedSnapshot;
}

function updateWebsiteListSaveButton() {
  const button = $("save-custom-website-lists");
  if (!button) return;
  button.disabled = !websiteListsAvailable || !websiteListsDirty() || websiteListsSaving;
  button.textContent = websiteListsSaving ? "正在保存…" : websiteListsDirty()
    ? "保存名单到教师电脑 · 有未保存修改" : "保存名单到教师电脑";
}

function selectedTargets() {
  return Array.from(roomList.querySelectorAll("input[data-target]:checked:not(:disabled)"))
    .map(input => input.dataset.target)
    .filter(Boolean);
}

function normalizeTarget(target) {
  return String(target || "").trim().toLowerCase();
}

function isIpAddress(target) {
  const value = String(target || "").trim();
  return /^(?:\d{1,3}\.){3}\d{1,3}$/.test(value) ||
    (value.includes(":") && /^[0-9a-f:]+$/i.test(value));
}

function targetDisplayName(target) {
  const normalized = normalizeTarget(target);
  for (const room of rooms) {
    for (const [host, name] of Object.entries(room.targetDisplayNames || {})) {
      const displayName = typeof name === "string" ? name.trim() : "";
      if (normalizeTarget(host) === normalized && displayName && !isIpAddress(displayName))
        return displayName;
    }
  }
  return target ? `未填写姓名 · ${target}` : "未填写姓名";
}

function classroomTargetDisplayName(target) {
  const seat = activeClassroomSeatLocations.get(normalizeTarget(target));
  return seat?.studentName || targetDisplayName(target);
}

function screenTargetDisplayName(target) {
  const studentName = typeof target?.studentName === "string" ? target.studentName.trim() : "";
  if (studentName && !isIpAddress(studentName)) return studentName;
  return "未填写姓名";
}

function screenTargetSeatDescription(target) {
  return `座位 ${target.seatNumber} · 第 ${target.row} 排第 ${target.column} 列`;
}

function classroomSeatDescription(seat) {
  if (!seat) return "学生电脑";
  return Number.isSafeInteger(seat.seatNumber)
    ? `座位 ${seat.seatNumber} · 第 ${seat.row} 排第 ${seat.column} 列`
    : `第 ${seat.row} 排第 ${seat.column} 列`;
}

function roomTargetDisplayName(room, target) {
  for (const [host, name] of Object.entries(room?.targetDisplayNames || {})) {
    const displayName = typeof name === "string" ? name.trim() : "";
    if (normalizeTarget(host) === normalizeTarget(target) && displayName && !isIpAddress(displayName))
      return displayName;
  }
  return target ? `未填写姓名 · ${target}` : "未填写姓名";
}

function duplicateDirectoryTargets() {
  const locationsByTarget = new Map();
  rooms.forEach((room, roomIndex) => {
    for (const target of room.targets || []) {
      const key = normalizeTarget(target);
      if (!key) continue;
      if (!locationsByTarget.has(key)) locationsByTarget.set(key, new Set());
      locationsByTarget.get(key).add(roomIndex);
    }
  });
  return new Set(Array.from(locationsByTarget)
    .filter(([, locationIndexes]) => locationIndexes.size > 1)
    .map(([target]) => target));
}

function duplicateDirectoryRoomIndexes() {
  const roomIndexesByName = new Map();
  rooms.forEach((room, index) => {
    const key = String(room.name || "未命名地点").trim().toLowerCase();
    if (!roomIndexesByName.has(key)) roomIndexesByName.set(key, []);
    roomIndexesByName.get(key).push(index);
  });
  return new Set(Array.from(roomIndexesByName.values())
    .filter(indexes => indexes.length > 1)
    .flat());
}

function renderComputerDirectory() {
  const selector = $("directory-room-select");
  const picker = $("directory-room-picker");
  const roomSummary = $("directory-room-summary");
  const grid = $("computer-directory-grid");
  if (!selector || !picker || !roomSummary || !grid) return;
  selector.replaceChildren();
  grid.replaceChildren();
  if (!rooms.length) {
    picker.classList.add("hidden");
    roomSummary.classList.remove("hidden");
    roomSummary.textContent = "未配置教室；请在教师电脑的“地点与学生名单”中添加地点，再重新读取电脑名单。";
    selector.disabled = true;
    if (selectedComputerTarget) {
      selectedComputerTarget = null;
      if (policyPageMode === "single") setPolicyPageMode("directory");
    }
    const option = document.createElement("option");
    option.value = "";
    option.textContent = "没有可用地点";
    selector.append(option);
    const empty = document.createElement("p");
    empty.className = "computer-directory-empty";
    empty.textContent = "当前没有可用地点，请在教师电脑确认电脑名单后刷新。";
    grid.append(empty);
    return;
  }

  const duplicateRoomIndexes = duplicateDirectoryRoomIndexes();
  selector.disabled = false;
  if (rooms.length === 1) {
    picker.classList.add("hidden");
    roomSummary.classList.remove("hidden");
  } else {
    picker.classList.remove("hidden");
    roomSummary.classList.add("hidden");
  }
  rooms.forEach((room, index) => {
    const option = document.createElement("option");
    option.value = String(index);
    option.textContent = duplicateRoomIndexes.has(index)
      ? `${room.name || "未命名地点"} · 地点名称重复`
      : `${room.name || "未命名地点"} · ${(room.targets || []).length} 台`;
    option.disabled = duplicateRoomIndexes.has(index);
    selector.append(option);
  });
  const preferredIndex = selectedDirectoryRoomName
    ? rooms.findIndex(room => (room.name || "未命名地点") === selectedDirectoryRoomName)
    : -1;
  const selectedRoomIndex = preferredIndex >= 0 && !duplicateRoomIndexes.has(preferredIndex)
    ? preferredIndex
    : rooms.findIndex((_, index) => !duplicateRoomIndexes.has(index));
  if (selectedRoomIndex < 0) {
    selector.disabled = true;
    selector.selectedIndex = -1;
    roomSummary.classList.remove("hidden");
    roomSummary.textContent = "地点名称有重复，无法确认要操作哪个地点；请先在教师电脑修正地点名称。";
    const warning = document.createElement("p");
    warning.className = "computer-directory-warning";
    warning.textContent = roomSummary.textContent;
    grid.append(warning);
    return;
  }
  selector.value = String(selectedRoomIndex);
  const room = rooms[selectedRoomIndex];
  selectedDirectoryRoomName = room.name || "未命名地点";
  rememberDirectoryRoomName(selectedDirectoryRoomName);
  if (selectedComputerTarget && !(room.targets || []).some(target =>
      normalizeTarget(target) === normalizeTarget(selectedComputerTarget))) {
    selectedComputerTarget = null;
    if (policyPageMode === "single") setPolicyPageMode("directory");
  }
  if (rooms.length === 1)
    roomSummary.textContent = `当前教室：${selectedDirectoryRoomName}`;
  if (duplicateRoomIndexes.size) {
    const warning = document.createElement("p");
    warning.className = "computer-directory-warning";
    warning.textContent = "部分地点名称重复，已停用这些地点；请在教师电脑修改名称后重新读取。";
    grid.append(warning);
  }
  const duplicates = duplicateDirectoryTargets();
  const allTargets = Array.isArray(room.targets) ? room.targets : [];
  const query = ($("directory-search")?.value || "").trim().toLocaleLowerCase();
  const targets = allTargets.filter(target => !query || roomTargetDisplayName(room, target).toLocaleLowerCase().includes(query));
  $("directory-count").textContent = query ? `${targets.length} / ${allTargets.length} 台电脑` : `${allTargets.length} 台电脑`;
  if (!targets.length) {
    const empty = document.createElement("p");
    empty.className = "computer-directory-empty";
    empty.textContent = query ? "没有找到匹配的学生或电脑，试试其他姓名、电脑名或 IP。" : "这个地点没有电脑。";
    grid.append(empty);
    return;
  }
  for (const target of targets) {
    const duplicate = duplicates.has(normalizeTarget(target));
    const studentName = roomTargetDisplayName(room, target);
    const missingName = studentName.startsWith("未填写姓名");
    const card = document.createElement("button");
    card.type = "button";
    card.className = "computer-directory-card";
    card.disabled = duplicate;
    card.dataset.target = target;
    if (duplicate) card.classList.add("is-disabled");
    card.setAttribute("aria-label", duplicate
      ? `${studentName}，名单中有重复记录，暂不可操作`
      : missingName ? `${studentName}，按电脑目标查看限制。` : `查看 ${studentName} 的限制`);
    const iconSlot = document.createElement("span");
    iconSlot.className = "student-computer-icon";
    iconSlot.setAttribute("aria-hidden", "true");
    const name = document.createElement("strong");
    name.textContent = studentName;
    const hint = document.createElement("small");
    hint.textContent = duplicate ? "这台电脑出现在多个地点，请先修正电脑名单。" :
      missingName ? "未填写姓名 · 可按电脑名/IP 查看" : "查看网站限制";
    const receipt = document.createElement("span");
    const known = latestStatusByTarget.get(normalizeTarget(target));
    const age = Date.now() - Date.parse(known?.collectedUtc || "");
    const current = known?.online && Number.isFinite(age) && age >= -120000 && age <= 120000;
    receipt.className = "state " + (known?.needsReview ? "warn" : current ? "good" : "neutral");
    receipt.textContent = known?.needsReview ? "需核对" : current ? "已回执" : known ? "待确认" : "等待读取";
    card.append(iconSlot, name, hint, receipt);
    if (!duplicate) card.addEventListener("click", () => openSingleComputer(target, room));
    grid.append(card);
  }
  if (duplicates.size) {
    const warning = document.createElement("p");
    warning.className = "computer-directory-warning";
    warning.textContent = `${duplicates.size} 台电脑出现在多个地点；单机限制已停用，请先修正电脑名单。`;
    grid.append(warning);
  }
}

function setPolicyPageMode(mode) {
  policyPageMode = mode;
  $("computer-directory-panel").classList.toggle("hidden", mode !== "directory");
  $("student-detail-header").classList.toggle("hidden", mode !== "single");
  $("bulk-policy-header").classList.toggle("hidden", mode !== "bulk");
  $("student-policy-controls").classList.toggle("hidden", mode === "directory");
  if (mode === "single") $("target-picker").open = false;
  if (mode === "bulk") $("target-picker").open = true;
  updateSelectionCount();
}

function openSingleComputer(target, room) {
  const normalized = normalizeTarget(target);
  if (duplicateDirectoryTargets().has(normalized)) {
    toast("这台电脑出现在多个地点，请先修正电脑名单。");
    return;
  }
  const inputs = Array.from(roomList.querySelectorAll("input[data-target]"));
  const match = inputs.find(input => normalizeTarget(input.dataset.target) === normalized);
  if (!match) {
    toast("电脑已不在当前名单中，请刷新后重试。");
    return;
  }
  for (const input of inputs) input.checked = input === match;
  computerDirectoryScrollY = window.scrollY;
  selectedComputerTarget = target;
  latestStatusByTarget.delete(normalized);
  $("student-detail-name").textContent = roomTargetDisplayName(room, target);
  $("student-detail-hint").textContent = "这里只操作这台电脑；更改前会显示当前状态并让你确认。";
  setPolicyPageMode("single");
  void refreshStatusIfSelected();
  $("student-detail-header").scrollIntoView({ behavior: "smooth", block: "start" });
}

function returnToComputerDirectory() {
  setPolicyPageMode("directory");
  if (computerDirectoryScrollY !== null)
    requestAnimationFrame(() => window.scrollTo(0, computerDirectoryScrollY));
}

function updateSelectionCount() {
  const targets = selectedTargets();
  const count = targets.length;
  const isClassroomDefault = activeClassroomTargets.length > 0 && sameTargets(targets, activeClassroomTargets);
  const websiteStatusReady = hasRequiredPolicyStatus(targets);
  $("selection-count").textContent = policyPageMode === "single" && targets.length === 1
    ? "单台电脑 · " + targetDisplayName(targets[0])
    : isClassroomDefault
    ? "当前范围 · " + count + " 台学生电脑"
    : count ? "已选择 " + count + " 台电脑" : "未选择电脑";
  const scopeStatus = $("target-scope-status");
  if (policyPageMode === "single" && targets.length === 1) {
    scopeStatus.textContent = "本页限制仅作用于 " + targetDisplayName(targets[0]) + "。";
  } else if (classroomTargetDefaultState === "mismatch") {
    scopeStatus.textContent = count
      ? "学生电脑未能与名单完整匹配；请核对选择范围。"
      : "学生电脑未能与名单完整匹配，未自动选择。请展开确认范围。";
  } else if (classroomTargetDefaultState === "unavailable") {
    scopeStatus.textContent = count
      ? "课堂模式尚未就绪，请在教师端核对教室范围。"
      : "请先选择学生电脑范围，再查看或切换限制。";
  } else if (count && !websiteStatusReady) {
    scopeStatus.textContent = policyStatusRequirementMessage(targets);
  } else {
    scopeStatus.textContent = "";
  }
  scopeStatus.classList.toggle("hidden", !scopeStatus.textContent);
  $("refresh-status").disabled = !count;
  const operationDisabled = policyOperationInFlight;
  $("apply-custom-website-policy").disabled = !count || !websiteStatusReady ||
    !customWebsitePolicyInput().valid || operationDisabled || !websiteListsAvailable;
  $("disable-custom-website-policy").disabled = !count || !websiteStatusReady || operationDisabled;
  updateCustomWebsiteModeGuidance(targets, websiteStatusReady);
  updateSavedPolicyProfileControls(targets);
}

function policyStatusRequirementMessage(targets) {
  const statuses = targets.map(target => latestStatusByTarget.get(normalizeTarget(target)));
  if (statuses.some(status => status?.needsReview))
    return "学生 Agent 身份或签名需要核对。请先在教师端“设备健康”确认身份，再刷新手机状态。";
  if (statuses.some(status => !status?.online))
    return "至少一台学生电脑没有回应。请检查学生 Agent 和校园网，再刷新状态。";
  if (statuses.some(status => !status?.supportsPolicyCompareAndSet))
    return "至少一台学生 Agent 版本过旧或状态不完整。请更新学生端并重新读取状态。";
  if (statuses.some(status => !Number.isSafeInteger(status?.websiteRevision) || status.websiteRevision < 0))
    return "没有读到网站策略版本。请刷新所选电脑状态后重试。";
  return "请刷新所选电脑状态；状态未确认时，无法安全更改限制。";
}

function hasRequiredPolicyStatus(targets) {
  if (!targets.length) return false;
  const duplicates = duplicateDirectoryTargets();
  if (targets.some(target => duplicates.has(normalizeTarget(target))))
    return false;
  return targets.every(target => {
    const status = latestStatusByTarget.get(normalizeTarget(target));
    if (!status || !status.online || status.needsReview || !status.supportsPolicyCompareAndSet) return false;
    return status.websiteRevision != null;
  });
}

function expectedRevisionFields(targets) {
  const revisions = Object.create(null);
  for (const target of targets) {
    const revision = latestStatusByTarget.get(normalizeTarget(target))?.websiteRevision;
    if (!Number.isSafeInteger(revision) || revision < 0) return null;
    revisions[target] = revision;
  }
  return { expectedWebsiteRevisions: revisions };
}

function selectedSavedPolicyProfile() {
  const id = $("saved-policy-profile")?.value || "";
  return savedPolicyProfiles.find(profile => profile.id === id) || null;
}

function mobilePolicyKindLabel(kind) {
  return kind === "website" ? "网站" : kind === "application" ? "应用" :
    kind === "websiteApplication" ? "网站和应用" : kind === "system" ? "长期系统" : "未知类型";
}

function mobilePolicyModeLabel(mode, kind) {
  const label = value => {
    const normalized = String(value || "").toLowerCase();
    return normalized === "blocklist" ? "阻止名单" : normalized === "allowlist" ? "允许名单" :
      normalized === "combined" ? "阻止名单和例外" : normalized === "audit" ? "先检查影响" :
        normalized === "enforce" ? "启用限制" : value || "模式未知";
  };
  if (kind === "system") return "长期生效";
  const parts = String(mode || "").split("|");
  if (kind === "websiteApplication" && parts.length === 2)
    return `${label(parts[0])} · ${label(parts[1])}`;
  return label(parts[0]);
}

function savedPolicyProfileDescription(profile) {
  if (!profile) return "选择教师端保存的策略预设。";
  const duration = profile.lifetimeMinutes === 0
    ? "不会自动到期" : `有效 ${profile.lifetimeMinutes} 分钟`;
  const details = [`${mobilePolicyKindLabel(profile.kind)} · ${mobilePolicyModeLabel(profile.mode, profile.kind)}`, duration];
  if (profile.kind === "system" && profile.systemSettings) {
    const enabled = systemPolicyLabels.filter(([key]) => profile.systemSettings[key]).map(([, label]) => label);
    details.push(enabled.length ? `包含：${enabled.join("、")}` : "当前没有启用的系统设置");
  }
  if ((profile.kind === "application" || profile.kind === "websiteApplication") &&
      String(profile.mode || "").toLowerCase().includes("enforce"))
    details.push("启用前必须核对逐台影响预览并再次确认");
  return details.join(" · ");
}

function validPolicyRevision(value) {
  return Number.isSafeInteger(value) && value >= 0;
}

function savedPolicyProfileStatusReady(targets, profile) {
  if (!profile || !targets.length) return false;
  const duplicates = duplicateDirectoryTargets();
  if (targets.some(target => duplicates.has(normalizeTarget(target)))) return false;
  const needsWebsite = profile.kind === "website" || profile.kind === "websiteApplication";
  const needsApplication = profile.kind === "application" || profile.kind === "websiteApplication";
  const needsSystem = profile.kind === "system";
  return targets.every(target => {
    const status = latestStatusByTarget.get(normalizeTarget(target));
    if (!status || !status.online || status.needsReview || !status.supportsPolicyCompareAndSet) return false;
    if (needsWebsite && !validPolicyRevision(status.websiteRevision)) return false;
    if (needsApplication && (!status.applicationSupported || !validPolicyRevision(status.applicationRevision))) return false;
    if (needsSystem && (!status.systemPolicy?.supported || status.systemPolicy.pending ||
        !validPolicyRevision(status.systemPolicy.revision))) return false;
    return true;
  });
}

function savedPolicyProfileReadinessMessage(targets, profile) {
  if (!savedPolicyProfilesAvailable) return "请先读取教师电脑上已保存的策略预设。";
  if (!profile) return "请选择一个教师端策略预设。";
  if (!targets.length) return "请选择至少一台学生电脑，再读取状态并发送预设。";
  const duplicates = duplicateDirectoryTargets();
  if (targets.some(target => duplicates.has(normalizeTarget(target))))
    return "所选目标在多个地点重复；请先在教师端修复地点目录。";
  const statuses = targets.map(target => latestStatusByTarget.get(normalizeTarget(target)));
  if (statuses.some(status => status?.needsReview))
    return "学生 Agent 身份或签名需要核对；请先在教师端确认身份，再刷新手机状态。";
  if (statuses.some(status => !status?.online))
    return "至少一台学生电脑没有回应；请检查学生 Agent 和校园网，再刷新状态。";
  if (statuses.some(status => !status?.supportsPolicyCompareAndSet))
    return "至少一台学生 Agent 版本过旧或状态不完整；请更新学生端并重新读取状态。";
  if ((profile.kind === "website" || profile.kind === "websiteApplication") &&
      statuses.some(status => !validPolicyRevision(status?.websiteRevision)))
    return "没有读到网站策略版本；请刷新所选电脑状态后重试。";
  if ((profile.kind === "application" || profile.kind === "websiteApplication") &&
      statuses.some(status => !status?.applicationSupported || !validPolicyRevision(status.applicationRevision)))
    return "所选电脑未报告可用的应用策略版本；请更新学生端或在教师端核对。";
  if (profile.kind === "system" && statuses.some(status => !status.systemPolicy?.supported ||
      status.systemPolicy.pending || !validPolicyRevision(status.systemPolicy.revision)))
    return "所选电脑的系统策略状态不完整或仍待处理；请在教师端核对后刷新。";
  return "状态已读回并通过身份核对，可以安全发送。";
}

function expectedRevisionFieldsForProfile(targets, profile) {
  const expected = {};
  const add = (field, readRevision) => {
    const revisions = Object.create(null);
    for (const target of targets) {
      const revision = readRevision(latestStatusByTarget.get(normalizeTarget(target)));
      if (!validPolicyRevision(revision)) return false;
      revisions[target] = revision;
    }
    expected[field] = revisions;
    return true;
  };
  if ((profile.kind === "website" || profile.kind === "websiteApplication") &&
      !add("expectedWebsiteRevisions", status => status?.websiteRevision)) return null;
  if ((profile.kind === "application" || profile.kind === "websiteApplication") &&
      !add("expectedApplicationRevisions", status => status?.applicationRevision)) return null;
  if (profile.kind === "system" &&
      !add("expectedSystemRevisions", status => status?.systemPolicy?.revision)) return null;
  return expected;
}

function updateSavedPolicyProfileControls(targets = selectedTargets()) {
  const select = $("saved-policy-profile");
  const refresh = $("refresh-saved-policy-profiles");
  const apply = $("apply-saved-policy-profile");
  const disable = $("disable-saved-policy-profile");
  const summary = $("saved-policy-profile-summary");
  const readiness = $("saved-policy-scope-status");
  if (!select || !refresh || !apply || !disable || !summary || !readiness) return;
  const profile = selectedSavedPolicyProfile();
  const statusReady = savedPolicyProfileStatusReady(targets, profile);
  const actionBlocked = policyOperationInFlight || pendingSavedPolicyReview !== null;
  select.disabled = !savedPolicyProfilesAvailable || savedPolicyProfiles.length === 0 || actionBlocked;
  refresh.disabled = savedPolicyProfilesLoading || actionBlocked;
  apply.disabled = !savedPolicyProfilesAvailable || !profile || !targets.length || !statusReady || actionBlocked;
  disable.disabled = !savedPolicyProfilesAvailable || !profile || !targets.length || !statusReady || actionBlocked;
  summary.textContent = savedPolicyProfileDescription(profile);
  readiness.textContent = statusReady && !actionBlocked
    ? savedPolicyProfileReadinessMessage(targets, profile)
    : actionBlocked ? pendingSavedPolicyReview ? "应用影响预览待确认；其他策略操作暂时停用。" : "正在发送策略预设…"
      : savedPolicyProfileReadinessMessage(targets, profile);
}

function renderSavedPolicyProfiles(selectedId = $("saved-policy-profile")?.value || "") {
  const select = $("saved-policy-profile");
  if (!select) return;
  select.replaceChildren();
  const placeholder = document.createElement("option");
  placeholder.value = "";
  placeholder.textContent = !savedPolicyProfilesAvailable
    ? "预设暂不可用" : savedPolicyProfiles.length ? "选择教师端策略预设" : "教师端尚无已保存预设";
  select.append(placeholder);
  for (const profile of savedPolicyProfiles) {
    const option = document.createElement("option");
    option.value = profile.id;
    option.textContent = `${profile.name} · ${mobilePolicyKindLabel(profile.kind)}`;
    select.append(option);
  }
  select.value = savedPolicyProfiles.some(profile => profile.id === selectedId) ? selectedId : "";
  updateSavedPolicyProfileControls();
}

async function refreshSavedPolicyProfiles(showToast = true) {
  const status = $("saved-policy-profiles-status");
  const button = $("refresh-saved-policy-profiles");
  const selectedId = $("saved-policy-profile")?.value || "";
  savedPolicyProfilesLoading = true;
  if (status) status.textContent = "正在读取教师电脑已保存的策略预设…";
  updateSavedPolicyProfileControls();
  try {
    const profiles = await api("/api/profiles");
    const supportedKinds = new Set(["website", "application", "websiteApplication", "system"]);
    if (!Array.isArray(profiles) || profiles.some(profile => !profile || typeof profile.id !== "string" ||
        typeof profile.name !== "string" || !supportedKinds.has(profile.kind) ||
        !Number.isSafeInteger(profile.lifetimeMinutes) || profile.lifetimeMinutes < 0))
      throw new Error("教师端返回的策略预设格式无效。");
    savedPolicyProfiles = profiles;
    savedPolicyProfilesAvailable = true;
    renderSavedPolicyProfiles(selectedId);
    if (status) status.textContent = profiles.length
      ? `已读取 ${profiles.length} 个教师端预设；发送后会返回逐台结果。`
      : "教师电脑当前没有已保存的策略预设；可在下方使用自定义网站限制。";
    if (showToast) toast(profiles.length ? "已读取教师端策略预设。" : "教师端当前没有已保存预设。");
    return true;
  } catch (error) {
    savedPolicyProfiles = [];
    savedPolicyProfilesAvailable = false;
    renderSavedPolicyProfiles("");
    if (status) status.textContent = `读取失败：${error?.message || "请检查教师电脑连接后重试。"}`;
    if (showToast) toast(`教师端策略预设暂不可用：${error?.message || "请稍后重试。"}`);
    return false;
  } finally {
    savedPolicyProfilesLoading = false;
    if (button) button.disabled = Boolean(pendingSavedPolicyReview) || policyOperationInFlight;
    updateSavedPolicyProfileControls();
  }
}

function policyModeLabel(mode) {
  return mode === "disabled" ? "已解除" : mode === "audit" ? "先看影响" :
    mode === "enforce" ? "阻止" : "状态未知";
}

function websiteModeLabel(mode) {
  return mode === "disabled" ? "已解除" : mode === "blocklist" ? "阻止指定网站" :
    mode === "allowlist" ? "只允许指定网站" : mode === "combined" ? "阻止指定网站，可放行例外" :
      "状态未知";
}

function edgeWebsitePolicyLabel(edge) {
  if (!edge) return "Edge 扩展状态未知";
  const mode = edge.executionMode === "extensionRealtime" ? "实时扩展模式" : "旧注册表模式";
  const applied = (edge.profiles || []).filter(profile => profile.status === "applied").length;
  const degraded = (edge.profiles || []).filter(profile => profile.status === "degraded").length;
  if (!edge.profiles?.length) return `Edge ${mode} · 尚无当前策略回执`;
  return `Edge ${mode} · Profile 已应用 ${applied} · 降级 ${degraded}`;
}

function runtimeEdgePolicyLabel(runtime) {
  if (runtime?.edgeProfileCount == null) return "Edge 策略状态未知";
  const mode = runtime.edgeExecutionMode === "extensionRealtime" ? "实时扩展模式" : "旧注册表模式";
  return `Edge ${mode} · 降级 ${runtime.edgeDegradedProfileCount || 0}/${runtime.edgeProfileCount}`;
}

function inputGuardStatusLabel(inputGuard) {
  if (!inputGuard) return "键鼠状态：此 Agent 版本未提供";
  if (!inputGuard.supported || inputGuard.phase === "unsupported")
    return "键鼠状态：Agent 不支持或未安装 Guard";
  switch (inputGuard.phase) {
    case "unlocked": return "键鼠状态：未锁定";
    case "starting": return "键鼠状态：正在启动 Guard";
    case "locked": return `键鼠状态：已锁定${inputGuard.leaseExpiresUtc ? " · 租约至 " + formatDateTime(inputGuard.leaseExpiresUtc) : ""}`;
    case "unlocking": return "键鼠状态：正在解除";
    case "expired": return "键鼠状态：租约已到期";
    case "guardExited": return "键鼠状态：Guard 已退出，需复核";
    default: return "键鼠状态：未知";
  }
}

function targetPolicySummary(target) {
  const status = latestStatusByTarget.get(normalizeTarget(target));
  if (!status?.online || status.needsReview) return "状态未知或待核对";
  if (status.runtimeSummary) {
    const runtime = status.runtimeSummary;
    const website = runtime.websiteMode == null ? "网站限制状态未知" :
      `网站限制：${websiteModeLabel(runtime.websiteMode)}${formatExpiry(runtime.websiteExpiresUtc)}`;
    const application = !runtime.applicationSupported || runtime.applicationMode == null
      ? "应用限制状态未知" : `应用${policyModeLabel(runtime.applicationMode)}${formatExpiry(runtime.applicationExpiresUtc)}`;
    const system = !runtime.systemPolicySupported ? "电脑设置未启用" : runtime.systemPolicyPending
      ? "电脑设置待处理" : "Agent 已报告电脑设置（效果待核验）";
    return [website, runtimeEdgePolicyLabel(runtime), application, system,
      inputGuardStatusLabel(runtime.inputGuard),
      `最近检查 ${formatDateTime(runtime.collectedUtc)}`].join("；");
  }
  const website = status.websiteMode == null ? "网站限制状态未知" :
    `网站限制：${websiteModeLabel(status.websiteMode)}${formatExpiry(status.websiteExpiresUtc)}`;
  const application = !status.applicationSupported || status.applicationMode == null
    ? "应用限制状态未知" : `应用${policyModeLabel(status.applicationMode)}${formatExpiry(status.applicationExpiresUtc)}`;
  const system = !status.systemPolicy?.supported ? "电脑设置状态未知" : status.systemPolicy.pending
    ? "电脑设置待核对" : !status.systemPolicy.settings ? "电脑设置未启用" :
      `Agent 已报告电脑设置${systemPolicyLabels.some(([key]) => status.systemPolicy.settings[key]) ? "已启用" : "已解除"}（效果待核验）`;
  return [website, edgeWebsitePolicyLabel(status.edgeWebsite), application, system,
    `最近检查 ${formatDateTime(status.collectedUtc)}`].join("；");
}

function targetConfirmationSummary(targets) {
  const shown = targets.slice(0, 6).map(target =>
    `${targetDisplayName(target)}：${targetPolicySummary(target)}`);
  if (targets.length > shown.length) shown.push(`其余 ${targets.length - shown.length} 台电脑状态已核验`);
  return shown.join("\n");
}

function sameTargets(left, right) {
  const normalize = values => [...values].sort((a, b) => a.localeCompare(b, "en", { sensitivity: "base" }));
  return JSON.stringify(normalize(left)) === JSON.stringify(normalize(right));
}

function renderRooms(preserveSelection = false) {
  const selectedBeforeRefresh = preserveSelection
    ? new Set(selectedTargets().map(normalizeTarget))
    : new Set();
  const duplicates = duplicateDirectoryTargets();
  roomList.replaceChildren();
  for (const room of rooms) {
    const card = document.createElement("section");
    card.className = "room-card";
    const heading = document.createElement("div");
    heading.className = "room-heading";
    const name = document.createElement("strong");
    name.textContent = room.name || "未命名地点";
    const count = document.createElement("small");
    count.textContent = (room.targets || []).length + " 台";
    heading.append(name, count);
    const computers = document.createElement("div");
    computers.className = "computer-list";
    for (const target of room.targets || []) {
      const duplicate = duplicates.has(normalizeTarget(target));
      const studentName = targetDisplayName(target);
      const missingName = studentName.startsWith("未填写姓名");
      const label = document.createElement("label");
      label.className = "computer-choice";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.dataset.target = target;
      input.disabled = duplicate;
      input.checked = !input.disabled && selectedBeforeRefresh.has(normalizeTarget(target));
      if (input.disabled) label.classList.add("is-disabled");
      input.addEventListener("change", () => {
        if (policyPageMode === "single") {
          selectedComputerTarget = null;
          setPolicyPageMode("bulk");
        }
        latestStatusByTarget.clear();
        updateSelectionCount();
        void refreshStatusIfSelected();
      });
      const value = document.createElement("span");
      value.className = "computer-name";
      value.textContent = studentName;
      label.append(input, value);
      if (input.disabled) {
        const hint = document.createElement("small");
        hint.className = "computer-choice-hint";
        hint.textContent = "地点重复";
        label.append(hint);
      } else if (missingName) {
        const hint = document.createElement("small");
        hint.className = "computer-choice-hint";
        hint.textContent = "未填写姓名 · 可按电脑名/IP 查看";
        label.append(hint);
      }
      computers.append(label);
    }
    card.append(heading, computers);
    roomList.append(card);
  }
  if (!rooms.length) {
    const empty = document.createElement("p");
    empty.className = "muted";
    empty.textContent = "没有可选学生电脑名单，请先在教师电脑添加。";
    roomList.append(empty);
  }
  renderComputerDirectory();
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
  if (uniqueRequested.size !== requested.length || matched.length !== uniqueRequested.size || matched.some(input => input.disabled) ||
      matchedUnique.size !== uniqueRequested.size) {
    classroomTargetDefaultState = "mismatch";
    targetPicker.open = true;
    updateSelectionCount();
    return;
  }

  for (const input of inputs) input.checked = !input.disabled && uniqueRequested.has(normalizeTarget(input.dataset.target));
  classroomTargetDefaultState = "matched";
  targetPicker.open = false;
  updateSelectionCount();
}

async function refreshCustomWebsiteLists(showToast = true) {
  const status = $("custom-website-lists-status");
  const button = $("refresh-custom-website-lists");
  if (showToast && websiteListsDirty() &&
      !window.confirm("重新读取会覆盖手机上尚未保存的名单修改。仍要重新读取吗？")) return false;
  if (button) button.disabled = true;
  if (status) status.textContent = "正在读取教师端当前名单……";
  try {
    const result = await api("/api/website-policy-lists");
    if (!result || typeof result.blockedDomains !== "string" || typeof result.allowedDomains !== "string")
      throw new Error("教师端返回的网站名单格式无效。");
    websiteListsAvailable = true;
    $("custom-website-mode").value = ["blocklist", "allowlist", "combined"].includes(result.mode)
      ? result.mode : result.allowedDomains.trim() && result.blockedDomains.trim() ? "combined" :
        result.allowedDomains.trim() ? "allowlist" : "blocklist";
    $("custom-website-blocked").value = result.blockedDomains;
    $("custom-website-allowed").value = result.allowedDomains;
    websiteListsUpdatedUtc = result.updatedUtc || null;
    websiteListsSavedSnapshot = websiteListSnapshot();
    updateCustomWebsiteModeGuidance();
    const blockedCount = readDomainList("custom-website-blocked").length;
    const allowedCount = readDomainList("custom-website-allowed").length;
    if (status) status.textContent = result.updatedUtc
      ? `已读取 ${result.campusId} · 阻止 ${blockedCount} 个域名 · 允许 ${allowedCount} 个域名 · ${new Date(result.updatedUtc).toLocaleString()}`
      : "教师端当前名单为空；请在教师电脑编辑后刷新。";
    if (showToast) toast("已刷新教师端当前网站名单。");
    updateSelectionCount();
    updateWebsiteListSaveButton();
    return true;
  } catch (error) {
    websiteListsAvailable = false;
    const message = error?.message || "请检查教师电脑连接后刷新。";
    if (status) status.textContent = `读取失败：${message}`;
    if (showToast) toast(`教师端网站名单暂不可用：${message}`);
    updateWebsiteListSaveButton();
    updateSelectionCount();
    return false;
  } finally {
    if (button) button.disabled = false;
  }
}

async function saveCustomWebsiteLists(showToast = true) {
  const status = $("custom-website-lists-status");
  if (!websiteListsAvailable) {
    if (status) status.textContent = "请先成功读取教师端当前名单，再保存手机上的修改。";
    return false;
  }
  if (!websiteListsDirty()) return true;
  if (websiteListsSaving) return false;
  websiteListsSaving = true;
  updateWebsiteListSaveButton();
  if (status) status.textContent = "正在把名单保存到教师电脑……";
  try {
    const result = await api("/api/website-policy-lists", {
      method: "POST",
      body: {
        mode: $("custom-website-mode").value,
        blockedDomains: $("custom-website-blocked").value,
        allowedDomains: $("custom-website-allowed").value,
        expectedUpdatedUtc: websiteListsUpdatedUtc
      }
    });
    if (!result || typeof result.blockedDomains !== "string" ||
        typeof result.allowedDomains !== "string" || typeof result.updatedUtc !== "string")
      throw new Error("教师端返回的网站名单格式无效。");
    websiteListsAvailable = true;
    websiteListsUpdatedUtc = result.updatedUtc;
    $("custom-website-mode").value = ["blocklist", "allowlist", "combined"].includes(result.mode)
      ? result.mode : "blocklist";
    $("custom-website-blocked").value = result.blockedDomains;
    $("custom-website-allowed").value = result.allowedDomains;
    websiteListsSavedSnapshot = websiteListSnapshot();
    updateCustomWebsiteModeGuidance();
    const blockedCount = readDomainList("custom-website-blocked").length;
    const allowedCount = readDomainList("custom-website-allowed").length;
    if (status) status.textContent = `已保存到教师电脑 · 阻止 ${blockedCount} 个域名 · 允许 ${allowedCount} 个域名`;
    updateSelectionCount();
    if (showToast) toast("手机编辑的名单已保存到教师电脑。");
    return true;
  } catch (error) {
    if (status) status.textContent = error.status === 409
      ? "教师端名单已在其他位置更新。手机修改仍保留；请复制修改内容后重新读取，再合并保存。"
      : error.status === 400
        ? "保存失败：请检查域名格式，并确认每份名单不超过 1000 个域名。"
        : `保存失败：${error?.message || "请检查连接后重试。"} 手机修改仍保留。`;
    if (showToast) toast(error.status === 409
      ? "教师端名单已更新，请先合并后再保存。"
      : error?.message || "名单没有保存，请检查后重试。");
    return false;
  } finally {
    websiteListsSaving = false;
    updateWebsiteListSaveButton();
  }
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
    latestStatusByTarget.set(normalizeTarget(item.target), item);
    const card = document.createElement("article");
    card.className = "result-card";
    const title = document.createElement("h3");
    title.textContent = targetDisplayName(item.target);
    const state = document.createElement("span");
    state.className = "state " + (item.needsReview ? "warn" : item.online ? "good" : "bad");
    state.textContent = item.needsReview ? "需要核对" : item.online ? "已收到回执" : "未收到回执";
    const heading = document.createElement("div");
    heading.className = "result-card-heading";
    heading.append(title, state);
    card.append(heading);

    const runtime = item.runtimeSummary;
    const websiteMode = runtime?.websiteMode != null ? websiteModeLabel(runtime.websiteMode) :
      item.websiteMode == null ? "未知" : websiteModeLabel(item.websiteMode);
    const runtimeAppSupported = runtime ? runtime.applicationSupported : item.applicationSupported;
    const runtimeAppMode = runtime ? runtime.applicationMode : item.applicationMode;
    const applicationMode = !runtimeAppSupported || !runtimeAppMode ? "未知" :
      runtimeAppMode === "disabled" ? "已解除" : runtimeAppMode === "enforce" ? "阻止" : "先看影响";
    const systemMode = runtime
      ? !runtime.systemPolicySupported ? "未启用" : runtime.systemPolicyPending ? "待处理" : "Agent 已报告 · 效果待核验"
      : !item.systemPolicy?.supported ? "未知" : item.systemPolicy.pending ? "待核对" :
        !item.systemPolicy.settings ? "未设置" :
          `Agent 已报告 · ${systemPolicyLabels.some(([key]) => item.systemPolicy.settings[key]) ? "已启用" : "已解除"} · 效果待核验`;
    const edgeMode = runtime ? runtimeEdgePolicyLabel(runtime) : edgeWebsitePolicyLabel(item.edgeWebsite);
    appendParagraph(card, `网站：${websiteMode} · ${edgeMode} · 应用：${applicationMode} · 电脑设置：${systemMode}`);
    if (runtime) appendParagraph(card, inputGuardStatusLabel(runtime.inputGuard));

    const detail = document.createElement("details");
    detail.className = "status-more";
    const summary = document.createElement("summary");
    summary.textContent = "查看限制状态";
    const detailContent = document.createElement("div");
    detailContent.className = "status-more-content";
    detail.append(summary, detailContent);
    const addDetail = text => appendParagraph(detailContent, text);

    if (item.collectedUtc) addDetail("最近检查：" + formatDateTime(item.collectedUtc));
    addDetail("安全检查：" + (item.supportsPolicyCompareAndSet ? "已完成" : "未完成，请先更新学生端"));
    if (!item.online || item.needsReview)
      addDetail(item.detail || "未收到可核验的学生 Agent 状态。");
    if (runtime) addDetail("状态来源：教师电脑转发的签名 Agent 摘要；只表示 Agent 报告状态，Windows 和浏览器实际效果仍需现场核验。");
    if (item.websiteMode) {
      addDetail("网站限制：" + websiteMode + formatExpiry(item.websiteExpiresUtc));
      if (runtime)
        addDetail("网站名单明细未包含在轻量状态中；请在教师端查看详细诊断。");
      else if (item.websiteBlockedDomains == null || item.websiteAllowedDomains == null)
        addDetail("此学生端版本不支持读取网站明细，请更新学生端");
      else {
        addDetail("阻止的网站：" + (item.websiteBlockedDomains.length ? item.websiteBlockedDomains.join("、") : "未设置"));
        addDetail("允许的网站：" + (item.websiteAllowedDomains.length ? item.websiteAllowedDomains.join("、") : "未设置"));
      }
    } else addDetail("网站限制：暂未收到状态");
    const edgeWebsite = item.edgeWebsite;
    if (runtime) {
      addDetail(inputGuardStatusLabel(runtime.inputGuard));
      if (runtime.edgeProfileCount == null) addDetail("Edge 执行器状态未知。");
      else addDetail(`${runtimeEdgePolicyLabel(runtime)} · 策略 r${runtime.edgePolicyRevision}`);
    } else if (!edgeWebsite) {
      addDetail("Edge 扩展：学生 Agent 版本较旧或暂未提供扩展状态");
    } else {
      addDetail(`Edge 执行器：${edgeWebsite.executionMode === "extensionRealtime" ? "实时扩展模式" : "旧注册表模式"} · 策略 r${edgeWebsite.revision}`);
      if (!edgeWebsite.profiles?.length) addDetail("Edge 扩展：尚未收到当前策略的 Profile 应用回执");
      else for (const profile of edgeWebsite.profiles) {
        const profileLabel = String(profile.profileId || "").slice(0, 8) || "未知 Profile";
        addDetail(`Edge Profile ${profileLabel}：${profile.status === "applied" ? "已读回应用" : "降级"} · 规则 ${profile.ruleCount}${profile.errorCode ? " · " + profile.errorCode : ""}`);
      }
    }
    if (!item.applicationSupported) addDetail("应用限制：暂未收到状态，可能需要更新学生端");
    else if (item.applicationMode) {
      addDetail("应用限制：" + applicationMode + formatExpiry(item.applicationExpiresUtc));
    } else addDetail("应用限制：暂未收到状态");
    const system = item.systemPolicy;
    if (runtime) {
      if (!runtime.systemPolicySupported) addDetail("电脑设置：当前没有已报告的系统策略。");
      else if (runtime.systemPolicyPending) addDetail("电脑设置：Agent 报告系统策略仍待处理。");
      else addDetail("电脑设置：Agent 已报告策略状态；具体开关与实际效果仍待现场核验。");
      addDetail("Veyon 服务：" + (runtime.veyonServiceState || "状态未知"));
      addDetail("学生端更新事务：" + (runtime.updateTransactionState || "状态未知"));
    } else if (!system) {
      addDetail("电脑设置：暂未收到状态，可能需要更新学生端");
    } else if (!system.supported) {
      addDetail("电脑设置：当前学生端暂不支持");
    } else if (system.pending) {
      addDetail("电脑设置：正在恢复或需要在教师电脑上核对");
    } else if (!system.settings) {
      addDetail("电脑设置：尚未启用");
    } else {
      const active = systemPolicyLabels.some(([key]) => system.settings[key]);
      addDetail("Agent 报告的电脑设置：" + (active ? "已启用，不会自动解除" : "已解除") + "；实际 Windows 效果仍待现场核验。");
      for (const [key, label] of systemPolicyLabels) {
        addDetail(label + "（Agent 报告）：" + (system.settings[key] ? "已启用" : "未启用"));
      }
    }
    if (item.needsReview) addDetail("操作结果需要核对，请在教师电脑查看详细信息。");
    card.append(detail);
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

function formatTimeOfDay(value) {
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return "未知";
  return new Intl.DateTimeFormat("zh-CN", {
    hour: "2-digit", minute: "2-digit", hourCycle: "h23"
  }).format(date);
}

function formatDateTime(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "未知";
  const parts = Object.fromEntries(new Intl.DateTimeFormat("zh-CN", {
    year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit",
    hourCycle: "h23"
  }).formatToParts(date).map(part => [part.type, part.value]));
  return `${parts.year}-${parts.month}-${parts.day} ${parts.hour}:${parts.minute}`;
}

function renderOperation(result, container = operationResult, reviewActions = null) {
  container.replaceChildren();
  const items = Array.isArray(result.results) ? result.results : [];
  const applicationReviewTargets = Array.isArray(result.applicationReview) ? result.applicationReview.length : 0;
  const needsReview = items.filter(item => item.needsReview).length;
  const responded = items.filter(item => item.agentAccepted && !item.needsReview).length;
  const notResponded = items.length - needsReview - responded;
  const summary = document.createElement("p");
  summary.className = "muted";
  summary.textContent = result.requiresReview
    ? `已收到 ${applicationReviewTargets} 台电脑的应用信息。请先查看影响预览，确认后才会启用限制。`
    : items.length
      ? `共 ${items.length} 台电脑：${responded} 台已回应，${needsReview} 台需核对，${notResponded} 台未回应。`
        : result.message ? "操作没有返回逐台电脑结果。" : "没有收到电脑回应，请检查连接后重试。";
  container.append(summary);
  if (typeof result.message === "string" && result.message.trim())
    appendParagraph(container, result.message.trim());
  for (const item of items) {
    const card = document.createElement("article");
    card.className = "result-card";
    const title = document.createElement("h3");
    title.textContent = targetDisplayName(item.target);
    const state = document.createElement("span");
    state.className = "state " + (item.needsReview ? "warn" : item.agentAccepted ? "good" : "bad");
    state.textContent = item.needsReview ? "需核对" : item.agentAccepted ? "电脑已回应" : "未回应";
    card.append(title, state);
    appendParagraph(card, item.needsReview
      ? "结果未能确认，请在教师电脑查看详细信息。"
      : item.agentAccepted
        ? "已收到电脑回执；实际限制效果仍需在学生电脑上确认。"
        : "暂未收到电脑回应，请检查连接后重试。");
    container.append(card);
  }
  if (result.applicationReview?.length) {
    const reviewTitle = document.createElement("h3");
    reviewTitle.textContent = "应用限制影响预览";
    container.append(reviewTitle);
    for (const target of result.applicationReview) {
      const card = document.createElement("article");
      card.className = "result-card";
      const title = document.createElement("h3");
      title.textContent = targetDisplayName(target.target);
      card.append(title);
      if (target.isSimulation) {
        appendParagraph(card, "根据已登记的软件估算可能影响范围；这不是实际使用记录，也不会拦截软件。");
        appendParagraph(card, "未登记或手动运行的软件可能不在检查范围内。");
      }
      if (!target.rules?.length) appendParagraph(card, target.isSimulation
        ? "没有发现可能受影响的软件。"
        : "最近没有相关记录。");
      for (const rule of target.rules || []) {
        appendParagraph(card, target.isSimulation
          ? rule.displayName + "：可能影响 " + rule.wouldBlockCount + " 个软件"
          : rule.displayName + "：相关记录 " + rule.wouldBlockCount + " 次，实际阻止 " + rule.blockedCount + " 次");
      }
      container.append(card);
    }
  }
  if (result.requiresReview && typeof result.reviewToken === "string" && reviewActions) {
    const actions = document.createElement("div");
    actions.className = "button-stack application-review-actions";
    const confirm = document.createElement("button");
    confirm.type = "button";
    confirm.className = "primary-button";
    confirm.textContent = "核对影响后确认启用";
    confirm.addEventListener("click", () => void reviewActions.confirm());
    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.className = "text-button";
    cancel.textContent = "取消本次应用";
    cancel.addEventListener("click", reviewActions.cancel);
    actions.append(confirm, cancel);
    container.append(actions);
  }
}

async function loadDashboard() {
  pairView.classList.add("hidden");
  $("saved-session").classList.add("hidden");
  $("pair-form").classList.add("hidden");
  setControlVisibility(true);
  $("logout-button").classList.remove("hidden");
  const session = await api("/api/session");
  const displayName = typeof session.device?.displayName === "string"
    ? session.device.displayName.trim() : "";
  $("welcome-title").textContent = `你好，${displayName || "老师"}`;
  $("paired-device-name").textContent = session.device?.displayName || "手机";
  applyClassroomSession(session);
  setActiveControlTab("classroom");
  renderClassroomMode(true);
  const loadWarnings = [];
  const [roomsResult, websiteListsResult, savedPolicyProfilesResult] = await Promise.allSettled([
    api("/api/rooms"), refreshCustomWebsiteLists(false), refreshSavedPolicyProfiles(false)
  ]);
  if (roomsResult.status === "fulfilled" && Array.isArray(roomsResult.value)) {
    rooms = roomsResult.value;
  } else {
    rooms = [];
    loadWarnings.push("学生电脑名单暂不可用，请稍后刷新。");
  }
  if (websiteListsResult.status === "rejected" || websiteListsResult.value !== true)
    loadWarnings.push("教师端网站名单暂不可用，请稍后刷新。");
  if (savedPolicyProfilesResult.status === "rejected" || savedPolicyProfilesResult.value !== true)
    loadWarnings.push("教师端策略预设暂不可用，请稍后刷新。");
  renderRooms();
  applyActiveClassroomTargetDefaults();
  await refreshStatusIfSelected();
  try {
    await loadClassroomRestores();
  } catch (error) {
    loadWarnings.push("待恢复内容暂不可用，请稍后刷新。");
  }
  const warning = $("dashboard-load-warning");
  warning.textContent = loadWarnings.length
    ? "教师端已连接；部分信息暂不可用。" + loadWarnings.join(" ")
    : "";
  warning.classList.toggle("hidden", loadWarnings.length === 0);
  startClassroomEventPolling();
}

async function connectDashboard() {
  if (connectionAttemptInFlight) return false;
  connectionAttemptInFlight = true;
  try {
    return await connectDashboardCore();
  } finally {
    connectionAttemptInFlight = false;
  }
}

async function connectDashboardCore() {
  try {
    await loadDashboard();
    setConnectionState(true);
    return true;
  } catch (error) {
    if (error.status === 401 && error.pairingRevoked) {
      await clearToken().catch(() => {});
      accessToken = null;
      await resumeTokenFromCookie().catch(() => null);
      accessToken = null;
      setControlVisibility(false);
      $("boot-view").classList.add("hidden");
      pairView.classList.remove("hidden");
      $("logout-button").classList.add("hidden");
      $("saved-session").classList.add("hidden");
      $("pair-form").classList.remove("hidden");
      showScannedPairingInvite();
      toast("教师已解除这部手机的连接，请重新申请连接。");
      return false;
    }
    pairView.classList.add("hidden");
    setControlVisibility(true);
    $("logout-button").classList.remove("hidden");
    setConnectionState(false);
    toast("暂时无法连接教师电脑，请检查连接后重试。");
    return false;
  }
}

function applyClassroomSession(session) {
  const previousScreenTargets = activeClassroomScreenTargets;
  const nextTargets = Array.isArray(session?.activeClassroomTargets)
    ? session.activeClassroomTargets.filter(target => typeof target === "string" && target.trim().length > 0)
    : [];
  const nextSessionId = typeof session?.activeClassroomSessionId === "string"
    ? session.activeClassroomSessionId
    : null;
  const sessionChanged = activeClassroomSessionId !== nextSessionId;
  if (sessionChanged) {
    classroomQuickControlSnapshot = null;
    $("classroom-quick-control-result").replaceChildren();
  }
  activeClassroomTargets = nextTargets;
  activeClassroomSessionId = nextSessionId;
  activeClassroomSeatLocations = new Map();
  const rawLocations = session?.activeClassroomSeatLocations;
  if (Array.isArray(rawLocations) && rawLocations.length <= 150) {
    const validated = new Map();
    const allowedTargets = new Set(nextTargets.map(normalizeTarget));
    let valid = rawLocations.length > 0;
    for (const location of rawLocations) {
      const key = normalizeTarget(location?.target);
      if (!key || !Number.isSafeInteger(location?.row) || location.row < 1 || location.row > 150 ||
          !Number.isSafeInteger(location?.column) || location.column < 1 || location.column > 150 ||
          !allowedTargets.has(key) || validated.has(key)) {
        valid = false;
        break;
      }
      const seatNumber = location?.seatNumber == null ? null : location.seatNumber;
      const studentName = location?.studentName == null ? "" : location.studentName;
      const computerName = location?.computerName == null ? "" : location.computerName;
      if ((seatNumber !== null && (!Number.isSafeInteger(seatNumber) || seatNumber < 1 || seatNumber > 150)) ||
          typeof studentName !== "string" || studentName.length > 100 ||
          /[\u0000-\u001f\u007f]/.test(studentName) ||
          typeof computerName !== "string" || computerName.length > 128 ||
          /[\u0000-\u001f\u007f]/.test(computerName)) {
        valid = false;
        break;
      }
      validated.set(key, {
        target: location.target,
        row: location.row,
        column: location.column,
        seatNumber,
        studentName: studentName.trim(),
        computerName: computerName.trim()
      });
    }
    if (valid) activeClassroomSeatLocations = validated;
  }
  activeClassroomScreenTargets = [];
  const rawScreenTargets = session?.activeClassroomScreenTargets;
  if (Array.isArray(rawScreenTargets) && rawScreenTargets.length <= 150) {
    const validated = [];
    const targetIds = new Set();
    const seatNumbers = new Set();
    let valid = rawScreenTargets.length > 0;
    for (const target of rawScreenTargets) {
      const targetId = typeof target?.targetId === "string" ? target.targetId.trim().toLowerCase() : "";
      const seatNumber = target?.seatNumber;
      const row = target?.row;
      const column = target?.column;
      const studentName = target?.studentName;
      const computerName = target?.computerName;
      if (!/^[0-9a-f]{32}$/.test(targetId) || targetIds.has(targetId) ||
          !Number.isSafeInteger(seatNumber) || seatNumber < 1 || seatNumber > 150 || seatNumbers.has(seatNumber) ||
          !Number.isSafeInteger(row) || row < 1 || row > 150 ||
          !Number.isSafeInteger(column) || column < 1 || column > 150 ||
          typeof studentName !== "string" || studentName.length > 100 || /[\u0000-\u001f\u007f]/.test(studentName) ||
          typeof computerName !== "string" || computerName.length > 128 || /[\u0000-\u001f\u007f]/.test(computerName)) {
        valid = false;
        break;
      }
      targetIds.add(targetId);
      seatNumbers.add(seatNumber);
      validated.push({ targetId, seatNumber, row, column,
        studentName: studentName.trim(), computerName: computerName.trim() });
    }
    if (valid) activeClassroomScreenTargets = validated;
  }
  activeClassroomChatTargets = [];
  const rawChatTargets = session?.activeClassroomChatTargets;
  if (Array.isArray(rawChatTargets) && rawChatTargets.length <= 150) {
    const validated = [];
    const targetIds = new Set();
    let valid = true;
    for (const target of rawChatTargets) {
      const targetId = typeof target?.targetId === "string" ? target.targetId.trim().toLowerCase() : "";
      const seatNumber = target?.seatNumber == null ? null : target.seatNumber;
      const row = target?.row == null ? null : target.row;
      const column = target?.column == null ? null : target.column;
      const studentName = target?.studentName;
      const computerName = target?.computerName;
      if (!/^[0-9a-f]{32}$/.test(targetId) || targetIds.has(targetId) ||
          (seatNumber !== null && (!Number.isSafeInteger(seatNumber) || seatNumber < 1 || seatNumber > 150)) ||
          (row !== null && (!Number.isSafeInteger(row) || row < 1 || row > 150)) ||
          (column !== null && (!Number.isSafeInteger(column) || column < 1 || column > 150)) ||
          (row === null) !== (column === null) ||
          typeof studentName !== "string" || studentName.length > 100 || /[\u0000-\u001f\u007f]/.test(studentName) ||
          typeof computerName !== "string" || computerName.length > 128 || /[\u0000-\u001f\u007f]/.test(computerName)) {
        valid = false;
        break;
      }
      targetIds.add(targetId);
      validated.push({ targetId, seatNumber, row, column,
        studentName: isIpAddress(studentName) ? "" : studentName.trim(),
        computerName: isIpAddress(computerName) ? "" : computerName.trim() });
    }
    if (valid) activeClassroomChatTargets = validated;
  }
  if (sessionChanged) resetClassroomChatState("课堂已变化，请重新选择学生。");
  else if (selectedClassroomChatTargetId && !activeClassroomChatTargets.some(target =>
      target.targetId === selectedClassroomChatTargetId))
    resetClassroomChatState("所选学生已离开课堂或暂不支持聊天。");
  renderClassroomChatTargets();
  renderClassroomChat();
  const screenTargets = classroomScreenTargets();
  const screenTargetsChanged = !sameClassroomScreenTargets(previousScreenTargets, screenTargets);
  if (sessionChanged || screenTargetsChanged) {
    classroomScreenPreviewStartController?.abort();
    singleTargetPreviewStartController?.abort();
  }
  if (classroomScreenPreviewLease && (sessionChanged || screenTargetsChanged ||
      classroomScreenPreviewTargets.some(target =>
        !screenTargets.some(active => normalizeTarget(active.targetId) === normalizeTarget(target))))) {
    const message = screenTargetsChanged
      ? "座位或学生信息已变化，屏幕查看已停止；请刷新后重新开启。"
      : "课堂已变化，屏幕查看已停止。";
    void stopClassroomScreenPreview(false);
    $("classroom-screen-preview-status").textContent = message;
  }
  renderMonitorTargets();
  renderClassroomMode();
  if (activeClassroomSessionId) void loadClassroomQuickControlSnapshot();
}

function renderClassroomMode(setDefaults = false) {
  const active = Boolean(activeClassroomSessionId && activeClassroomTargets.length > 0);
  const pill = $("classroom-mode-value");
  if (setDefaults) setActiveControlTab("classroom");
  $("classroom-target-summary").textContent = active
    ? activeClassroomTargets.length + " 台学生电脑"
    : "等待教室配置";
  pill.textContent = active ? "当前课堂" : "待配置";
  pill.className = "pill mode";
  $("classroom-mode-description").textContent = active
    ? "查看学生状态、发送消息，随时管理课堂。"
    : "教师端配置好地点和学生名单后，课堂范围会持续生效。";
  renderClassroomQuickControl();
}

function renderClassroomQuickControl() {
  const active = Boolean(activeClassroomSessionId && activeClassroomTargets.length > 0);
  const snapshot = classroomQuickControlSnapshot?.sessionId === activeClassroomSessionId ? classroomQuickControlSnapshot : null;
  const pilotEnabled = snapshot?.pilotFeatureEnabled === true;
  const inputLockedCount = (snapshot?.devices || []).filter(item => item.inputState === "locked" ||
    (!item.inputState && item.phase === "inputLockedVerified")).length;
  const inputNeedsReview = (snapshot?.devices || []).some(item => item.inputLeaseId &&
    item.inputState !== "locked" && item.phase !== "inputLockedVerified");
  const state = $("classroom-quick-control-state");
  const status = $("classroom-quick-control-status");
  $("classroom-quick-control-website").disabled = !active || classroomQuickControlUnavailable || classroomQuickControlBusy || !pilotEnabled;
  $("classroom-quick-control-input").disabled = !active || classroomQuickControlUnavailable || classroomQuickControlBusy ||
    (!pilotEnabled && inputLockedCount === 0 && !inputNeedsReview);
  $("classroom-quick-control-input").querySelector("strong").textContent = inputLockedCount > 0
    ? "解除键鼠锁定" : inputNeedsReview ? "核对并解除键鼠" : "锁定键鼠";
  $("classroom-quick-control-input").querySelector("span").textContent = inputLockedCount > 0
    ? `已核验 ${inputLockedCount} 台 · 不影响网站限制`
    : inputNeedsReview ? "重新读取租约状态 · 不改网站限制" : "断线后自动释放";
  $("classroom-quick-control-clear-websites").disabled = !active || classroomQuickControlUnavailable || classroomQuickControlBusy;
  $("classroom-quick-control-release").disabled = !active || classroomQuickControlUnavailable || classroomQuickControlBusy;
  $("mobile-stat-total").textContent = active ? activeClassroomTargets.length : "0";
  const devices = snapshot?.devices || [];
  const websiteStates = devices.map(websiteControlState);
  $("classroom-website-current").textContent = snapshot ? summarizeControlStates(websiteStates, {
    notEnabled: "未启用", waitingForAgent: "等待上线", applying: "正在下发", reported: "已写入·待核对",
    applied: "已确认生效", needsReview: "需核对", expired: "已到期", releasing: "正在解除", unknown: "状态未确认"
  }) : "正在读取…";
  $("classroom-input-current").textContent = snapshot ? summarizeControlStates(devices.map(item =>
    item.inputState && item.inputState !== "unknown" ? item.inputState : item.inputLeaseId ?
      item.phase === "inputLockedVerified" ? "locked" : "needsReview" : "notEnabled"),
    {notEnabled: "本工具未锁定", locked: "已锁定", needsReview: "需核对"}) : "正在读取…";
  $("classroom-website-delivery-hint").textContent = websiteStates.includes("waitingForAgent") || websiteStates.includes("applying")
    ? "已启用自动补发。学生机开机后需等待服务就绪，可能需 1–2 分钟；无需重复点击。"
    : websiteStates.includes("reported") ? "名单已写入，正在等待浏览器执行确认。"
    : websiteStates.every(value => value === "notEnabled") ? "启用一次后，未开机的学生电脑也会保留待下发任务；键鼠可单独操作。"
    : "网站限制与键鼠锁定独立操作，互不作为前置条件。";
  const acknowledged = devices.filter((item, index) => ["applied", "reported"].includes(websiteStates[index]) || item.phase === "inputLockedVerified").length;
  const waiting = websiteStates.filter(value => value === "waitingForAgent").length;
  const review = devices.filter((item, index) => websiteStates[index] !== "waitingForAgent" &&
    (["unknown", "needsReview", "reported", "releasing"].includes(websiteStates[index]) ||
     ["unknown", "conflict", "failed", "unsupported", "releasePending"].includes(item.phase))).length;
  const sending = devices.filter(item => ["websiteApplying", "switching", "inputLocking"].includes(item.phase)).length;
  $("mobile-stat-receipts").textContent = snapshot ? acknowledged : "—";
  $("mobile-stat-review").textContent = snapshot ? waiting + review : "—";
  state.textContent = classroomQuickControlUnavailable ? "Mac 暂不支持" : !active ? "待配置" : !snapshot ? "读取中" :
    websiteStates.includes("waitingForAgent") ? "等待自动下发" :
    websiteStates.includes("applying") ? "正在下发" : review ? "有待核对" :
    websiteStates.some(value => ["reported", "applied"].includes(value)) ? "网站限制已启用" : "状态已更新";
  state.className = "pill " + (review || waiting ? "warn" : acknowledged ? "good" : "mode");
  status.textContent = classroomQuickControlUnavailable
    ? "Mac 教师端暂不支持课堂快捷管控；网站、应用和系统策略可在“限制”页发送。"
    : !active ? "请先在教师电脑确认学生名单。" : !snapshot ? "正在读取学生状态…" :
    !pilotEnabled ? "此教师端暂未开放新管控；可以解除本工具已有管控。" :
    classroomQuickControlBusy || sending ? `正在执行 ${sending} 台，回执会自动更新。` :
    "名单写入和浏览器执行结果分别核对，展开学生可查看原因。";
  if (snapshot?.controlExpiresUtc && devices.some(item => item.mode !== "none" || item.inputLeaseId ||
      item.websiteState === "waitingForAgent")) {
    const expires = new Date(snapshot.controlExpiresUtc);
    if (!Number.isNaN(expires.getTime()))
      status.textContent += " 快捷管控将于 " + expires.toLocaleString("zh-CN", { hour12: false }) + " 自动解除。";
  }
  if (snapshot?.roomName) $("classroom-target-summary").textContent = snapshot.roomName + " · " + activeClassroomTargets.length + " 台学生电脑";
  const progress = $("classroom-quick-control-progress");
  progress.classList.toggle("hidden", !active || !snapshot || classroomQuickControlUnavailable);
  const existing = new Map(Array.from(progress.children).map(row => [row.dataset.targetId, row]));
  const present = new Set();
  devices.forEach((item, index) => {
    const key = item.targetId || `${item.displayName}-${index}`;
    present.add(key);
    let row = existing.get(key);
    if (!row) {
      row = document.createElement("details");
      row.className = "mobile-progress-row";
      row.dataset.targetId = key;
      const heading = document.createElement("summary");
      heading.append(document.createElement("strong"), document.createElement("span"));
      row.append(heading, document.createElement("p"));
      progress.append(row);
    }
    const heading = row.querySelector("summary");
    heading.firstElementChild.textContent = item.displayName || "未填写姓名";
    const badge = heading.lastElementChild;
    badge.textContent = quickControlPhaseLabel(item.phase);
    badge.className = "state " + (["websiteApplied", "inputLockedVerified"].includes(item.phase) ? "good" :
      ["notStarted", "released"].includes(item.phase) ? "neutral" : "warn");
    row.querySelector("p").textContent = "网站：" + summarizeControlStates([websiteControlState(item)], {
      notEnabled:"未启用",waitingForAgent:"等待上线后自动下发",applying:"正在下发",reported:"已写入，等待浏览器确认",
      applied:"已确认生效",needsReview:"需核对",expired:"已到期",releasing:"正在解除",unknown:"状态未确认"
    }, false) + "。" + (item.detail || "等待设备回执。");
  });
  for (const [key, row] of existing) if (!present.has(key)) row.remove();
}

function websiteControlState(item) {
  if (["notEnabled", "waitingForAgent", "applying", "reported", "applied", "needsReview", "expired", "releasing", "unknown"].includes(item.websiteState)) return item.websiteState;
  if (item.phase === "waitingForAgent") return "waitingForAgent";
  if (["websiteApplying", "switching"].includes(item.phase)) return "applying";
  if (item.phase === "websiteApplied") return "applied";
  if (item.phase === "websiteReported") return "reported";
  return item.websiteRevision ? "needsReview" : "notEnabled";
}

function summarizeControlStates(states, labels, counts = true) {
  const grouped = new Map();
  for (const state of states) grouped.set(state, (grouped.get(state) || 0) + 1);
  return Array.from(grouped, ([state, count]) => (labels[state] || "需核对") + (counts ? ` ${count} 台` : "")).join(" · ") || "暂无学生电脑";
}

function quickControlPhaseLabel(phase) {
  return ({
    waitingForAgent: "等待连接", websiteApplying: "推送中", websiteReported: "已写入 · 待核对",
    websiteApplied: "浏览器已应用", inputLockedVerified: "键鼠已核验", notStarted: "尚未操作", released: "已解除",
    switching: "切换中", inputLocking: "锁定中", releasePending: "恢复中", unknown: "需核对",
    conflict: "策略冲突", failed: "执行失败", unsupported: "暂不支持"
  })[phase] || "待确认";
}
async function loadClassroomQuickControlSnapshot() {
  if (!activeClassroomSessionId || !accessToken) return;
  try {
    const snapshot = await api("/api/classroom/quick-control");
    if (snapshot?.sessionId !== activeClassroomSessionId || !Array.isArray(snapshot.devices) ||
        snapshot.devices.length > 150 || snapshot.devices.some(item =>
          typeof item?.displayName !== "string" || !["none", "websiteRestriction", "inputLocked", "websiteAndInputLocked"].includes(item.mode) ||
          typeof item.phase !== "string"))
      throw new Error("课堂快捷管控状态无效。");
    classroomQuickControlSnapshot = snapshot;
  } catch (error) {
    classroomQuickControlSnapshot = null;
    classroomQuickControlUnavailable = error?.status === 501;
    $("classroom-quick-control-state").textContent = classroomQuickControlUnavailable ? "Mac 暂不支持" : "状态未知";
    $("classroom-quick-control-state").className = classroomQuickControlUnavailable ? "pill mode" : "pill warn";
    $("classroom-quick-control-status").textContent = error.message || "无法读取课堂管控状态；操作时会再次核验。";
  }
  renderClassroomQuickControl();
}

async function runClassroomQuickControl(action) {
  if (!activeClassroomSessionId || classroomQuickControlBusy) return;
  if (!['release', 'clearWebsiteRestrictions', 'unlockInput'].includes(action) &&
      classroomQuickControlSnapshot?.pilotFeatureEnabled !== true) {
    toast("当前教师端构建未开放课堂快捷管控试点；没有发送命令。");
    return;
  }
  const labels = {
    enableWebsiteRestriction: "启用网站限制",
    lockInput: "限时锁定键鼠",
    unlockInput: "解除键鼠锁定",
    clearWebsiteRestrictions: "恢复全班网站访问",
    release: "解除快捷管控"
  };
  const label = labels[action];
  if (!label) return;
  const deviceNames = classroomQuickControlSnapshot?.sessionId === activeClassroomSessionId
    ? classroomQuickControlSnapshot.devices.map(item => item.displayName).filter(Boolean).slice(0, 6) : [];
  const targetSummary = deviceNames.length
    ? deviceNames.join("、") + (activeClassroomTargets.length > deviceNames.length ? ` 等 ${activeClassroomTargets.length} 台电脑` : "")
    : `当前互动中的 ${activeClassroomTargets.length} 台电脑`;
  const explanation = action === "clearWebsiteRestrictions"
    ? "逐台清除当前电脑的黑名单和白名单。键鼠锁定、应用限制和系统限制保持不变；离线或无法核验的电脑不会被修改。"
    : action === "enableWebsiteRestriction"
    ? "将应用已保存的网站名单；已有其他策略的电脑会保留原策略并报告冲突。"
    : action === "lockInput"
      ? "键鼠租约会定时续期；教师端关闭或断线后，锁定最多 20 秒自动解除。"
      : action === "unlockInput"
        ? "只解除本工具持有且能通过签名状态核验的键鼠锁定；网站限制保持不变。"
      : "只解除本工具能确认归属的网站限制或键鼠租约。";
  if (!window.confirm(`${label}\n目标：${targetSummary}\n${explanation}\n继续吗？`)) return;
  classroomQuickControlBusy = true;
  renderClassroomQuickControl();
  const output = $("classroom-quick-control-result");
  output.replaceChildren();
  appendParagraph(output, "正在逐台执行并核对设备签名状态…");
  try {
    const result = await api("/api/classroom/quick-control", {
      method: "POST", body: { sessionId: activeClassroomSessionId, action }
    });
    if (result?.sessionId !== activeClassroomSessionId || !Array.isArray(result.results) ||
        result.results.length !== activeClassroomTargets.length)
      throw new Error("课堂电脑范围在操作期间发生变化；请刷新状态后重试。");
    output.replaceChildren();
    appendParagraph(output, result.message || `${label}流程已结束。`);
    for (const item of result.results.slice(0, 24)) {
      const row = document.createElement("article");
      row.className = "result-card";
      const title = document.createElement("h3");
      title.textContent = item.displayName || "学生电脑";
      row.append(title);
      const resultLabel = item.phase === "waitingForAgent" ? "等待 Agent 上线" :
        item.phase === "releasePending" ? "等待恢复" : item.succeeded ? "已核对" : item.needsReview ? "待核对" : "失败";
      appendParagraph(row, `${resultLabel} · ${item.detail || "无详细信息"}`);
      output.append(row);
    }
    if (result.results.length > 24)
      appendParagraph(output, `另有 ${result.results.length - 24} 台电脑结果未展开。`);
    await loadClassroomQuickControlSnapshot();
  } catch (error) {
    output.replaceChildren();
    appendParagraph(output, error.message || "课堂管控操作失败；请刷新状态后再试。");
  } finally {
    classroomQuickControlBusy = false;
    renderClassroomQuickControl();
  }
}

async function loadClassroomRestores() {
  const response = await api("/api/classroom/restores");
  if (!Number.isSafeInteger(response.count) || response.count < 0 || !Array.isArray(response.items) ||
      response.items.length > 50 || !Number.isSafeInteger(response.hiddenCount) || response.hiddenCount < 0)
    throw new Error("待恢复列表无效。");
  const panel = $("classroom-restore-panel");
  panel.classList.toggle("hidden", response.count === 0);
  $("classroom-restore-count").textContent = response.count + " 项";
  const list = $("classroom-restore-list");
  list.replaceChildren();
  for (const item of response.items) {
    const row = document.createElement("article");
    row.className = "result-card";
    const title = document.createElement("h3");
    title.textContent = item.studentName || "未填写姓名";
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
    renderOperation(result, $("classroom-restore-result"));
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
  activeClassroomSessionId = null;
  activeClassroomTargets = [];
  activeClassroomSeatLocations = new Map();
  activeClassroomScreenTargets = [];
  activeClassroomChatTargets = [];
  resetClassroomChatState("");
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
        activeClassroomSessionId = null;
        activeClassroomTargets = [];
        activeClassroomSeatLocations = new Map();
        activeClassroomScreenTargets = [];
        activeClassroomChatTargets = [];
        resetClassroomChatState("课堂已结束，聊天记录已清除。");
        if (classroomScreenPreviewLease) void stopClassroomScreenPreview(false);
        activeClassroomTargets = [];
        activeClassroomSeatLocations = new Map();
        activeClassroomScreenTargets = [];
        renderMonitorTargets();
        renderClassroomMode();
        await loadClassroomRestores();
      } else {
        $("classroom-events-panel").classList.remove("hidden");
        applyClassroomSession(await api("/api/session"));
        if (!Number.isSafeInteger(page.cursor) || page.cursor < classroomEventCursor ||
            !Array.isArray(page.events) || page.events.length > 50)
          throw new Error("学生消息分页无效。");
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
        $("classroom-event-status").textContent = "课堂模式常开 · 新求助会自动显示。";
      }
      renderClassroomEvents();
    } catch (error) {
      if (version !== classroomEventPollVersion) return;
      $("classroom-event-status").textContent = error.status === 401
        ? "手机授权暂不可用；请重新连接教师电脑。"
        : "学生消息连接暂时中断，正在自动重试。";
      renderClassroomEvents();
    } finally {
      if (classroomEventPollController === controller) classroomEventPollController = null;
    }
    await delay(5000);
    if (activeClassroomSessionId) await loadClassroomQuickControlSnapshot();
  }
}

function readSignedClassroomEvent(signedJson) {
  if (typeof signedJson !== "string" || signedJson.length > 16384)
    throw new Error("学生消息超过大小限制。");
  const envelope = JSON.parse(signedJson);
  if (typeof envelope.payload !== "string" || typeof envelope.signature !== "string" ||
      typeof envelope.publicKeyPem !== "string")
    throw new Error("学生消息签名封装无效。");
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
    throw new Error("学生消息与当前互动不匹配。");
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
    title.textContent = classroomTargetDisplayName(request.target) + " 需要帮助";
    card.append(title);
    const seat = activeClassroomSeatLocations.get(normalizeTarget(request.target));
    if (seat) appendParagraph(card, classroomSeatDescription(seat));
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
      input.setAttribute("aria-label", classroomTargetDisplayName(request.target) + " 的回复");
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
    title.textContent = "学生通知";
    card.append(title);
    appendClassroomNoticeMessage(card, notice.message || "");
    list.append(card);
  }
}

async function sendClassroomNotice() {
  if (!classroomEventSessionId) {
    toast("当前没有学生互动。");
    return;
  }
  const input = $("classroom-notice-input");
  const button = $("send-classroom-notice");
  const message = input.value.trim();
  if (!message) {
    toast("请先输入通知内容。");
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
    toast("通知已发送给 " + result.targetCount + " 台学生电脑。");
  } catch (error) {
    toast(error.message);
  } finally {
    button.disabled = !classroomEventSessionId;
    button.textContent = "发送通知";
  }
}

function classroomChatTargetLabel(target) {
  if (target.studentName) return target.studentName;
  return "未填写姓名";
}

function resetClassroomChatState(status = "") {
  classroomChatDrafts.clear();
  stopClassroomChatPolling();
  selectedClassroomChatTargetId = null;
  classroomChatMessages = [];
  classroomChatRevision = 0;
  classroomChatDraftEventId = null;
  classroomChatDraftMessage = null;
  classroomChatSending = false;
  classroomChatStatusText = status;
  const input = $("classroom-chat-input");
  if (input) input.value = "";
  renderClassroomChatTargets();
  renderClassroomChat();
}

function stopClassroomChatPolling() {
  classroomChatPollVersion++;
  classroomChatPollController?.abort();
  classroomChatPollController = null;
  classroomChatPollTask = null;
  clearTimeout(classroomChatExpiryTimer);
  classroomChatExpiryTimer = 0;
}

function selectClassroomChatTarget(targetId) {
  if (classroomChatSending) return;
  if (selectedClassroomChatTargetId) classroomChatDrafts.set(selectedClassroomChatTargetId, {
    text: $("classroom-chat-input").value, eventId: classroomChatDraftEventId, message: classroomChatDraftMessage
  });
  const target = activeClassroomChatTargets.find(item => item.targetId === targetId) || null;
  stopClassroomChatPolling();
  selectedClassroomChatTargetId = target?.targetId || null;
  classroomChatMessages = [];
  classroomChatRevision = 0;
  classroomChatDraftEventId = null;
  classroomChatDraftMessage = null;
  classroomChatSending = false;
  classroomChatStatusText = target ? "正在连接学生会话…" : "";
  const draft = target ? classroomChatDrafts.get(target.targetId) : null;
  classroomChatDraftEventId = draft?.eventId || null;
  classroomChatDraftMessage = draft?.message || null;
  const input = $("classroom-chat-input");
  if (input) input.value = draft?.text || "";
  renderClassroomChatTargets();
  renderClassroomChat();
  if (target && activeClassroomSessionId) startClassroomChatPolling();
}

function startClassroomChatPolling() {
  if (!accessToken || !activeClassroomSessionId || !selectedClassroomChatTargetId || classroomChatPollTask)
    return;
  const version = ++classroomChatPollVersion;
  const sessionId = activeClassroomSessionId;
  const targetId = selectedClassroomChatTargetId;
  classroomChatPollTask = pollClassroomChat(version, sessionId, targetId).finally(() => {
    if (version === classroomChatPollVersion) classroomChatPollTask = null;
  });
}

async function pollClassroomChat(version, sessionId, targetId) {
  while (version === classroomChatPollVersion && accessToken &&
      activeClassroomSessionId === sessionId && selectedClassroomChatTargetId === targetId) {
    const controller = new AbortController();
    classroomChatPollController = controller;
    try {
      const query = new URLSearchParams({ targetId, sessionId, afterRevision: String(classroomChatRevision) });
      const page = await api("/api/classroom/chat?" + query.toString(), { signal: controller.signal });
      if (version !== classroomChatPollVersion) return;
      if (typeof page.sessionId !== "string" || page.sessionId.toLowerCase() !== sessionId.toLowerCase() ||
          page.targetId !== targetId || !Number.isSafeInteger(page.revision) ||
          page.revision < classroomChatRevision || !Array.isArray(page.messages) || page.messages.length > 200)
        throw new Error("聊天会话与当前课堂不匹配；请重新选择学生。");
      const validated = [];
      const eventIds = new Set();
      for (const item of page.messages) {
        const eventId = typeof item?.eventId === "string" ? item.eventId.toLowerCase() : "";
        const issuedUtc = Date.parse(item?.issuedUtc);
        const expiresUtc = Date.parse(item?.expiresUtc);
        if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(eventId) ||
            eventIds.has(eventId) || !["teacher", "student"].includes(item?.sender) ||
            typeof item?.message !== "string" || item.message.length < 1 || item.message.length > 500 ||
            /[\u0000-\u0009\u000b-\u001f\u007f]/.test(item.message) ||
            !Number.isFinite(issuedUtc) || !Number.isFinite(expiresUtc) || expiresUtc <= issuedUtc ||
            typeof item?.delivered !== "boolean")
          throw new Error("聊天消息格式无效；请重新打开会话。");
        eventIds.add(eventId);
        validated.push({ eventId, sender: item.sender, message: item.message,
          issuedUtc: item.issuedUtc, expiresUtc: item.expiresUtc, delivered: item.delivered });
      }
      classroomChatRevision = page.revision;
      classroomChatMessages = validated;
      classroomChatStatusText = "会话已连接，双方消息会自动同步。";
      renderClassroomChat();
    } catch (error) {
      if (version !== classroomChatPollVersion || error.name === "AbortError") return;
      classroomChatStatusText = error.status === 401
        ? "聊天授权暂不可用；请刷新课堂或重新连接教师电脑。"
        : error.status === 403 || error.status === 409
          ? error.message
          : "聊天连接暂时中断，正在自动重试。";
      renderClassroomChat();
      await delay(3000);
    } finally {
      if (classroomChatPollController === controller) classroomChatPollController = null;
    }
  }
}

function renderClassroomChatTargets() {
  const select = $("classroom-chat-target");
  if (!select) return;
  const selected = selectedClassroomChatTargetId;
  select.replaceChildren();
  const placeholder = document.createElement("option");
  placeholder.value = "";
  placeholder.textContent = activeClassroomChatTargets.length ? "选择一名学生" : "暂无可聊天学生";
  select.append(placeholder);
  for (const target of activeClassroomChatTargets) {
    const option = document.createElement("option");
    option.value = target.targetId;
    const seat = target.seatNumber ? `座位 ${target.seatNumber} · ` : "";
    option.textContent = seat + classroomChatTargetLabel(target);
    select.append(option);
  }
  select.value = selected && activeClassroomChatTargets.some(target => target.targetId === selected)
    ? selected : "";
  select.disabled = !activeClassroomSessionId || activeClassroomChatTargets.length === 0;
}

function renderClassroomChat() {
  const panel = $("classroom-chat-panel");
  if (!panel) return;
  panel.classList.toggle("hidden", !activeClassroomSessionId);
  const target = activeClassroomChatTargets.find(item => item.targetId === selectedClassroomChatTargetId) || null;
  const state = $("classroom-chat-state");
  const detail = $("classroom-chat-target-detail");
  const list = $("classroom-chat-messages");
  const input = $("classroom-chat-input");
  const button = $("send-classroom-chat");
  if (!list || !input || !button) return;

  state.textContent = target ? "单人会话" : activeClassroomChatTargets.length ? "未选择学生" : "暂不可用";
  if (target) {
    const location = target.seatNumber ? `座位 ${target.seatNumber}` : "当前课堂";
    const grid = target.row && target.column ? ` · 第 ${target.row} 排第 ${target.column} 列` : "";
    detail.textContent = location + grid;
    detail.classList.remove("hidden");
  } else {
    detail.textContent = "";
    detail.classList.add("hidden");
  }

  const wasNearBottom = list.scrollHeight - list.scrollTop - list.clientHeight < 48;
  list.replaceChildren();
  if (!activeClassroomSessionId) {
    appendParagraph(list, "等待教师端配置课堂地点和学生名单。");
  } else if (!activeClassroomChatTargets.length) {
    appendParagraph(list, "暂无已确认支持聊天的学生端。请更新或重新连接学生端后刷新课堂。");
  } else if (!target) {
    appendParagraph(list, "选择学生后显示该电脑最近的会话消息。");
  } else if (!classroomChatMessages.length) {
    appendParagraph(list, "会话已打开，可以发送第一条消息。");
  } else {
    for (const message of classroomChatMessages) {
      const bubble = document.createElement("article");
      bubble.className = "classroom-chat-message " + message.sender;
      const meta = document.createElement("div");
      meta.className = "classroom-chat-message-meta";
      const speaker = document.createElement("strong");
      speaker.textContent = message.sender === "teacher" ? "老师" : classroomChatTargetLabel(target);
      const time = document.createElement("span");
      time.textContent = formatTimeOfDay(message.issuedUtc);
      meta.append(speaker, time);
      const body = document.createElement("p");
      body.className = "classroom-chat-message-body";
      body.textContent = message.message;
      bubble.append(meta, body);
      if (message.sender === "teacher") {
        const expired = Date.parse(message.expiresUtc) <= Date.now();
        const delivery = document.createElement("span");
        delivery.className = "classroom-chat-delivery";
        delivery.textContent = message.delivered ? "学生端已收到" : expired ? "未收到回执" : "等待学生端接收";
        bubble.append(delivery);
        if (expired && !message.delivered) {
          const retry = document.createElement("button");
          retry.className = "secondary-button classroom-chat-retry";
          retry.type = "button";
          retry.textContent = "重试发送";
          retry.disabled = classroomChatSending;
          retry.addEventListener("click", () =>
            void transmitClassroomChat(target.targetId, message.message, message.eventId, false));
          bubble.append(retry);
        }
      }
      list.append(bubble);
    }
  }
  if (wasNearBottom) list.scrollTop = list.scrollHeight;
  scheduleClassroomChatExpiryRender();
  input.disabled = !target || classroomChatSending;
  button.disabled = !target || classroomChatSending || !input.value.trim();
  const sendLabel = classroomChatDraftEventId ? "重试发送" : "发送消息";
  button.querySelector("span").textContent = sendLabel;
  button.setAttribute("aria-label", sendLabel);
  $("classroom-chat-target").disabled = !activeClassroomSessionId || !activeClassroomChatTargets.length || classroomChatSending;
  $("classroom-chat-selected-name").textContent = target ? classroomChatTargetLabel(target) : "请选择学生";
  $("mobile-messages-open").disabled = !accessToken || !activeClassroomSessionId;
  $("classroom-chat-status").textContent = target
    ? classroomChatStatusText || "消息只保存在本堂课期间。"
    : activeClassroomChatTargets.length ? "消息只保存在本堂课期间。" : "";
}

function scheduleClassroomChatExpiryRender() {
  clearTimeout(classroomChatExpiryTimer);
  classroomChatExpiryTimer = 0;
  const now = Date.now();
  const nextExpiry = classroomChatMessages
    .filter(message => message.sender === "teacher" && !message.delivered && Date.parse(message.expiresUtc) > now)
    .map(message => Date.parse(message.expiresUtc))
    .sort((left, right) => left - right)[0];
  if (nextExpiry) classroomChatExpiryTimer = setTimeout(() => {
    classroomChatExpiryTimer = 0;
    renderClassroomChat();
  }, Math.max(1, nextExpiry - now + 1));
}

async function transmitClassroomChat(targetId, message, eventId, clearDraft) {
  if (!targetId || !activeClassroomSessionId || !message || !eventId || classroomChatSending) return;
  const selectedAtStart = selectedClassroomChatTargetId === targetId;
  if (selectedAtStart) {
    classroomChatSending = true;
    classroomChatStatusText = "正在发送…";
    renderClassroomChat();
  }
  try {
    await api("/api/classroom/chat", {
      method: "POST",
      body: { sessionId: activeClassroomSessionId, targetId, eventId, message }
    });
    if (selectedAtStart && selectedClassroomChatTargetId === targetId) {
      classroomChatStatusText = "消息已提交，等待学生端接收确认。";
      if (clearDraft && classroomChatDraftEventId === eventId) {
        $("classroom-chat-input").value = "";
        classroomChatDraftEventId = null;
        classroomChatDraftMessage = null;
      }
    }
  } catch (error) {
    if (selectedAtStart && selectedClassroomChatTargetId === targetId) {
      if (clearDraft) {
        classroomChatDraftEventId = eventId;
        classroomChatDraftMessage = message;
      }
      classroomChatStatusText = error.message || "消息发送失败；请重试。";
    }
  } finally {
    if (selectedAtStart && selectedClassroomChatTargetId === targetId) {
      classroomChatSending = false;
      renderClassroomChat();
    }
  }
}

function sendCurrentClassroomChatMessage() {
  const input = $("classroom-chat-input");
  const targetId = selectedClassroomChatTargetId;
  const message = input.value.replace(/\r\n?/g, "\n").trim();
  if (!message || !targetId) return;
  const eventId = classroomChatDraftEventId || createClassroomChatEventId();
  void transmitClassroomChat(targetId, message, eventId, true);
}

function createClassroomChatEventId() {
  if (typeof crypto.randomUUID === "function") return crypto.randomUUID();
  const hex = createRequestNonce();
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-4${hex.slice(13, 16)}-a${hex.slice(17, 20)}-${hex.slice(20)}`;
}

async function refreshStatusIfSelected() {
  const targets = selectedTargets();
  const requestId = ++statusReadGeneration;
  if (!targets.length) {
    statusList.replaceChildren();
    const empty = document.createElement("p");
    empty.className = "muted";
    empty.textContent = "选择一台电脑后查看当前限制状态。";
    statusList.append(empty);
    return;
  }
  const requestedTargets = [...targets];
  for (const target of requestedTargets) latestStatusByTarget.delete(normalizeTarget(target));
  updateSelectionCount();
  $("refresh-status").disabled = true;
  statusList.replaceChildren();
  const loading = document.createElement("p");
  loading.className = "muted";
  loading.textContent = "正在读取学生电脑状态……";
  statusList.append(loading);
  try {
    const statuses = await api("/api/status", {
      method: "POST",
      body: { targets: requestedTargets }
    });
    if (requestId !== statusReadGeneration || !sameTargets(requestedTargets, selectedTargets())) return;
    renderStatuses(statuses);
  } catch (error) {
    if (requestId !== statusReadGeneration || !sameTargets(requestedTargets, selectedTargets())) return;
    statusList.replaceChildren();
    const card = document.createElement("article");
    card.className = "result-card";
    const title = document.createElement("h3");
    title.textContent = requestedTargets.length === 1
      ? targetDisplayName(requestedTargets[0])
      : "所选电脑";
    card.append(title);
    const state = document.createElement("span");
    state.className = "state bad";
    state.textContent = "当前限制状态未知";
    card.append(state);
    appendParagraph(card, error?.message || "暂时无法读取电脑状态，请检查连接后重试。");
    statusList.append(card);
    for (const target of requestedTargets) latestStatusByTarget.delete(normalizeTarget(target));
    updateSelectionCount();
  } finally {
    if (requestId === statusReadGeneration) {
      $("refresh-status").disabled = false;
      updateSelectionCount();
    }
  }
}

function mobilePolicyErrorMessage(error, action) {
  if (error?.status === 401 && error?.pairingRevoked)
    return "手机连接已失效，请重新连接教师电脑后重试。";
  if (error?.status === 401 || error?.status === 403)
    return `${action}失败：${error?.message || "请检查手机配对、校园网和请求来源后重试。"}`;
  if (error?.status === 409 || error?.status === 400)
    return `${action}失败：${error.message || "请刷新后重试。"}`;
  if (error?.status === 429) return "操作较频繁，请稍后重试。";
  if (error?.status === 501) return error.message || "教师电脑暂不支持此操作。";
  if (error?.status === 503) return `${action}失败：${error.message || "教师端签名密钥暂不可用，请检查教师电脑状态后重试。"}`;
  if (error?.status >= 500) return `${action}失败：${error.message || "教师电脑暂时无法处理，请稍后重试。"}`;
  return `${action}暂时无法完成，请确认教师电脑和学生电脑连接正常后重试。`;
}

function savedPolicyConfirmationText(profile, targets, enabled) {
  const targetNames = targets.map(targetDisplayName).join("、");
  const action = enabled ? "发送" : "解除";
  const warning = profile.kind === "system" && enabled
    ? "\n这是长期系统限制，不会自动到期；之后需由教师明确解除。"
    : (profile.kind === "application" || profile.kind === "websiteApplication") &&
      String(profile.mode || "").toLowerCase().includes("enforce") && enabled
      ? "\n应用阻止会先进行影响检查；未查看并二次确认前不会启用。"
      : "";
  return `确认${action}“${profile.name}”？\n类型：${mobilePolicyKindLabel(profile.kind)} · ${mobilePolicyModeLabel(profile.mode, profile.kind)}\n目标电脑：${targetNames}\n${profile.lifetimeMinutes === 0 ? "期限：不会自动到期" : `期限：${profile.lifetimeMinutes} 分钟`}${warning}\n\n当前限制：\n${targetConfirmationSummary(targets)}`;
}

function savedPolicyReviewContextStillCurrent(review) {
  return Boolean(review && selectedSavedPolicyProfile()?.id === review.profileId &&
    sameTargets(review.targets, selectedTargets()));
}

function cancelPendingSavedPolicyReview() {
  if (!pendingSavedPolicyReview) return;
  pendingSavedPolicyReview = null;
  policyOperationInFlight = false;
  $("saved-policy-profiles-status").textContent =
    "本次应用限制未确认，因此没有启用；未使用的审核票据会自动过期。";
  document.querySelector("#saved-policy-result .application-review-actions")?.remove();
  updateSelectionCount();
}

async function submitSavedPolicyProfile(profile, targets, enabled, reviewToken = null) {
  const body = { profileId: profile.id, targets, enabled };
  if (reviewToken) body.reviewToken = reviewToken;
  else {
    const expected = expectedRevisionFieldsForProfile(targets, profile);
    if (!expected) throw new Error("电脑策略版本已变化；请刷新所选电脑状态后重试。");
    Object.assign(body, expected);
  }
  const result = await api("/api/policy", { method: "POST", body });
  if (!result || !Array.isArray(result.results))
    throw new Error("教师端返回的逐台策略结果格式无效。");
  if (result.requiresReview) {
    if (typeof result.reviewToken !== "string" || !result.applicationReview?.length)
      throw new Error("应用影响检查没有返回可核对的逐台结果；尚未启用应用限制。");
    pendingSavedPolicyReview = { profileId: profile.id, targets: [...targets], reviewToken: result.reviewToken };
    $("saved-policy-profiles-status").textContent =
      "应用影响检查已返回。请逐台查看下方结果，再确认是否启用；确认前不会启用应用阻止。";
    renderOperation(result, $("saved-policy-result"), {
      confirm: confirmPendingSavedPolicyReview,
      cancel: cancelPendingSavedPolicyReview
    });
    return { reviewPending: true, result };
  }
  renderOperation(result, $("saved-policy-result"));
  $("saved-policy-profiles-status").textContent = result.message ||
    "策略请求已处理；请查看每台学生电脑的回执。";
  return { reviewPending: false, result };
}

async function runSavedPolicyProfile(enabled) {
  if (policyOperationInFlight || pendingSavedPolicyReview) return;
  const profile = selectedSavedPolicyProfile();
  const targets = [...selectedTargets()];
  if (!profile || !targets.length) return;
  if (!savedPolicyProfileStatusReady(targets, profile)) {
    $("saved-policy-scope-status").textContent = savedPolicyProfileReadinessMessage(targets, profile);
    await refreshStatusIfSelected();
    return;
  }
  if (!expectedRevisionFieldsForProfile(targets, profile)) {
    $("saved-policy-profiles-status").textContent = "电脑策略版本已变化，正在重新读取状态…";
    await refreshStatusIfSelected();
    return;
  }
  if (!window.confirm(savedPolicyConfirmationText(profile, targets, enabled))) return;

  policyOperationInFlight = true;
  updateSelectionCount();
  $("saved-policy-result").replaceChildren();
  $("saved-policy-profiles-status").textContent = "正在向教师端发送策略预设…";
  let keepReviewPending = false;
  try {
    const submitted = await submitSavedPolicyProfile(profile, targets, enabled);
    keepReviewPending = submitted.reviewPending;
  } catch (error) {
    $("saved-policy-profiles-status").textContent = mobilePolicyErrorMessage(error, "策略预设");
    renderOperation({ message: mobilePolicyErrorMessage(error, "策略预设"), results: [] },
      $("saved-policy-result"));
    toast(mobilePolicyErrorMessage(error, "策略预设"));
  } finally {
    if (!keepReviewPending) {
      policyOperationInFlight = false;
      for (const target of targets) latestStatusByTarget.delete(normalizeTarget(target));
      await refreshStatusIfSelected();
    }
    updateSelectionCount();
  }
}

async function confirmPendingSavedPolicyReview() {
  const review = pendingSavedPolicyReview;
  if (!review || policyOperationInFlight === false) return;
  if (!savedPolicyReviewContextStillCurrent(review)) {
    cancelPendingSavedPolicyReview();
    toast("所选电脑或策略已变化；没有启用应用限制，请重新检查。");
    return;
  }
  if (!window.confirm("请确认已查看每台电脑的应用影响预览。继续后，教师端会再次核对预设、状态和影响结果，再发送应用阻止策略。"))
    return;

  document.querySelectorAll("#saved-policy-result .application-review-actions button")
    .forEach(button => { button.disabled = true; });
  const profile = savedPolicyProfiles.find(item => item.id === review.profileId);
  if (!profile) {
    cancelPendingSavedPolicyReview();
    return;
  }
  $("saved-policy-profiles-status").textContent = "正在重新核对影响结果并确认应用限制…";
  try {
    pendingSavedPolicyReview = null;
    const submitted = await submitSavedPolicyProfile(profile, review.targets, true, review.reviewToken);
    if (submitted.reviewPending)
      throw new Error("教师端再次要求影响检查；请重新核对下方结果。");
  } catch (error) {
    pendingSavedPolicyReview = null;
    const message = mobilePolicyErrorMessage(error, "确认应用限制");
    $("saved-policy-profiles-status").textContent = message;
    renderOperation({ message, results: [] }, $("saved-policy-result"));
    toast(message);
  } finally {
    pendingSavedPolicyReview = null;
    policyOperationInFlight = false;
    for (const target of review.targets) latestStatusByTarget.delete(normalizeTarget(target));
    await refreshStatusIfSelected();
    updateSelectionCount();
  }
}

async function runCustomWebsitePolicy(enabled) {
  const targets = selectedTargets();
  if (!targets.length) return;
  if (enabled && websiteListsAvailable) {
    const input = customWebsitePolicyInput();
    if (!input.valid) {
      toast(customWebsitePolicyInputError(input));
      return;
    }
  }
  if (enabled) {
    if (websiteListsDirty()) {
      if (!await saveCustomWebsiteLists(false)) return;
    } else if (!await refreshCustomWebsiteLists(false)) {
      return;
    }
  }
  if (enabled && !websiteListsAvailable) {
    toast("还没有读取到教师端名单，请刷新后再试。");
    return;
  }
  const expected = expectedRevisionFields(targets);
  if (!expected) {
    toast("电脑状态有变化，请刷新状态后重试。");
    await refreshStatusIfSelected();
    return;
  }
  const input = customWebsitePolicyInput();
  if (enabled && !input.valid) {
    toast(customWebsitePolicyInputError(input));
    return;
  }
  const targetNames = targets.map(targetDisplayName).join("、");
  const effectiveListsSummary = input.mode === "allowlist"
    ? `允许 ${input.allowedDomains.length} 个域名，其他网站会被阻止`
    : input.mode === "combined"
      ? `阻止 ${input.blockedDomains.length} 个域名，允许 ${input.allowedDomains.length} 个例外`
      : `阻止 ${input.blockedDomains.length} 个域名，其他网站仍可访问`;
  const message = enabled
    ? `确认推送“${websiteModeLabel(input.mode)}”？\n目标电脑：${targetNames}\n${effectiveListsSummary}\n截止时间：${input.deadlineText}`
    : `确认解除以下学生的网站限制？\n${targetNames}`;
  const confirmation = message + "\n当前限制：\n" + targetConfirmationSummary(targets);
  if (!window.confirm(confirmation)) return;
  const body = {
    targets,
    enabled,
    mode: input.mode,
    expiresUtc: enabled ? input.expiresUtc : null,
    blockedDomains: input.blockedDomains,
    allowedDomains: input.allowedDomains,
    ...expected
  };
  policyOperationInFlight = true;
  updateSelectionCount();
  operationResult.replaceChildren();
  try {
    renderOperation(await api("/api/policy/custom-website", { method: "POST", body }));
  } catch (error) {
    toast(mobilePolicyErrorMessage(error, "网站限制"));
  } finally {
    for (const target of targets) latestStatusByTarget.delete(normalizeTarget(target));
    await refreshStatusIfSelected();
    policyOperationInFlight = false;
    updateSelectionCount();
  }
}

function customWebsitePolicyInputError(input) {
  if (input.domainValidationError) return `请修正名单中的域名格式。${input.domainValidationError}`;
  if (!input.hasRequiredDomains) return input.mode === "allowlist"
    ? "请先填写至少一个允许访问的域名。"
    : input.mode === "combined"
      ? "请填写要阻止的网站，或填写允许访问的例外网站。"
      : "请先填写至少一个要阻止的域名。";
  return "请设置未来 24 小时内的限制截止时间。";
}

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

async function waitForPairingApproval(ticket) {
  while (true) {
    const result = await api("/api/pair-status", { method: "POST", body: { ticket } });
    if (result.state === "approved" && result.accessToken) return result;
    if (result.state === "rejected") throw new Error("教师没有允许这部手机连接，请重新申请或联系教师。");
    if (result.state === "expired") throw new Error("连接请求已过期，请让教师重新生成连接码。");
    await delay(1100);
  }
}

async function pair(event) {
  event.preventDefault();
  const code = $("pair-code").value.trim();
  const deviceName = $("device-name").value.trim();
  const button = $("pair-form").querySelector("button[type=submit]");
  button.disabled = true;
  button.textContent = "等待教师允许…";
  try {
    const request = await api("/api/pair", {
      method: "POST",
      body: { pairingCode: code, deviceName }
    });
    toast("申请已发送，请教师在电脑上允许这部手机连接。");
    const result = await waitForPairingApproval(request.ticket);
    await saveToken(result.accessToken).catch(() => {});
    accessToken = result.accessToken;
    $("pair-code").value = "";
    if (await connectDashboard()) toast("手机已连接教师电脑。");
  } catch (error) {
    toast(error.message);
  } finally {
    button.disabled = false;
    button.textContent = "申请连接";
  }
}

async function leaveControlPage() {
  singleTargetPreviewStartController?.abort();
  await stopClassroomScreenPreview(false);
  stopClassroomEventPolling();
  setControlVisibility(false);
  pairView.classList.remove("hidden");
  $("logout-button").classList.add("hidden");
  $("pair-form").classList.add("hidden");
  $("saved-session").classList.remove("hidden");
}

async function resumeSavedSession() {
  try {
    if (!accessToken) accessToken = await readToken().catch(() => null);
    if (!accessToken) accessToken = await resumeTokenFromCookie();
    if (!accessToken) {
      $("saved-session").classList.add("hidden");
      $("pair-form").classList.remove("hidden");
      $("boot-view").classList.add("hidden");
      pairView.classList.remove("hidden");
      showScannedPairingInvite();
      return;
    }
    await connectDashboard();
  } catch (error) {
    $("saved-session-status").textContent = "无法确认连接状态，请检查教师电脑是否已启动手机控制后重试。";
    $("resume-control-button").textContent = "重新连接";
    toast("无法恢复手机连接，请检查教师电脑后重试。");
  }
}

async function forgetMobileSession() {
  singleTargetPreviewStartController?.abort();
  await stopClassroomScreenPreview(false);
  stopClassroomEventPolling();
  const tokenToForget = accessToken || await readToken().catch(() => null);
  accessToken = null;
  await clearToken().catch(() => {});
  let cookieCleared = false;
  try {
    const headers = new Headers({ "Content-Type": "application/json" });
    if (tokenToForget) {
      headers.set("Authorization", "Bearer " + tokenToForget);
      headers.set("X-Veyon-Request-Nonce", createRequestNonce());
      headers.set("X-Veyon-Request-Timestamp", new Date(Date.now() + serverClockOffsetMs).toISOString());
    }
    const response = await fetch("/api/mobile-forget", {
      method: "POST",
      credentials: "same-origin",
      headers,
      body: "{}",
      cache: "no-store",
      redirect: "error",
      referrerPolicy: "no-referrer"
    });
    cookieCleared = response.ok;
  } catch { }
  setControlVisibility(false);
  pairView.classList.remove("hidden");
  $("boot-view").classList.add("hidden");
  $("logout-button").classList.add("hidden");
  $("saved-session").classList.add("hidden");
  $("pair-form").classList.remove("hidden");
  showScannedPairingInvite();
  toast(cookieCleared
    ? "旧连接已清除。请向教师获取连接码，再申请重新配对。"
    : "本机连接记录已清除。请确认已打开对应教师连接页，再重新配对。");
}

function showScannedPairingInvite() {
  if (!scannedPairingCode) return;
  $("pair-code").value = scannedPairingCode;
  const status = $("pair-invite-status");
  status.textContent = "已读取连接码，点击“申请连接”，再请教师在电脑上允许。";
  status.classList.remove("hidden");
}

function setAll(checked) {
  for (const input of roomList.querySelectorAll("input[data-target]:not(:disabled)")) input.checked = checked;
  $("toggle-all").textContent = checked ? "取消全选" : "全选";
  updateSelectionCount();
  void refreshStatusIfSelected();
}

$("pair-form").addEventListener("submit", pair);
$("refresh-rooms").addEventListener("click", async () => {
  try {
    rooms = await api("/api/rooms");
    renderRooms(true);
    latestStatusByTarget.clear();
    await refreshStatusIfSelected();
    toast("学生电脑名单已刷新。");
  } catch { toast("暂时无法刷新学生电脑名单，请稍后重试。"); }
});
$("refresh-computer-directory").addEventListener("click", async () => {
  const button = $("refresh-computer-directory");
  button.disabled = true;
  try {
    rooms = await api("/api/rooms");
    renderRooms(true);
    const targetStillListed = !selectedComputerTarget || rooms.some(room =>
      (room.targets || []).some(target => normalizeTarget(target) === normalizeTarget(selectedComputerTarget)));
    const targetIsDuplicate = selectedComputerTarget &&
      duplicateDirectoryTargets().has(normalizeTarget(selectedComputerTarget));
    const selectedRoom = selectedComputerTarget && rooms.find(room => (room.targets || []).some(target =>
      normalizeTarget(target) === normalizeTarget(selectedComputerTarget)));
    if (policyPageMode === "single" && (!targetStillListed || targetIsDuplicate)) {
      const removedTarget = selectedComputerTarget;
      for (const input of roomList.querySelectorAll("input[data-target]")) input.checked = false;
      latestStatusByTarget.delete(normalizeTarget(removedTarget));
      selectedComputerTarget = null;
      setPolicyPageMode("directory");
      toast(targetIsDuplicate
        ? "这台电脑出现在多个地点，限制已停用；请先修正电脑名单。"
        : "这台电脑已从名单移除，限制已停用。");
    } else {
      if (policyPageMode === "single" && selectedComputerTarget) {
        $("student-detail-name").textContent = roomTargetDisplayName(selectedRoom, selectedComputerTarget);
        latestStatusByTarget.delete(normalizeTarget(selectedComputerTarget));
        void refreshStatusIfSelected();
      }
      toast("电脑名单已刷新。");
    }
  } catch { toast("暂时无法刷新电脑名单，请稍后重试。"); }
  finally { button.disabled = false; }
});
$("directory-room-select").addEventListener("change", () => {
  const room = rooms[Number($("directory-room-select").value)];
  selectedDirectoryRoomName = room?.name || "未命名地点";
  rememberDirectoryRoomName(selectedDirectoryRoomName);
  selectedComputerTarget = null;
  statusReadGeneration++;
  latestStatusByTarget.clear();
  renderComputerDirectory();
});
$("open-bulk-policy").addEventListener("click", () => {
  computerDirectoryScrollY = window.scrollY;
  selectedComputerTarget = null;
  setPolicyPageMode("bulk");
  void refreshStatusIfSelected();
});
$("back-to-computer-directory").addEventListener("click", returnToComputerDirectory);
$("back-to-computer-directory-from-bulk").addEventListener("click", returnToComputerDirectory);
$("target-picker").addEventListener("toggle", () => {
  if (policyPageMode === "single" && $("target-picker").open) {
    selectedComputerTarget = null;
    setPolicyPageMode("bulk");
  }
});
$("refresh-status").addEventListener("click", refreshStatusIfSelected);
$("refresh-custom-website-lists").addEventListener("click", () => void refreshCustomWebsiteLists());
$("refresh-saved-policy-profiles").addEventListener("click", () => void refreshSavedPolicyProfiles());
$("saved-policy-profile").addEventListener("change", () => {
  $("saved-policy-result").replaceChildren();
  updateSelectionCount();
});
$("apply-saved-policy-profile").addEventListener("click", () => void runSavedPolicyProfile(true));
$("disable-saved-policy-profile").addEventListener("click", () => void runSavedPolicyProfile(false));
$("save-custom-website-lists").addEventListener("click", () => void saveCustomWebsiteLists());
$("apply-custom-website-policy").addEventListener("click", () => void runCustomWebsitePolicy(true));
$("disable-custom-website-policy").addEventListener("click", () => void runCustomWebsitePolicy(false));
$("custom-website-mode").addEventListener("change", () => {
  updateCustomWebsiteModeGuidance();
  updateWebsiteListSaveButton();
  updateSelectionCount();
  if (websiteListsDirty())
    $("custom-website-lists-status").textContent = "手机上有尚未保存的模式或名单修改；推送时会先自动保存。";
});
initializeCustomWebsiteDeadline();
updateCustomWebsiteModeGuidance();
for (const id of ["custom-website-blocked", "custom-website-allowed", "custom-website-deadline-date", "custom-website-deadline-time"])
  for (const eventName of ["input", "change"]) {
    $(id).addEventListener(eventName, updateSelectionCount);
    if (id === "custom-website-blocked" || id === "custom-website-allowed") $(id).addEventListener(eventName, () => {
      updateWebsiteListSaveButton();
      if (websiteListsDirty())
        $("custom-website-lists-status").textContent = "手机上有尚未保存的名单修改；推送时会先自动保存。";
    });
  }
$("logout-button").addEventListener("click", leaveControlPage);
$("resume-control-button").addEventListener("click", resumeSavedSession);
$("forget-mobile-session").addEventListener("click", forgetMobileSession);
$("reconnect-button").addEventListener("click", () => void connectDashboard());
$("classroom-tab-button").addEventListener("click", () => setActiveControlTab("classroom"));
$("policy-tab-button").addEventListener("click", () => setActiveControlTab("policy"));
$("monitor-tab-button").addEventListener("click", () => setActiveControlTab("monitor"));
function openMobileMessages() {
  if (!activeClassroomSessionId) return;
  const dialog = $("mobile-messages-dialog");
  if (dialog.open) return;
  if (classroomScreenPreviewLease) void stopClassroomScreenPreview(false);
  dialog.showModal();
  document.body.classList.add("messages-open");
  $("classroom-chat-target").focus();
}
function closeMobileMessages() {
  const dialog = $("mobile-messages-dialog");
  if (dialog.open) dialog.close();
}
$("mobile-messages-open").addEventListener("click", openMobileMessages);
$("mobile-messages-close").addEventListener("click", closeMobileMessages);
$("mobile-messages-dialog").addEventListener("close", () => {
  document.body.classList.remove("messages-open");
  $("mobile-messages-open").focus();
});
$("directory-search").addEventListener("input", renderComputerDirectory);
$("settings-tab-button").addEventListener("click", () => setActiveControlTab("settings"));
$("monitor-go-classroom").addEventListener("click", () => setActiveControlTab("classroom"));
$("monitor-preview-quality").addEventListener("change", () => void updateClassroomScreenPreviewSettings());
$("monitor-preview-refresh").addEventListener("change", () => void updateClassroomScreenPreviewSettings());
$("send-classroom-notice").addEventListener("click", sendClassroomNotice);
$("classroom-chat-target").addEventListener("change", event =>
  selectClassroomChatTarget(event.currentTarget.value));
$("classroom-chat-input").addEventListener("input", event => {
  const currentMessage = event.currentTarget.value.replace(/\r\n?/g, "\n").trim();
  if (classroomChatDraftEventId && currentMessage !== classroomChatDraftMessage) {
    classroomChatDraftEventId = null;
    classroomChatDraftMessage = null;
  }
  renderClassroomChat();
});
$("classroom-chat-input").addEventListener("keydown", event => {
  if (event.key !== "Enter" || event.shiftKey || event.isComposing || event.keyCode === 229) return;
  event.preventDefault();
  sendCurrentClassroomChatMessage();
});
$("send-classroom-chat").addEventListener("click", sendCurrentClassroomChatMessage);
$("monitor-preview-toggle").addEventListener("click", () => void startClassroomScreenPreview());
$("monitor-preview-pause").addEventListener("click", () => void (classroomScreenPreviewPaused
  ? resumeClassroomScreenPreview() : pauseClassroomScreenPreview()));
$("classroom-quick-control-website").addEventListener("click", () => void runClassroomQuickControl("enableWebsiteRestriction"));
$("classroom-quick-control-input").addEventListener("click", () => {
  const devices = classroomQuickControlSnapshot?.sessionId === activeClassroomSessionId
    ? classroomQuickControlSnapshot.devices || [] : [];
  const locked = devices.some(item => item.inputState === "locked" || item.inputLeaseId ||
    (!item.inputState && item.phase === "inputLockedVerified"));
  void runClassroomQuickControl(locked ? "unlockInput" : "lockInput");
});
$("classroom-quick-control-clear-websites").addEventListener("click", () => void runClassroomQuickControl("clearWebsiteRestrictions"));
$("classroom-quick-control-release").addEventListener("click", () => void runClassroomQuickControl("release"));
$("retry-classroom-restores").addEventListener("click", retryClassroomRestores);
$("toggle-all").addEventListener("click", () => {
  const inputs = Array.from(roomList.querySelectorAll("input[data-target]"));
  setAll(!inputs.length || inputs.some(input => !input.checked));
});
document.addEventListener("visibilitychange", () => {
  if (document.hidden) {
    classroomScreenPreviewStartController?.abort();
    singleTargetPreviewStartController?.abort();
    if (classroomScreenPreviewLease || classroomScreenPreviewStartController)
      void stopClassroomScreenPreview(true);
  } else if (accessToken && !$("reconnect-button").classList.contains("hidden")) {
    void connectDashboard();
  }
});
window.addEventListener("online", () => {
  if (accessToken && !$("reconnect-button").classList.contains("hidden"))
    void connectDashboard();
});
window.addEventListener("pageshow", event => {
  if (event.persisted && accessToken) void connectDashboard();
});
window.addEventListener("pagehide", () => {
  classroomScreenPreviewStartController?.abort();
  singleTargetPreviewStartController?.abort();
  stopClassroomChatPolling();
  if (classroomScreenPreviewLease || classroomScreenPreviewStartController)
    void stopClassroomScreenPreview(false);
});

async function start() {
  if (!window.isSecureContext || !window.crypto?.getRandomValues) {
    $("boot-view").classList.add("hidden");
    pairView.classList.remove("hidden");
    toast("请从连接说明页点“打开安全连接”后再使用本页。");
    return;
  }
  if ("serviceWorker" in navigator) {
    let hadController = Boolean(navigator.serviceWorker.controller);
    navigator.serviceWorker.addEventListener("controllerchange", () => {
      if (!hadController) return;
      hadController = false;
      window.location.reload();
    });
    navigator.serviceWorker.register("/service-worker.js", { updateViaCache: "none" })
      .then(registration => registration.update())
      .catch(() => {});
  }
  let tokenReadFailed = false;
  try {
    accessToken = await readToken();
  } catch { tokenReadFailed = true; }
  if (!accessToken) {
    try { accessToken = await resumeTokenFromCookie(); }
    catch {
      $("boot-view").classList.add("hidden");
      pairView.classList.remove("hidden");
      $("pair-form").classList.add("hidden");
      $("saved-session").classList.remove("hidden");
      $("saved-session-status").textContent = "无法确认连接状态，请检查教师电脑是否已启动手机控制后重试。";
      $("resume-control-button").textContent = "重新连接";
      return;
    }
  }
  if (accessToken) {
    $("boot-view").classList.add("hidden");
    await connectDashboard();
    if (scannedPairingCode) toast("此浏览器已连接。退出后可继续使用刚扫描的连接码。");
    return;
  }
  $("boot-view").classList.add("hidden");
  pairView.classList.remove("hidden");
  if (tokenReadFailed) toast("没有读取到手机连接记录，可以重新申请。连接后可从此页面添加桌面图标，方便下次使用。");
  showScannedPairingInvite();
}

start();
