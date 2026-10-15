// WardLock background worker: carries a number-matched fill approval to
// completion after the popup closes. The popup hands off {challengeId, tabId};
// this worker polls the desktop app and fills the code once the user has typed
// the matching 2-digit number into WardLock. The app is the security authority —
// the challenge is one-shot, time-boxed, and bound to this browser profile.

// Chrome runs this file as a classic service worker; Firefox loads shared.js
// first via manifest background.scripts.
if (typeof importScripts === "function") importScripts("shared.js");

let activeApprovals = 0;

// An open native port keeps the service worker (and a WardLock.exe proxy
// process) alive indefinitely; release it once no approval is in flight.
function releasePortIfIdle() {
  if (activeApprovals > 0 || !port) return;
  port.disconnect();
  port = null;
  while (pending.length) pending.shift()({ ok: false, error: "native-disconnect", detail: "" });
}

function broadcast(update) {
  // Popup may be closed — ignore the "no receiver" rejection
  ext.runtime.sendMessage(update).catch(() => {});
}

async function setBadge(text, color) {
  await ext.action.setBadgeBackgroundColor({ color });
  await ext.action.setBadgeText({ text });
  setTimeout(() => ext.action.setBadgeText({ text: "" }), 6000);
}

async function awaitApproval(handoff) {
  activeApprovals++;
  try {
    await pollApproval(handoff);
  } finally {
    activeApprovals--;
    releasePortIfIdle();
  }
}

async function pollApproval({ challengeId, tabId, hostname }) {
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
        const results = await ext.scripting.executeScript({
          target: { tabId },
          func: fillCodeInPage,
          args: [res.code, hostname],
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

ext.runtime.onMessage.addListener((msg) => {
  if (msg?.type === "await-approval") awaitApproval(msg);
});
