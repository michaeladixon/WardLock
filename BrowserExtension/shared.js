// Shared by the popup and the background worker (each runs it in its own
// context, so `port`/`pending` are per-context): the native-messaging client,
// the per-profile client ID, and the page-side fill function.

const HOST = "com.wardlock.wardlock";

// Firefox exposes promise-based APIs on `browser`; Chrome on `chrome` (and on
// `browser` from Chrome 148). Use whichever exists so one script runs in both.
const ext = globalThis.browser ?? globalThis.chrome;

let port = null;
const pending = []; // FIFO of resolvers — the host answers strictly in order

function connect() {
  port = ext.runtime.connectNative(HOST);
  port.onMessage.addListener((msg) => {
    const resolve = pending.shift();
    if (resolve) resolve(msg);
  });
  port.onDisconnect.addListener((p) => {
    // Capture the browser's real reason (host not found / forbidden / exited):
    // Firefox reports it on port.error, Chrome on runtime.lastError.
    const detail = p?.error?.message || ext.runtime.lastError?.message || "";
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
      resolve({ ok: false, error: "native-disconnect", detail: ext.runtime.lastError?.message || "" });
    }
  });
}

// Random per-profile ID: lets the app force number-matched approval for the
// first 24h after a new browser pairing. Self-asserted — hardening, not identity.
async function getClientId() {
  const stored = await ext.storage.local.get("clientId");
  if (stored.clientId) return stored.clientId;
  const clientId = crypto.randomUUID();
  await ext.storage.local.set({ clientId });
  return clientId;
}

// Runs inside the page (serialized by scripting.executeScript, so it must be
// self-contained): finds a likely OTP input and fills it.
// Uses the native value setter + input/change events so React/Vue forms notice.
function fillCodeInPage(code, expectedHost) {
  // The tab may have navigated since the request was made (approval can take
  // up to ~75s): never fill a code into a different site.
  if (location.hostname !== expectedHost) return false;

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
