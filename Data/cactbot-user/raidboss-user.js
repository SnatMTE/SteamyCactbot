'use strict';

// ============================================================
// CactbotUI relay - broadcasts every Cactbot alarm/alert/info
// text back via OverlayPlugin BroadcastMessage so the
// CactbotUI Dalamud plugin can display them in-game.
//
// SETUP:
//   1. Copy this file to your Cactbot user config directory.
//      For IINACT this is usually:
//        %APPDATA%\XIVLauncher\pluginConfigs\IINACT\cactbot-user\
//   2. In IINACT open the Cactbot raidboss overlay settings and
//      set "User Config Path" to that directory, then reload.
//   3. The plugin overlay should show "Relay loaded" briefly
//      on startup - that confirms the file is working.
// ============================================================

(function cactbotUIRelay() {

  // ----- helpers ------------------------------------------------

  function sendBroadcast(text, type) {
    const trimmed = String(text ?? '').trim();
    if (!trimmed) return;
    if (typeof callOverlayHandler !== 'function') return;
    void callOverlayHandler({
      call: 'broadcast',
      source: 'cactbot-ui-relay',
      msg: { type: type || 'info', text: trimmed },
    });
  }

  // Dedup within 3 s so both hooks don't double-fire the same text.
  const seen = new Map();
  function sendDeduped(text, type) {
    const key = String(text ?? '').trim();
    if (!key) return;
    const now = Date.now();
    const last = seen.get(key);
    if (last !== undefined && now - last < 3000) return;
    seen.set(key, now);
    // Prune stale entries occasionally.
    if (seen.size > 100) {
      const cutoff = now - 10000;
      for (const [k, t] of seen) if (t < cutoff) seen.delete(k);
    }
    sendBroadcast(text, type);
  }

  // ----- Method 1: Options.TransformText hook -------------------
  // Called by popup-text.ts _addTextFor() for EVERY alarm/alert/info
  // text before it is written into the DOM.  Severity is unknown here
  // so we broadcast as 'info'; the MutationObserver below will
  // re-broadcast with the correct severity if the dedup window allows.
  if (typeof Options !== 'undefined') {
    const origTransformText = Options.TransformText ?? null;
    Options.TransformText = function (text) {
      const out = origTransformText ? origTransformText.call(this, text) : text;
      const result = (out !== null && out !== undefined) ? out : text;
      sendDeduped(result, 'info');
      return out;
    };
  }

  // ----- Method 2: MutationObserver on Cactbot alert elements ---
  // Watches for div.alarm-text / div.alert-text / div.info-text nodes
  // added to the DOM (class names confirmed from popup-text.ts
  // _makeTextElement: `const textElementClass = \`\${textType}-text\``).
  // This fires AFTER TransformText so the dedup Map prevents duplicates,
  // but it carries the correct severity type.
  function checkNode(node) {
    if (node.nodeType !== 1) return;
    const cl = node.classList;
    if (!cl) return;
    const text = (node.innerText || node.textContent || '').trim();
    if (!text) return;
    if      (cl.contains('alarm-text')) sendDeduped(text, 'alarm');
    else if (cl.contains('alert-text')) sendDeduped(text, 'alert');
    else if (cl.contains('info-text'))  sendDeduped(text, 'info');
  }

  function startObserver() {
    const observer = new MutationObserver(function (mutations) {
      for (const m of mutations) {
        for (const node of m.addedNodes) checkNode(node);
      }
    });
    observer.observe(document.documentElement, { childList: true, subtree: true });

    // Startup confirmation - if the plugin shows "Relay loaded" the file is working.
    sendBroadcast('Relay loaded', 'info');
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', startObserver);
  } else {
    startObserver();
  }

})();
