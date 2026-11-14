// Friendly handling of a dropped live connection. The interactive pages (dashboard, news, settings...) run over a persistent
// connection to the server. It drops whenever a phone sleeps or changes network, or when the server restarts for an update, and
// Blazor's stock reaction is a dead-end box saying "Reconnection failed. Try reloading the page". This replaces it:
//   - a slim banner while it retries by itself;
//   - if the server no longer knows this session (after an update, or a long sleep) the page reloads on its own;
//   - if the network is simply gone it keeps retrying and reloads as soon as the network is back;
//   - never a reload loop: at most one automatic reload per 15 seconds, after that a button.
(function () {
    'use strict';
    var KEY = 'aipulse-reconnect-reload';
    var banner = null, text = null, button = null, retryTimer = null, pageHiddenAt = 0, active = false;

    function build() {
        banner = document.getElementById('components-reconnect-modal');
        if (!banner) return false;
        text = banner.querySelector('[data-conn-text]');
        button = banner.querySelector('[data-conn-reload]');
        if (button && !button.dataset.bound) { button.dataset.bound = '1'; button.addEventListener('click', function () { location.reload(); }); }
        return true;
    }

    function show(message, withButton) {
        if (!banner && !build()) return;
        active = true;
        banner.hidden = false;
        if (text) text.textContent = message;
        if (button) button.hidden = !withButton;
    }
    function hide() {
        active = false;
        if (retryTimer) { clearTimeout(retryTimer); retryTimer = null; }
        if (banner || build()) banner.hidden = true;
    }

    function reloadOnce(reason) {
        var now = Date.now(), last = 0;
        try { last = parseInt(sessionStorage.getItem(KEY) || '0', 10); } catch (e) { /* storage blocked */ }
        if (now - last < 15000) { show('Still not connected. Check your connection, then reload.', true); return; }
        try { sessionStorage.setItem(KEY, String(now)); } catch (e) { /* ignore */ }
        show('Reconnecting' + (reason ? ' (' + reason + ')' : '') + '…', false);
        location.reload();
    }

    function retry() {
        if (!active) return;
        if (!navigator.onLine) { show('You are offline. Reconnecting when your connection is back…', false); retryTimer = setTimeout(retry, 3000); return; }
        try {
            if (window.Blazor && typeof Blazor.reconnect === 'function') {
                Blazor.reconnect().then(function (ok) {
                    if (ok === false) reloadOnce('session ended');
                    else if (ok === true) hide();
                    else retryTimer = setTimeout(retry, 3000);
                }).catch(function () { retryTimer = setTimeout(retry, 3000); });
                return;
            }
        } catch (e) { /* fall through to reload */ }
        reloadOnce('retrying');
    }

    // Blazor tells the reconnect modal element which state it is in, by class and by event.
    document.addEventListener('components-reconnect-state-changed', function (e) {
        var state = e && e.detail && e.detail.state;
        if (state === 'show') show('Connection lost. Reconnecting…', false);
        else if (state === 'hide') hide();
        else if (state === 'failed') { show('Still reconnecting…', false); retryTimer = setTimeout(retry, 2000); }
        else if (state === 'rejected') reloadOnce('session ended');
    });

    window.addEventListener('online', function () { if (active) retry(); });
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'hidden') { pageHiddenAt = Date.now(); return; }
        // Back from the background: phones suspend the connection, so check it and recover quietly.
        if (active) retry();
    });
    document.addEventListener('DOMContentLoaded', build);
})();
