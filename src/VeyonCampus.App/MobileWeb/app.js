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
let serverClockOffsetMs = 0;
let rooms = [];
let profiles = [];
let activeClassroomTargets = [];
let activeClassroomSessionId = null;
let activeClassroomSeatLocations = new Map();
let activeClassroomCountdownDeadline = null;
let monitorSelectedTargets = new Set();
let classroomScreenPreviewLease = null;
let classroomScreenPreviewMode = "screenshot";
let classroomScreenPreviewTargets = [];
let classroomScreenPreviewStartController = null;
let veyonRemoteViewPending = false;
let classroomScreenPreviewObserver = null;
const classroomScreenPreviewVisibleTargets = new Set();
const classroomScreenPreviewControllers = new Map();
const classroomScreenPreviewTimers = new Map();
const classroomScreenPreviewObjectUrls = new Map();
let classroomTargetDefaultState = "unavailable";
let pendingReview = null;
let lastOperation = null;
let policyOperationInFlight = false;
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
  else if (classroomScreenPreviewLease) void stopClassroomScreenPreview(false);
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

async function resumeTokenFromCookie() {
  const response = await fetch("/api/mobile-resume", {
    method: "POST",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json" },
    body: "{}"
  });
  if (response.status === 401) return null;
  if (!response.ok) throw new Error("教师端暂时无法恢复配对，请确认服务已启动后重试。");
  const result = await response.json();
  if (typeof result.accessToken !== "string" || result.accessToken.length < 32)
    throw new Error("教师端返回的配对状态无效。");
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
    const response = await fetch(path, {
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
  const button = $("monitor-preview-toggle");
  const status = $("classroom-screen-preview-status");
  if (classroomScreenPreviewLease) {
    await stopClassroomScreenPreview(true);
    return;
  }
  if (!activeClassroomSessionId || activeClassroomTargets.length === 0) {
    status.textContent = "当前没有启用学生互动。";
    return;
  }
  const requestedTargets = Array.from(monitorSelectedTargets)
    .filter(target => activeClassroomTargets.some(active => normalizeTarget(active) === target));
  if (requestedTargets.length === 0) {
    status.textContent = "请先选择要查看的学生电脑。";
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
      method: "POST", body: { targets: requestedTargets }, signal: startController.signal
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
      sameTargets(targets, requestedTargets) &&
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
      throw new Error("屏幕查看范围无效，请刷新学生互动状态后重试。");
    }

    classroomScreenPreviewLease = { id: result.leaseId, sessionId: result.sessionId };
    classroomScreenPreviewTargets = targets;
    renderClassroomScreenPreviewTiles(targets);
    button.textContent = "停止巡视";
    button.setAttribute("aria-expanded", "true");
    renderMonitorTargets();
    status.textContent = `正在查看 ${targets.length} 台电脑的屏幕；仅刷新手机上可见的画面。`;
    $("monitor-preview-guidance").textContent = "画面每 2 秒更新；停止预览、离开监控页或互动结束时会撤销查看授权。";
    observeClassroomScreenPreviewTiles();
  } catch (error) {
    if (error.name === "AbortError") return;
    if (classroomScreenPreviewLease) await stopClassroomScreenPreview(false);
    status.textContent = error.message || "屏幕巡视暂时无法开启。";
    button.textContent = "查看所选屏幕";
  } finally {
    if (classroomScreenPreviewStartController === startController)
      classroomScreenPreviewStartController = null;
    button.disabled = false;
    renderMonitorTargets();
  }
}

function selectedMonitorTargets() {
  return activeClassroomTargets.filter(target => monitorSelectedTargets.has(normalizeTarget(target)));
}

async function openSelectedVeyonView() {
  if (classroomScreenPreviewLease && classroomScreenPreviewMode === "veyon-window") {
    await stopClassroomScreenPreview(true);
    return;
  }
  if (classroomScreenPreviewLease) await stopClassroomScreenPreview(false);
  const targets = selectedMonitorTargets();
  const status = $("monitor-veyon-view-status");
  if (targets.length !== 1) {
    status.textContent = "请只选择 1 台电脑，再在手机查看 Veyon 实时画面。";
    toast(status.textContent);
    return;
  }
  if (veyonRemoteViewPending) return;

  veyonRemoteViewPending = true;
  const button = $("monitor-veyon-view-button");
  button.disabled = true;
  button.textContent = "正在连接 Veyon…";
  status.textContent = "正在教师电脑打开 Veyon，并连接手机实时画面。";
  try {
    const result = await api("/api/classroom/veyon-view/mobile/start", {
      method: "POST", body: { target: targets[0] }
    });
    if (typeof result?.target !== "string" || normalizeTarget(result.target) !== normalizeTarget(targets[0]))
      throw new Error("Veyon 返回的电脑与当前选择不一致，请刷新学生互动状态后重试。");
    if (typeof result?.leaseId !== "string" || typeof result?.sessionId !== "string")
      throw new Error("教师电脑未返回有效的实时监看授权，请刷新后重试。");
    classroomScreenPreviewLease = { id: result.leaseId, sessionId: result.sessionId };
    classroomScreenPreviewMode = "veyon-window";
    classroomScreenPreviewTargets = [targets[0]];
    renderClassroomScreenPreviewTiles(classroomScreenPreviewTargets);
    status.textContent = result.message || `正在把 ${targets[0]} 的 Veyon 画面传到手机。`;
    $("classroom-screen-preview-status").textContent = status.textContent;
    $("monitor-preview-guidance").textContent = "Veyon 窗口画面约每秒刷新；停止、离开监控页或互动结束时会关闭监看。";
    observeClassroomScreenPreviewTiles();
    toast(status.textContent);
  } catch (error) {
    status.textContent = error.message || "无法打开 Veyon 监看窗口。";
    toast(status.textContent);
  } finally {
    veyonRemoteViewPending = false;
    renderMonitorTargets();
  }
}

function renderMonitorTargets() {
  const active = Boolean(activeClassroomSessionId && activeClassroomTargets.length);
  $("monitor-empty").classList.toggle("hidden", active);
  $("monitor-content").classList.toggle("hidden", !active);
  $("monitor-active-count").textContent = `${activeClassroomTargets.length} 台`;
  const list = $("monitor-target-list");
  if (!list) return;
  list.replaceChildren();
  const search = normalizeTarget($("monitor-target-search")?.value);
  const matches = activeClassroomTargets.filter(target => !search || normalizeTarget(target).includes(search));
  const selectedCount = selectedMonitorTargets().length;
  const liveVeyonViewActive = Boolean(classroomScreenPreviewLease && classroomScreenPreviewMode === "veyon-window");
  $("monitor-selection-count").textContent = `已选 ${selectedCount} / 5 台`;
  $("monitor-select-visible").disabled = !matches.length || Boolean(classroomScreenPreviewLease) || veyonRemoteViewPending;
  $("monitor-clear-selection").disabled = selectedCount === 0 || Boolean(classroomScreenPreviewLease) || veyonRemoteViewPending;
  $("monitor-preview-toggle").disabled = !active || monitorSelectedTargets.size === 0 || liveVeyonViewActive;
  const veyonViewButton = $("monitor-veyon-view-button");
  veyonViewButton.disabled = !active || (!liveVeyonViewActive && selectedCount !== 1) || veyonRemoteViewPending;
  veyonViewButton.textContent = veyonRemoteViewPending
    ? "正在连接 Veyon…"
    : liveVeyonViewActive ? "停止手机实时画面" : "在手机查看 Veyon 实时画面";
  $("monitor-veyon-view-guidance").textContent = liveVeyonViewActive
    ? "画面来自教师电脑上的 Veyon 监看窗口，约每秒更新一次。"
    : selectedCount === 1
      ? "选中 1 台学生电脑后，将 Veyon 监看窗口画面传到手机。"
    : selectedCount > 1
      ? "Veyon 实时画面一次查看 1 台电脑；请将选择缩至 1 台。"
      : "选中 1 台学生电脑后，可在手机查看 Veyon 实时画面。";
  if (classroomScreenPreviewLease) {
    $("monitor-preview-toggle").textContent = "停止查看屏幕";
    $("monitor-preview-toggle").setAttribute("aria-expanded", "true");
  } else {
    $("monitor-preview-toggle").textContent = "查看所选屏幕";
    $("monitor-preview-toggle").setAttribute("aria-expanded", "false");
  }

  if (!matches.length) {
    const empty = document.createElement("p");
    empty.className = "muted monitor-empty-results";
    empty.textContent = search ? "没有匹配的电脑。" : "当前没有可查看的学生电脑。";
    list.append(empty);
    return;
  }
  const fragment = document.createDocumentFragment();
  for (const target of matches) {
    const key = normalizeTarget(target);
    const seat = activeClassroomSeatLocations.get(key);
    const label = document.createElement("label");
    label.className = "monitor-target-choice";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.checked = monitorSelectedTargets.has(key);
    input.disabled = Boolean(classroomScreenPreviewLease) || veyonRemoteViewPending;
    input.setAttribute("aria-label", `选择 ${target}`);
    input.addEventListener("change", () => {
      if (input.checked) {
        if (monitorSelectedTargets.size >= 5) {
          input.checked = false;
          toast("一次最多查看 5 台电脑。请停止当前预览后切换选择。");
          return;
        }
        monitorSelectedTargets.add(key);
      } else monitorSelectedTargets.delete(key);
      renderMonitorTargets();
    });
    const details = document.createElement("span");
    details.className = "monitor-target-details";
    const name = document.createElement("strong");
    name.textContent = target;
    const location = document.createElement("small");
    location.textContent = seat ? `第 ${seat.row} 排 · 第 ${seat.column} 位` : "学生电脑";
    details.append(name, location);
    label.append(input, details);
    fragment.append(label);
  }
  list.append(fragment);
}

function renderClassroomScreenPreviewTiles(targets) {
  const grid = $("classroom-screen-preview-grid");
  grid.replaceChildren();
  grid.classList.toggle("is-live", classroomScreenPreviewMode === "veyon-window");
  classroomScreenPreviewVisibleTargets.clear();
  for (const target of targets) {
    const key = normalizeTarget(target);
    const seat = activeClassroomSeatLocations.get(key);
    const tile = document.createElement("article");
    tile.className = "classroom-screen-preview-tile";
    tile.dataset.target = target;

    const heading = document.createElement("div");
    heading.className = "classroom-screen-preview-tile-heading";
    const name = document.createElement("strong");
    name.textContent = target;
    const timestamp = document.createElement("small");
    timestamp.textContent = "等待画面";
    heading.append(name, timestamp);
    const position = document.createElement("p");
    position.className = "classroom-screen-preview-tile-position";
    position.textContent = seat ? `第 ${seat.row} 排 · 第 ${seat.column} 位` : "学生电脑";

    const image = document.createElement("img");
    image.className = "classroom-screen-preview-image";
    image.alt = `${target} 的屏幕预览`;
    image.width = 320;
    image.height = 180;
    image.loading = "lazy";
    image.decoding = "async";

    const frameStatus = document.createElement("p");
    frameStatus.className = "classroom-screen-preview-frame-status";
    frameStatus.textContent = "查看时自动加载画面。";
    tile.append(heading, position, image, frameStatus);
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
    const frame = await fetchClassroomScreenPreviewFrame(target, lease.id, controller.signal,
      classroomScreenPreviewMode);
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
    if (frameStatus) frameStatus.textContent = frame.cached ? "显示上次画面" : "画面已更新";
  } catch (error) {
    if (error.name !== "AbortError" && frameStatus)
      frameStatus.textContent = error.message || "画面暂时不可用。";
    if (error.status === 401 || error.status === 403) void stopClassroomScreenPreview(false);
  } finally {
    if (classroomScreenPreviewControllers.get(target) === controller)
      classroomScreenPreviewControllers.delete(target);
    if (classroomScreenPreviewLease?.id === lease.id &&
        classroomScreenPreviewVisibleTargets.has(target) && !document.hidden) {
      const refreshDelay = classroomScreenPreviewMode === "veyon-window" ? 1000 : 2000;
      const timer = setTimeout(() => void refreshClassroomScreenPreviewFrame(tile), refreshDelay);
      classroomScreenPreviewTimers.set(target, timer);
    }
  }
}

async function fetchClassroomScreenPreviewFrame(target, leaseId, signal, mode = "screenshot") {
  const liveVeyonWindow = mode === "veyon-window";
  const contentType = liveVeyonWindow ? "image/jpeg" : "image/png";
  const headers = new Headers({ Accept: contentType });
  headers.set("Authorization", "Bearer " + accessToken);
  headers.set("X-Veyon-Request-Nonce", createRequestNonce());
  headers.set("X-Veyon-Request-Timestamp", new Date(Date.now() + serverClockOffsetMs).toISOString());
  headers.set("X-Veyon-Screen-Preview-Lease", leaseId.replaceAll("-", ""));
  const endpoint = liveVeyonWindow ? "/api/classroom/veyon-view/mobile/" : "/api/classroom/screen-preview/";
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
  const maximumBytes = liveVeyonWindow ? 3 * 1024 * 1024 : 1024 * 1024;
  if (blob.size === 0 || blob.size > maximumBytes) throw new Error("屏幕画面大小无效。");
  return {
    blob,
    capturedUtc: response.headers.get("X-Captured-Utc"),
    cached: response.headers.get("X-Screen-Frame") === "cached"
  };
}

async function stopClassroomScreenPreview(showStatus) {
  const lease = classroomScreenPreviewLease;
  const stoppedVeyonWindow = classroomScreenPreviewMode === "veyon-window";
  classroomScreenPreviewLease = null;
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
  button.textContent = "查看所选屏幕";
  button.setAttribute("aria-expanded", "false");
  $("monitor-preview-guidance").textContent = "开启后只刷新当前可见的屏幕；离开页面或互动结束会停止预览。";
  renderMonitorTargets();
  if (showStatus) {
    const message = stoppedVeyonWindow ? "Veyon 手机实时画面已停止。" : "屏幕巡视已停止。";
    $("classroom-screen-preview-status").textContent = message;
    if (stoppedVeyonWindow) $("monitor-veyon-view-status").textContent = message;
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

function currentProfile() {
  return profiles.find(profile => profile.id === profileSelect.value) || null;
}

function customWebsitePolicyInput() {
  const durationMinutes = Number($("custom-website-duration").value);
  const domains = $("custom-website-domains").value.split(/[\r\n]+/)
    .map(domain => domain.trim()).filter(Boolean);
  return {
    mode: $("custom-website-mode").value,
    durationMinutes,
    domains,
    valid: Number.isInteger(durationMinutes) && durationMinutes >= 10 && durationMinutes <= 300 && domains.length > 0
  };
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
    ? "当前范围 · " + count + " 台学生电脑"
    : count ? "已选择 " + count + " 台电脑" : "未选择电脑";
  const scopeStatus = $("target-scope-status");
  if (classroomTargetDefaultState === "mismatch") {
    scopeStatus.textContent = count
      ? "学生电脑未能与名单完整匹配；请核对选择范围。"
      : "学生电脑未能与名单完整匹配，未自动选择。请展开确认范围。";
  } else if (classroomTargetDefaultState === "unavailable") {
    scopeStatus.textContent = count
      ? "学生互动未开启，请核对电脑范围。"
      : "请先选择学生电脑范围，再查看或切换限制。";
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
    warning.textContent = "电脑或设置已更改，请重新检查影响后确认。";
    $("operation-result").append(warning);
  }
  $("refresh-status").disabled = !count;
  const operationDisabled = policyOperationInFlight;
  $("enable-policy").disabled = !count || !currentProfile() || operationDisabled;
  $("disable-policy").disabled = !count || !currentProfile() || operationDisabled;
  $("apply-custom-website-policy").disabled = !count || !customWebsitePolicyInput().valid || operationDisabled;
  $("disable-custom-website-policy").disabled = !count || operationDisabled;
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
    name.textContent = room.name || "未命名地点";
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
      const isIpTarget = /^(?:\d{1,3}\.){3}\d{1,3}$/.test(target) ||
        (target.includes(":") && /^[0-9a-f:]+$/i.test(target));
      const displayName = room.targetDisplayNames?.[target]?.trim();
      const displayNameIsIp = displayName && (/^(?:\d{1,3}\.){3}\d{1,3}$/.test(displayName) ||
        (displayName.includes(":") && /^[0-9a-f:]+$/i.test(displayName)));
      value.textContent = displayName && !displayNameIsIp ? displayName : isIpTarget ? "未命名学生" : target;
      label.append(input, value);
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
  $("profile-empty-help").classList.toggle("hidden", profiles.length > 0);
  if (!profiles.length) {
    const option = document.createElement("option");
    option.value = "";
    option.textContent = "暂无策略预设";
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
    const type = profile.kind === "website" ? "学生代理" : profile.kind === "application" ? "应用" :
      profile.kind === "websiteApplication" ? "学生代理+应用" : "系统";
    const combinedModes = String(profile.mode || "").split("|");
    const websiteMode = combinedModes[0] === "Blocklist" ? "学生代理黑名单" :
      combinedModes[0] === "Allowlist" ? "学生代理白名单" : "学生代理";
    const applicationMode = combinedModes[1] === "Enforce" ? "应用限制" :
      combinedModes[1] === "Audit" ? "应用影响检查" : "应用策略";
    const mode = profile.kind === "websiteApplication" ? websiteMode + " + " + applicationMode :
      profile.mode === "Blocklist" ? "黑名单" :
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
      appendParagraph(card, "学生代理限制：" + websiteMode + formatExpiry(item.websiteExpiresUtc));
    } else appendParagraph(card, "学生代理限制：状态未知");
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
    hourCycle: "h23"
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
    pendingReview = { kind: "policy", profileId: request.profileId,
      targets: [...request.targets], token: result.reviewToken };
    const confirm = document.createElement("button");
    confirm.className = "danger-button review-confirm";
    confirm.type = "button";
    confirm.textContent = "我已查看影响检查，确认启用限制";
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
  $("saved-session").classList.add("hidden");
  $("pair-form").classList.add("hidden");
  controlView.classList.remove("hidden");
  $("logout-button").classList.remove("hidden");
  const session = await api("/api/session");
  $("welcome-title").textContent = session.device?.displayName || "已连接";
  $("paired-device-name").textContent = session.device?.displayName || "手机";
  applyClassroomSession(session);
  setActiveControlTab("classroom");
  renderClassroomMode(true);
  const loadWarnings = [];
  const [roomsResult, profilesResult] = await Promise.allSettled([api("/api/rooms"), api("/api/profiles")]);
  if (roomsResult.status === "fulfilled" && Array.isArray(roomsResult.value)) {
    rooms = roomsResult.value;
  } else {
    rooms = [];
    const detail = roomsResult.status === "rejected" ? roomsResult.reason?.message : "教师端返回的数据格式无效。";
    loadWarnings.push("学生电脑名单暂不可用" + (detail ? "：" + String(detail).slice(0, 240) : "。"));
  }
  if (profilesResult.status === "fulfilled" && Array.isArray(profilesResult.value)) {
    profiles = profilesResult.value;
  } else {
    profiles = [];
    const detail = profilesResult.status === "rejected" ? profilesResult.reason?.message : "教师端返回的数据格式无效。";
    loadWarnings.push("策略预设暂不可用" + (detail ? "：" + String(detail).slice(0, 240) : "。"));
  }
  renderRooms();
  applyActiveClassroomTargetDefaults();
  renderProfiles();
  await refreshStatusIfSelected();
  try {
    await loadClassroomRestores();
  } catch (error) {
    loadWarnings.push("待恢复项暂不可用" + (error.message ? "：" + String(error.message).slice(0, 240) : "。"));
  }
  const warning = $("dashboard-load-warning");
  warning.textContent = loadWarnings.length
    ? "教师端已连接；部分信息暂不可用。" + loadWarnings.join(" ")
    : "";
  warning.classList.toggle("hidden", loadWarnings.length === 0);
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
      await resumeTokenFromCookie().catch(() => null);
      accessToken = null;
      controlView.classList.add("hidden");
      $("boot-view").classList.add("hidden");
      pairView.classList.remove("hidden");
      $("logout-button").classList.add("hidden");
      $("saved-session").classList.add("hidden");
      $("pair-form").classList.remove("hidden");
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
  const sessionChanged = activeClassroomSessionId !== nextSessionId;
  if (classroomScreenPreviewLease &&
      (classroomScreenPreviewLease.sessionId !== nextSessionId ||
       classroomScreenPreviewTargets.some(target =>
         !nextTargets.some(active => normalizeTarget(active) === normalizeTarget(target)))))
    void stopClassroomScreenPreview(false);
  activeClassroomTargets = nextTargets;
  activeClassroomSessionId = nextSessionId;
  if (sessionChanged) {
    monitorSelectedTargets = new Set(nextTargets.slice(0, 5).map(normalizeTarget));
  } else {
    const allowed = new Set(nextTargets.map(normalizeTarget));
    monitorSelectedTargets = new Set(Array.from(monitorSelectedTargets)
      .filter(target => allowed.has(target)).slice(0, 5));
    if (monitorSelectedTargets.size === 0 && nextTargets.length)
      monitorSelectedTargets = new Set(nextTargets.slice(0, 5).map(normalizeTarget));
  }
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
  renderMonitorTargets();
  renderClassroomMode();
}

function renderClassroomMode(setDefaults = false) {
  const active = Boolean(activeClassroomSessionId && activeClassroomTargets.length > 0);
  const pill = $("classroom-mode-value");
  const sessionButton = $("classroom-session-toggle");
  if (setDefaults) setActiveControlTab("classroom");
  $("classroom-target-summary").textContent = active
    ? activeClassroomTargets.length + " 台学生电脑已启用互动"
    : "等待开始";
  sessionButton.textContent = active ? "结束互动" : "开始互动";
  sessionButton.className = active
    ? "danger-button classroom-start-button"
    : "primary-button classroom-start-button";
  pill.textContent = active ? "互动已开启" : "未开始";
  pill.className = "pill mode";
  $("classroom-mode-description").textContent = active
    ? "可在下方查看学生求助并发送通知。"
    : "开始互动后，可查看学生求助并向所选学生电脑发送通知。";
}

async function runClassroomSession() {
  const button = $("classroom-session-toggle");
  const nextActive = !activeClassroomSessionId;
  if (!nextActive && !window.confirm("确定结束学生互动吗？")) return;
  button.disabled = true;
  button.textContent = nextActive ? "正在开启互动…" : "正在结束互动…";
  try {
    const result = await api("/api/classroom/session", {
      method: "POST", body: { active: nextActive }
    });
    if (result?.active !== nextActive)
      throw new Error(result?.message || "互动状态没有按预期更新，请刷新后重试。");
    const session = await api("/api/session");
    applyClassroomSession(session);
    renderClassroomMode();
    await loadClassroomRestores();
    toast(result.message || (nextActive ? "学生互动已开启。" : "学生互动已结束。"));
  } catch (error) {
    toast(error.message || "学生互动操作失败，请重试。");
  } finally {
    button.disabled = false;
    renderClassroomMode();
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
        activeClassroomSeatLocations = new Map();
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
        $("classroom-event-status").textContent = "学生互动已开启 · 新求助会自动显示。";
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
  const combinedApplicationEnforcement = profile.kind === "websiteApplication" &&
    String(profile.mode || "").split("|")[1] === "Enforce";
  const action = profile.kind === "system"
    ? "即将对 " + targets.length + " 台电脑启用长期系统限制：" +
      systemPolicyLabels.filter(([key]) => profile.systemSettings?.[key]).map(([, label]) => label).join("、") +
      "。策略不会自动到期；未选中的系统设置不会由此预设启用。是否继续？"
    : (profile.kind === "application" && profile.mode === "Enforce") || combinedApplicationEnforcement
    ? "学生代理限制会先发送；应用限制先进入影响检查。请核对逐台身份、版本和统计，教师确认前不会启用应用阻止。是否开始？"
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
    : "确认向 " + targets.length + " 台电脑解除“" + profile.name + "”" +
      (profile.kind === "websiteApplication" ? "学生代理和应用限制" : "限制") + "？";
  if (!window.confirm(message)) return;
  await runPolicy({ profileId: profile.id, targets, enabled: false });
}

async function runPolicy(body) {
  policyOperationInFlight = true;
  updateSelectionCount();
  operationResult.replaceChildren();
  try {
    lastOperation = { ...body, targets: [...body.targets] };
    renderOperation(await api("/api/policy", { method: "POST", body }), body);
  } catch (error) {
    pendingReview = null;
    toast(error.message);
  } finally {
    policyOperationInFlight = false;
    updateSelectionCount();
  }
}

async function runCustomWebsitePolicy(enabled) {
  const targets = selectedTargets();
  if (!targets.length) return;
  const input = customWebsitePolicyInput();
  if (enabled && !input.valid) {
    toast("请输入网站域名，并将时长设为 10–300 分钟。");
    return;
  }
  const message = enabled
    ? `确认向 ${targets.length} 台电脑发送${input.mode === "Blocklist" ? "黑名单" : "白名单"}网站策略？\n${input.domains.join("、")}\n${input.durationMinutes} 分钟后自动解除。`
    : `确认向 ${targets.length} 台电脑解除网站限制？`;
  if (!window.confirm(message)) return;
  const body = {
    targets,
    enabled,
    mode: input.mode,
    durationMinutes: input.durationMinutes,
    domains: input.domains
  };
  const operation = { kind: "custom-website", ...body };
  policyOperationInFlight = true;
  updateSelectionCount();
  operationResult.replaceChildren();
  try {
    lastOperation = { ...operation, targets: [...targets] };
    renderOperation(await api("/api/policy/custom-website", { method: "POST", body }), operation);
  } catch (error) {
    pendingReview = null;
    toast(error.message);
  } finally {
    policyOperationInFlight = false;
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
  if (((profile?.kind === "application" && profile.mode === "Enforce") ||
       (profile?.kind === "websiteApplication" && String(profile.mode || "").split("|")[1] === "Enforce")) && request.enabled) {
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
    await saveToken(result.accessToken).catch(() => {});
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

async function leaveControlPage() {
  await stopClassroomScreenPreview(false);
  stopClassroomEventPolling();
  pendingReview = null;
  controlView.classList.add("hidden");
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
    $("saved-session-status").textContent = "无法确认配对状态，请确认教师服务已启动后重试。";
    $("resume-control-button").textContent = "重试恢复配对";
    toast(error.message || "无法读取手机配对凭据。");
  }
}

function showScannedPairingInvite() {
  if (!scannedPairingCode) return;
  $("pair-code").value = scannedPairingCode;
  const status = $("pair-invite-status");
  status.textContent = "配对码已读取，点击下方按钮配对。";
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
    toast("学生电脑名单已刷新。");
  } catch (error) { toast(error.message); }
});
$("refresh-status").addEventListener("click", refreshStatusIfSelected);
$("enable-policy").addEventListener("click", enablePolicy);
$("disable-policy").addEventListener("click", disablePolicy);
$("apply-custom-website-policy").addEventListener("click", () => void runCustomWebsitePolicy(true));
$("disable-custom-website-policy").addEventListener("click", () => void runCustomWebsitePolicy(false));
for (const id of ["custom-website-mode", "custom-website-domains", "custom-website-duration"])
  $(id).addEventListener("input", updateSelectionCount);
$("logout-button").addEventListener("click", leaveControlPage);
$("resume-control-button").addEventListener("click", resumeSavedSession);
$("reconnect-button").addEventListener("click", () => void connectDashboard());
$("classroom-tab-button").addEventListener("click", () => setActiveControlTab("classroom"));
$("policy-tab-button").addEventListener("click", () => setActiveControlTab("policy"));
$("monitor-tab-button").addEventListener("click", () => setActiveControlTab("monitor"));
$("settings-tab-button").addEventListener("click", () => setActiveControlTab("settings"));
$("monitor-go-classroom").addEventListener("click", () => setActiveControlTab("classroom"));
$("monitor-target-search").addEventListener("input", renderMonitorTargets);
$("monitor-veyon-view-button").addEventListener("click", () => void openSelectedVeyonView());
$("monitor-select-visible").addEventListener("click", () => {
  const query = normalizeTarget($("monitor-target-search").value);
  const matches = activeClassroomTargets.filter(target => !query || normalizeTarget(target).includes(query));
  monitorSelectedTargets = new Set(matches.slice(0, 5).map(normalizeTarget));
  renderMonitorTargets();
  if (matches.length > 5) toast("一次最多选择 5 台；已选当前搜索结果中的前 5 台。");
});
$("monitor-clear-selection").addEventListener("click", () => {
  monitorSelectedTargets.clear();
  renderMonitorTargets();
});
$("send-classroom-notice").addEventListener("click", sendClassroomNotice);
$("monitor-preview-toggle").addEventListener("click", () => void startClassroomScreenPreview());
$("classroom-session-toggle").addEventListener("click", () => void runClassroomSession());
$("retry-classroom-restores").addEventListener("click", retryClassroomRestores);
$("toggle-all").addEventListener("click", () => {
  const inputs = Array.from(roomList.querySelectorAll("input[data-target]"));
  setAll(!inputs.length || inputs.some(input => !input.checked));
});
profileSelect.addEventListener("change", updateSelectionCount);

document.addEventListener("visibilitychange", () => {
  if (document.hidden && (classroomScreenPreviewLease || classroomScreenPreviewStartController))
    void stopClassroomScreenPreview(true);
});
window.addEventListener("pagehide", () => {
  if (classroomScreenPreviewLease || classroomScreenPreviewStartController)
    void stopClassroomScreenPreview(false);
});

async function start() {
  if (!window.isSecureContext || !window.crypto?.getRandomValues) {
    $("boot-view").classList.add("hidden");
    pairView.classList.remove("hidden");
    toast("请在已信任教师根证书的 HTTPS 地址打开本页。");
    return;
  }
  if ("serviceWorker" in navigator) {
    navigator.serviceWorker.register("/service-worker.js").catch(() => {});
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
      $("saved-session-status").textContent = "无法确认配对状态，请确认教师服务已启动后重试。";
      $("resume-control-button").textContent = "重试恢复配对";
      return;
    }
  }
  if (accessToken) {
    $("boot-view").classList.add("hidden");
    await connectDashboard();
    if (scannedPairingCode) toast("当前浏览器已有配对。退出后可继续使用刚扫描的邀请。");
    return;
  }
  $("boot-view").classList.add("hidden");
  pairView.classList.remove("hidden");
  if (tokenReadFailed) toast("没有读取到本机配对记录；可重新配对。完成后从此页面添加桌面图标即可保留登录状态。");
  showScannedPairingInvite();
}

start();
