"use strict";

importScripts("policyCompiler.js");

const nativeHostName = "fun.kidscode.veyoncampus.policy";
const maximumDomainsPerList = 1000;
const profileIdPromise = chrome.storage.local.get("profileId").then(async stored => {
  if (typeof stored.profileId === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(stored.profileId))
    return stored.profileId.toLowerCase();
  const profileId = crypto.randomUUID();
  await chrome.storage.local.set({ profileId });
  return profileId;
});
let nativePort = null;
let reconnectDelayMs = 1000;
let reconnectTimer = null;
let syncTimer = null;
let applying = Promise.resolve();

function isDomain(value) {
  return typeof value === "string" && value.length > 0 && value.length <= 253 &&
    value === value.toLowerCase() && !value.endsWith(".") &&
    value.split(".").every(label => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(label));
}

function computePolicyHash(snapshot) {
  const fields = [
    "1",
    snapshot.campusId,
    String(snapshot.revision),
    snapshot.mode,
    snapshot.expiresUtc || "",
    snapshot.expired ? "1" : "0",
    String(snapshot.blockedDomains.length),
    ...snapshot.blockedDomains,
    String(snapshot.allowedDomains.length),
    ...snapshot.allowedDomains
  ];
  return crypto.subtle.digest("SHA-256", new TextEncoder().encode(fields.join("\n")))
    .then(bytes => [...new Uint8Array(bytes)].map(value => value.toString(16).padStart(2, "0")).join("").toUpperCase());
}

async function validateSnapshot(snapshot) {
  if (!snapshot || snapshot.schemaVersion !== 1 || snapshot.messageType !== "websitePolicy.snapshot" ||
      typeof snapshot.campusId !== "string" || snapshot.campusId.length < 1 || snapshot.campusId.length > 128 ||
      !Number.isSafeInteger(snapshot.revision) || snapshot.revision < 0 ||
      !["legacyRegistry", "extensionRealtime"].includes(snapshot.executionMode) ||
      snapshot.executionMode === "extensionRealtime" && !/^[a-p]{32}$/.test(snapshot.extensionId || "") ||
      snapshot.executionMode === "legacyRegistry" && snapshot.extensionId != null ||
      !["disabled", "blocklist", "allowlist", "combined"].includes(snapshot.mode) ||
      !Array.isArray(snapshot.blockedDomains) || !Array.isArray(snapshot.allowedDomains) ||
      snapshot.blockedDomains.length > maximumDomainsPerList || snapshot.allowedDomains.length > maximumDomainsPerList ||
      typeof snapshot.expired !== "boolean")
    throw new Error("snapshot_shape_invalid");

  if (![...snapshot.blockedDomains, ...snapshot.allowedDomains].every(isDomain) ||
      new Set(snapshot.blockedDomains).size !== snapshot.blockedDomains.length ||
      new Set(snapshot.allowedDomains).size !== snapshot.allowedDomains.length ||
      snapshot.blockedDomains.some((domain, index, values) => index > 0 && values[index - 1] >= domain) ||
      snapshot.allowedDomains.some((domain, index, values) => index > 0 && values[index - 1] >= domain))
    throw new Error("snapshot_domains_invalid");

  if (snapshot.expiresUtc !== null && snapshot.expiresUtc !== undefined &&
      (typeof snapshot.expiresUtc !== "string" || !Number.isFinite(Date.parse(snapshot.expiresUtc))))
    throw new Error("snapshot_expiry_invalid");
  if (snapshot.expired && (!snapshot.expiresUtc || Date.parse(snapshot.expiresUtc) > Date.now() ||
      snapshot.mode !== "disabled" || snapshot.blockedDomains.length !== 0 || snapshot.allowedDomains.length !== 0))
    throw new Error("snapshot_expired_state_invalid");
  if (snapshot.mode === "disabled" && (snapshot.blockedDomains.length !== 0 || snapshot.allowedDomains.length !== 0) ||
      snapshot.mode === "blocklist" && (snapshot.blockedDomains.length === 0 || snapshot.allowedDomains.length !== 0) ||
      snapshot.mode === "allowlist" && (snapshot.allowedDomains.length === 0 || snapshot.blockedDomains.length !== 0) ||
      snapshot.mode === "combined" && snapshot.blockedDomains.length + snapshot.allowedDomains.length === 0)
    throw new Error("snapshot_mode_invalid");

  const hash = await computePolicyHash(snapshot);
  if (snapshot.policyHash !== hash) throw new Error("snapshot_hash_mismatch");
}

async function hashRules(rules) {
  const bytes = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(VeyonPolicyCompiler.canonicalRules(rules)));
  return [...new Uint8Array(bytes)].map(value => value.toString(16).padStart(2, "0")).join("").toUpperCase();
}

function decodeBase64(value, maximumBytes) {
  if (typeof value !== "string" || value.length === 0 || value.length > maximumBytes * 2)
    throw new Error("agent_envelope_field_invalid");
  const binary = atob(value);
  if (binary.length === 0 || binary.length > maximumBytes) throw new Error("agent_envelope_field_invalid");
  return Uint8Array.from(binary, character => character.charCodeAt(0));
}

async function verifyAgentEnvelope(envelope) {
  if (!envelope || typeof envelope.payload !== "string" || typeof envelope.signature !== "string" ||
      typeof envelope.publicKeyPem !== "string" || envelope.publicKeyPem.length > 8192)
    throw new Error("agent_envelope_invalid");

  const stored = await chrome.storage.local.get("agentPublicKeyPem");
  if (typeof stored.agentPublicKeyPem === "string" && stored.agentPublicKeyPem !== envelope.publicKeyPem)
    throw new Error("agent_identity_changed");

  const pemBody = envelope.publicKeyPem.replace(/-----BEGIN PUBLIC KEY-----|-----END PUBLIC KEY-----|\s/g, "");
  const publicKeyBytes = decodeBase64(pemBody, 2048);
  const publicKey = await crypto.subtle.importKey("spki", publicKeyBytes,
    { name: "RSA-PSS", hash: "SHA-256" }, false, ["verify"]);
  const payloadBytes = decodeBase64(envelope.payload, 128 * 1024);
  const signatureBytes = decodeBase64(envelope.signature, 1024);
  const valid = await crypto.subtle.verify({ name: "RSA-PSS", saltLength: 32 }, publicKey,
    signatureBytes, payloadBytes);
  if (!valid) throw new Error("agent_signature_invalid");

  if (typeof stored.agentPublicKeyPem !== "string")
    await chrome.storage.local.set({ agentPublicKeyPem: envelope.publicKeyPem });
  return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(payloadBytes));
}

async function reconcileRules(snapshot) {
  const now = Date.now();
  const expired = snapshot.expired || (snapshot.expiresUtc && Date.parse(snapshot.expiresUtc) <= now);
  const effective = expired ? { ...snapshot, mode: "disabled", blockedDomains: [], allowedDomains: [] } : snapshot;
  const desired = VeyonPolicyCompiler.compile(effective);
  const existing = await chrome.declarativeNetRequest.getDynamicRules();
  const owned = existing.filter(rule => rule.id >= VeyonPolicyCompiler.firstRuleId && rule.id <= VeyonPolicyCompiler.lastRuleId);

  // One atomic update preserves the previous restrictions if Edge rejects the replacement.
  await chrome.declarativeNetRequest.updateDynamicRules({
    removeRuleIds: owned.map(rule => rule.id),
    addRules: desired
  });

  const installed = (await chrome.declarativeNetRequest.getDynamicRules())
    .filter(rule => rule.id >= VeyonPolicyCompiler.firstRuleId && rule.id <= VeyonPolicyCompiler.lastRuleId);
  const expectedText = VeyonPolicyCompiler.canonicalRules(desired);
  const actualText = VeyonPolicyCompiler.canonicalRules(installed);
  if (actualText !== expectedText) throw new Error("rules_readback_mismatch");

  const rulesHash = await hashRules(installed);
  if (snapshot.expiresUtc && !expired) {
    await chrome.alarms.create("veyon-policy-expiry", { when: Date.parse(snapshot.expiresUtc) });
  } else {
    await chrome.alarms.clear("veyon-policy-expiry");
  }

  await chrome.storage.local.set({
    lastAppliedRevision: snapshot.revision,
    lastAppliedPolicyHash: snapshot.policyHash,
    lastRulesHash: rulesHash,
    lastAppliedUtc: new Date().toISOString(),
    expiresUtc: expired ? null : snapshot.expiresUtc || null,
    lastStatus: "applied"
  });
  return { rulesHash, ruleCount: installed.length };
}

async function applySnapshot(snapshot) {
  await validateSnapshot(snapshot);
  const stored = await chrome.storage.local.get(["lastAppliedRevision", "lastAppliedPolicyHash"]);
  if (Number.isSafeInteger(stored.lastAppliedRevision)) {
    if (snapshot.revision < stored.lastAppliedRevision) throw new Error("snapshot_revision_rollback");
    if (snapshot.revision === stored.lastAppliedRevision && stored.lastAppliedPolicyHash !== snapshot.policyHash &&
        !(snapshot.expired && snapshot.expiresUtc && Date.parse(snapshot.expiresUtc) <= Date.now()))
      throw new Error("snapshot_same_revision_conflict");
  }

  const result = await reconcileRules(snapshot);
  const profileId = await profileIdPromise;
  postNative({
    schemaVersion: 1,
    messageType: "websitePolicy.applyResult",
    profileId,
    revision: snapshot.revision,
    policyHash: snapshot.policyHash,
    rulesHash: result.rulesHash,
    status: "applied",
    ruleCount: result.ruleCount,
    appliedUtc: new Date().toISOString(),
    errorCode: null
  });
}

function postNative(message) {
  if (!nativePort) return;
  try { nativePort.postMessage(message); }
  catch { nativePort = null; }
}

function requestSnapshot() {
  profileIdPromise.then(profileId => postNative({ schemaVersion: 1, messageType: "resync", profileId }));
}

async function applyAgentEnvelope(message) {
  let snapshot;
  try {
    snapshot = await verifyAgentEnvelope(message.envelope);
    await validateSnapshot(snapshot);
  } catch (error) {
    const errorCode = typeof error?.message === "string" ? error.message.slice(0, 128) : "snapshot_untrusted";
    await chrome.storage.local.set({ lastStatus: "degraded", lastErrorCode: errorCode });
    return;
  }

  try {
    await applySnapshot(snapshot);
  } catch (error) {
    const errorCode = typeof error?.message === "string" ? error.message.slice(0, 128) : "apply_failed";
    await chrome.storage.local.set({ lastStatus: "degraded", lastErrorCode: errorCode });
    const profileId = await profileIdPromise;
    postNative({
      schemaVersion: 1,
      messageType: "websitePolicy.applyResult",
      profileId,
      revision: snapshot.revision,
      policyHash: snapshot.policyHash,
      rulesHash: "0".repeat(64),
      status: "degraded",
      ruleCount: 0,
      appliedUtc: new Date().toISOString(),
      errorCode
    });
  }
}

function connectNative() {
  if (nativePort) return;
  try {
    nativePort = chrome.runtime.connectNative(nativeHostName);
    nativePort.onMessage.addListener(message => {
      if (!message) return;
      if (message.messageType === "websitePolicy.agentEnvelope") {
        applying = applying.then(() => applyAgentEnvelope(message));
      } else if (message.messageType === "websitePolicy.resultAck" && message.accepted === false) {
        chrome.storage.local.set({ lastStatus: "degraded", lastErrorCode: "agent_rejected_apply_result" });
      }
    });
    nativePort.onDisconnect.addListener(() => {
      nativePort = null;
      if (syncTimer) clearInterval(syncTimer);
      if (reconnectTimer) clearTimeout(reconnectTimer);
      reconnectTimer = setTimeout(connectNative, reconnectDelayMs);
      reconnectDelayMs = Math.min(reconnectDelayMs * 2, 30000);
    });
    reconnectDelayMs = 1000;
    profileIdPromise.then(profileId => postNative({ schemaVersion: 1, messageType: "hello", profileId }));
    requestSnapshot();
    if (syncTimer) clearInterval(syncTimer);
    syncTimer = setInterval(requestSnapshot, 2000);
  } catch {
    nativePort = null;
    if (reconnectTimer) clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(connectNative, reconnectDelayMs);
    reconnectDelayMs = Math.min(reconnectDelayMs * 2, 30000);
  }
}

chrome.runtime.onInstalled.addListener(connectNative);
chrome.runtime.onStartup.addListener(connectNative);
chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name !== "veyon-policy-expiry") return;
  chrome.storage.local.get("expiresUtc").then(async state => {
    if (typeof state.expiresUtc === "string" && Date.parse(state.expiresUtc) <= Date.now()) {
      const existing = await chrome.declarativeNetRequest.getDynamicRules();
      const ownedIds = existing.filter(rule => rule.id >= VeyonPolicyCompiler.firstRuleId &&
        rule.id <= VeyonPolicyCompiler.lastRuleId).map(rule => rule.id);
      if (ownedIds.length > 0)
        await chrome.declarativeNetRequest.updateDynamicRules({ removeRuleIds: ownedIds, addRules: [] });
      await chrome.storage.local.set({ expiresUtc: null, lastStatus: "expired", lastErrorCode: null });
    }
    requestSnapshot();
  }).catch(error => chrome.storage.local.set({
    lastStatus: "degraded",
    lastErrorCode: typeof error?.message === "string" ? error.message.slice(0, 128) : "expiry_revoke_failed"
  }));
});

connectNative();
