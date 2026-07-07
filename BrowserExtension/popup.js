// WardLock popup: asks the desktop app (via native messaging) which accounts
// match the current tab's domain and fills the chosen code into the page.
// The app is the security authority — it re-validates the domain and refuses
// while locked; this popup is just UI.

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
    // Capture Chrome's real reason (host not found / forbidden / exited) before it clears
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

// Random per-profile ID: lets the app force number-matched approval for the
// first 24h after a new browser pairing. Self-asserted — hardening, not identity.
async function getClientId() {
  const stored = await chrome.storage.local.get("clientId");
  if (stored.clientId) return stored.clientId;
  const clientId = crypto.randomUUID();
  await chrome.storage.local.set({ clientId });
  return clientId;
}

const content = document.getElementById("content");
const domainEl = document.getElementById("domain");

function show(html) {
  content.innerHTML = html;
}

function esc(s) {
  const div = document.createElement("div");
  div.textContent = s ?? "";
  return div.innerHTML;
}

// Runs inside the page: finds a likely OTP input and fills it.
// Uses the native value setter + input/change events so React/Vue forms notice.
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

async function fill(tab, account, hostname) {
  const client = await getClientId();
  const res = await request({ action: "fill-code", id: account.id, domain: hostname, client });
  if (!res.ok) {
    if (res.error === "approval-required") {
      showChallenge(tab, res);
      return;
    }
    show(`<div class="message error">${esc(friendlyError(res))}</div>`);
    return;
  }

  let filled = false;
  try {
    const results = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: fillCodeInPage,
      args: [res.code],
    });
    filled = results?.[0]?.result === true;
  } catch (e) {
    filled = false;
  }

  if (filled) {
    show(`<div class="message filled">✓ Code filled (${esc(String(res.secondsRemaining))}s left)</div>`);
    setTimeout(() => window.close(), 900);
  } else {
    // No obvious OTP field — fall back to clipboard
    try {
      await navigator.clipboard.writeText(res.code);
      show(`<div class="message">No code field found — copied <b>${esc(res.code)}</b> to clipboard.<span class="hint">Click the field and paste.</span></div>`);
    } catch (e) {
      show(`<div class="message">Code: <b>${esc(res.code)}</b> (${esc(String(res.secondsRemaining))}s left)</div>`);
    }
  }
}

// Number-matched approval (issue #1): show the 2-digit number here, the user
// types it into the WardLock window — out-of-band relative to this popup, so a
// spoofed page or reflexive click can never release the code. The background
// worker finishes the fill even if this popup closes when WardLock takes focus.
function showChallenge(tab, res) {
  show(
    `<div class="challenge">
       <div class="challenge-number">${esc(res.challenge)}</div>
       <div class="message">WardLock is asking for approval.<span class="hint">Type this number into the WardLock window (${esc(String(res.expiresIn))}s). The code fills automatically once approved.</span></div>
     </div>`
  );

  chrome.runtime.onMessage.addListener(function onUpdate(msg) {
    if (msg?.type !== "approval-update" || msg.challengeId !== res.challengeId) return;
    chrome.runtime.onMessage.removeListener(onUpdate);

    switch (msg.status) {
      case "approved":
        if (msg.filled) {
          show(`<div class="message filled">✓ Approved — code filled</div>`);
          setTimeout(() => window.close(), 900);
        } else {
          show(`<div class="message">Approved, but no code field found. Code: <b>${esc(msg.code)}</b></div>`);
        }
        break;
      case "denied":
        show(`<div class="message error">WardLock denied this fill.</div>`);
        break;
      case "expired":
        show(`<div class="message error">Approval timed out. Try again.</div>`);
        break;
      case "locked":
        show(`<div class="message error">WardLock locked before the request was approved.</div>`);
        break;
      default:
        show(`<div class="message error">Approval failed. Try again.</div>`);
    }
  });

  // The background worker polls and fills — it outlives this popup
  chrome.runtime.sendMessage({ type: "await-approval", challengeId: res.challengeId, tabId: tab.id });
}

function friendlyError(res) {
  const error = typeof res === "string" ? res : res.error;
  const detail = (typeof res === "object" && res.detail) || "";

  switch (error) {
    case "locked": return "WardLock is locked. Unlock the app, then try again.";
    case "app-not-running": return "WardLock isn't running. Start the app, then try again.";
    case "app-elevated": return "WardLock is running as administrator, so the browser can't reach it. Restart WardLock normally (not elevated).";
    case "app-unreachable": return "Couldn't reach the WardLock app. Restart it, then try again.";
    case "origin-not-allowed": return "This extension isn't authorized. Re-enable browser integration in WardLock.";
    case "domain-mismatch": return "WardLock refused: account domain doesn't match this page.";
    case "native-disconnect":
      // Surface Chrome's actual native-messaging failure reason
      if (/not found/i.test(detail))
        return "WardLock's browser host isn't registered. In WardLock: menu (≡) → Enable Browser Integration, then reopen this popup.";
      if (/forbidden/i.test(detail))
        return "This extension's ID isn't authorized for the WardLock host. Confirm the ID is hcbclfghekjpdgnbfnmfeaamigencjjf, then re-enable browser integration.";
      if (/exited|crashed/i.test(detail))
        return "WardLock's browser helper stopped. Make sure the WardLock app is running, then try again.";
      return "Can't reach WardLock. Make sure the app is running and browser integration is enabled." + (detail ? ` (${detail})` : "");
    default: return `WardLock error: ${error}`;
  }
}

async function init() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  let hostname = null;
  try {
    const url = new URL(tab.url);
    if (url.protocol === "http:" || url.protocol === "https:") hostname = url.hostname;
  } catch (e) { /* chrome:// pages etc. */ }

  if (!hostname) {
    show(`<div class="message">This page can't receive codes.</div>`);
    return;
  }
  domainEl.textContent = hostname;

  const res = await request({ action: "accounts", domain: hostname, client: await getClientId() });
  if (!res.ok) {
    show(`<div class="message error">${esc(friendlyError(res))}</div>`);
    return;
  }

  if (res.accounts.length === 0) {
    show(`<div class="message">No account is linked to <b>${esc(hostname)}</b>.<span class="hint">In WardLock: right-click an account → Set Fill Domain.</span></div>`);
    return;
  }

  show("");
  for (const account of res.accounts) {
    const btn = document.createElement("button");
    btn.className = "account";
    const display = account.issuer
      ? `${account.issuer}${account.label ? " (" + account.label + ")" : ""}`
      : account.label;
    const approvalHint = account.requiresApproval ? `<span class="approval" title="Requires approval in WardLock">🛡</span>` : "";
    btn.innerHTML = `<span class="name">${esc(display)}</span>${approvalHint}<span class="source">${esc(account.source)}</span>`;
    btn.addEventListener("click", () => fill(tab, account, hostname));
    content.appendChild(btn);
  }
}

init();
