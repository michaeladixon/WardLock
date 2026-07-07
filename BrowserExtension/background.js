// WardLock background worker: carries a number-matched fill approval to
// completion after the popup closes. The popup hands off {challengeId, tabId};
// this worker polls the desktop app and fills the code once the user has typed
// the matching 2-digit number into WardLock. The app is the security authority —
// the challenge is one-shot, time-boxed, and bound to this browser profile.

const HOST = "com.wardlock.wardlock";

let port = null;
const pending = []; // FIFO of resolvers — the host answers strictly in order

function connect() {
  port = chrome.runtime.connectNative(HOST);
  port.onMessage.addListener((msg) => {
    const resolve = pending.shift();
    if (resolve) resolve(msg);
  });
  port.onDisconnect.addListener(() => {
    const detail = chrome.runtime.lastError?.message || "";
    port = null;
    while (pending.length) pending.shift()({ ok: false, error: "native-disconnect", detail });
  });
}

function request(msg) {
  return new Promise((resolve) => {
    if (!port) connect();
    pending.push(resolve);
    try {
      port.postMessage(msg);
    } catch (e) {
      pending.pop();
      resolve({ ok: false, error: "native-disconnect", detail: chrome.runtime.lastError?.message || "" });
    }
  });
}

// Random per-profile ID: lets the app force approval for the first 24h after
// a new browser pairing. Self-asserted, so a hardening heuristic — not identity.
async function getClientId() {
  const stored = await chrome.storage.local.get("clientId");
  if (stored.clientId) return stored.clientId;
  const clientId = crypto.randomUUID();
  await chrome.storage.local.set({ clientId });
  return clientId;
}

// Runs inside the page: finds a likely OTP input and fills it.
// (Duplicated from popup.js — MV3 offers no shared module for injected funcs.)
function fillCodeInPage(code) {
  const isFillable = (el) =>
    el instanceof HTMLInputElement &&
    !el.disabled && !el.readOnly &&
    ["text", "tel", "number", "password", ""].includes(el.type || "") &&
    el.offsetParent !== null;

  let target = isFillable(document.activeElement) ? document.activeElement : null;
  if (!target) {
    const selectors = [
      'input[autocomplete="one-time-code"]',
      'input[name*="otp" i]', 'input[id*="otp" i]',
      'input[name*="totp" i]', 'input[name*="2fa" i]', 'input[id*="2fa" i]',
      'input[name*="mfa" i]', 'input[id*="mfa" i]',
      'input[name*="code" i]', 'input[id*="code" i]',
      'input[name*="token" i]',
      'input[inputmode="numeric"]',
    ];
    for (const sel of selectors) {
      const el = [...document.querySelectorAll(sel)].find(isFillable);
      if (el) { target = el; break; }
    }
  }
  if (!target) return false;

  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
  target.focus();
  setter.call(target, code);
  target.dispatchEvent(new Event("input", { bubbles: true }));
  target.dispatchEvent(new Event("change", { bubbles: true }));
  return true;
}

function broadcast(update) {
  // Popup may be closed — ignore the "no receiver" rejection
  chrome.runtime.sendMessage(update).catch(() => {});
}

async function setBadge(text, color) {
  await chrome.action.setBadgeBackgroundColor({ color });
  await chrome.action.setBadgeText({ text });
  setTimeout(() => chrome.action.setBadgeText({ text: "" }), 6000);
}

async function awaitApproval({ challengeId, tabId }) {
  const client = await getClientId();

  // Poll for ~75s — the app's entry window is 60s plus pickup slack
  for (let i = 0; i < 75; i++) {
    const res = await request({ action: "approval-status", challengeId, client });

    if (!res.ok) {
      broadcast({ type: "approval-update", challengeId, status: res.error === "locked" ? "locked" : "error" });
      setBadge("✕", "#f38ba8");
      return;
    }

    if (res.status === "pending") {
      await new Promise((r) => setTimeout(r, 1000));
      continue;
    }

    if (res.status === "approved") {
      let filled = false;
      try {
        const results = await chrome.scripting.executeScript({
          target: { tabId },
          func: fillCodeInPage,
          args: [res.code],
        });
        filled = results?.[0]?.result === true;
      } catch (e) {
        filled = false;
      }
      // If no field was found, hand the code to the popup (if open) as fallback
      broadcast({ type: "approval-update", challengeId, status: "approved", filled, code: filled ? undefined : res.code });
      setBadge("✓", "#a6e3a1");
      return;
    }

    // denied / expired / unknown
    broadcast({ type: "approval-update", challengeId, status: res.status });
    setBadge("✕", "#f38ba8");
    return;
  }

  broadcast({ type: "approval-update", challengeId, status: "expired" });
}

chrome.runtime.onMessage.addListener((msg) => {
  if (msg?.type === "await-approval") awaitApproval(msg);
});
