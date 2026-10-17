"use strict";

(() => {
  const firstRuleId = 1000;
  const lastRuleId = 3009;

  function compile(snapshot) {
    const rules = [];
    const navigationTypes = ["main_frame"];

    if (snapshot.mode === "allowlist") {
      rules.push({
        id: firstRuleId,
        priority: 1,
        action: { type: "block" },
        condition: { urlFilter: "|http://", resourceTypes: navigationTypes }
      });
      rules.push({
        id: firstRuleId + 1,
        priority: 1,
        action: { type: "block" },
        condition: { urlFilter: "|https://", resourceTypes: navigationTypes }
      });
    }

    if (snapshot.mode === "blocklist" || snapshot.mode === "combined") {
      snapshot.blockedDomains.forEach((domain, index) => {
        rules.push({
          id: firstRuleId + 10 + index,
          priority: 20,
          action: { type: "block" },
          condition: { requestDomains: [domain], resourceTypes: navigationTypes }
        });
      });
    }

    if (snapshot.mode === "allowlist" || snapshot.mode === "combined") {
      snapshot.allowedDomains.forEach((domain, index) => {
        rules.push({
          id: firstRuleId + 1010 + index,
          priority: 30,
          action: { type: "allow" },
          condition: { requestDomains: [domain], resourceTypes: navigationTypes }
        });
      });
    }

    if (rules.some(rule => rule.id > lastRuleId))
      throw new Error("rule_id_range_exceeded");
    return rules;
  }

  function canonicalRules(rules) {
    return JSON.stringify([...rules].map(rule => {
      const condition = {};
      if (typeof rule.condition.urlFilter === "string") condition.urlFilter = rule.condition.urlFilter;
      if (Array.isArray(rule.condition.requestDomains))
        condition.requestDomains = [...rule.condition.requestDomains].sort();
      if (Array.isArray(rule.condition.resourceTypes))
        condition.resourceTypes = [...rule.condition.resourceTypes].sort();
      return {
        id: rule.id,
        priority: rule.priority,
        action: { type: rule.action.type },
        condition
      };
    }).sort((left, right) => left.id - right.id));
  }

  globalThis.VeyonPolicyCompiler = Object.freeze({ compile, canonicalRules, firstRuleId, lastRuleId });
})();
