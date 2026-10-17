"use strict";

const TEST_RULE_ID = 1;
const status = document.getElementById("status");

function setStatus(message) {
  status.textContent = message;
}

async function readRules() {
  const rules = await chrome.declarativeNetRequest.getDynamicRules();
  return rules.filter(rule => rule.id === TEST_RULE_ID);
}

async function refreshStatus() {
  const rules = await readRules();
  setStatus(rules.length === 0
    ? "原型规则未安装。"
    : `原型规则已安装：\n${JSON.stringify(rules[0], null, 2)}`);
}

document.getElementById("block").addEventListener("click", async () => {
  try {
    await chrome.declarativeNetRequest.updateDynamicRules({
      removeRuleIds: [TEST_RULE_ID],
      addRules: [{
        id: TEST_RULE_ID,
        priority: 1,
        action: { type: "block" },
        condition: {
          requestDomains: ["bilibili.com"],
          resourceTypes: ["main_frame"]
        }
      }]
    });
    const installed = await readRules();
    if (installed.length !== 1 || installed[0].condition.requestDomains?.[0] !== "bilibili.com")
      throw new Error("规则读回与预期不一致。");
    setStatus("规则已写入并读回。现在在此 Edge 窗口中导航到 bilibili.com；无需重启浏览器。");
  } catch (error) {
    setStatus(`安装规则失败：${error instanceof Error ? error.message : String(error)}`);
  }
});

document.getElementById("remove").addEventListener("click", async () => {
  try {
    await chrome.declarativeNetRequest.updateDynamicRules({ removeRuleIds: [TEST_RULE_ID] });
    const installed = await readRules();
    if (installed.length !== 0) throw new Error("规则移除后仍能读回。");
    setStatus("规则已移除并确认读回为空。重新导航到 bilibili.com 检查恢复；无需重启浏览器。");
  } catch (error) {
    setStatus(`移除规则失败：${error instanceof Error ? error.message : String(error)}`);
  }
});

document.getElementById("refresh").addEventListener("click", () => {
  refreshStatus().catch(error => setStatus(`读取规则失败：${error instanceof Error ? error.message : String(error)}`));
});

refreshStatus().catch(error => setStatus(`读取规则失败：${error instanceof Error ? error.message : String(error)}`));
