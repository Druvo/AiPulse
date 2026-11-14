// Install support for the /app page, loaded on every page so the browser's install prompt is never missed: Chromium fires
// "beforeinstallprompt" once, early, and it must be kept until someone presses a button. iOS has no such event (Safari only offers
// Share -> Add to Home Screen), so there the page shows steps instead of a button.
(function () {
    'use strict';
    var deferred = null;
    var installed = false;

    function standalone() {
        return (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) || window.navigator.standalone === true;
    }
    function isIos() {
        var ua = navigator.userAgent || '';
        return /iPhone|iPad|iPod/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1);
    }

    function render() {
        var button = document.querySelector('[data-install-button]');
        var status = document.querySelector('[data-install-status]');
        if (!button && !status) return;

        var message = null;
        if (standalone() || installed) {
            if (button) button.hidden = true;
            message = installed && !standalone() ? 'Installed. Open AiPulse from your home screen or app list.' : 'You are using the installed app.';
        } else if (deferred) {
            if (button) button.hidden = false;
            message = 'Ready to install.';
        } else if (isIos()) {
            message = 'On iPhone and iPad: tap Share, then Add to Home Screen (steps below).';
        } else if (status && status.textContent.trim() === '') {
            message = 'If your browser does not offer an Install button, follow the steps for your device below.';
        }
        if (status && message) status.textContent = message;
    }

    window.addEventListener('beforeinstallprompt', function (e) { e.preventDefault(); deferred = e; render(); });
    window.addEventListener('appinstalled', function () { installed = true; deferred = null; render(); });

    function promptInstall() {
        if (!deferred) return;
        var evt = deferred;
        deferred = null;
        evt.prompt();
        evt.userChoice.then(function (choice) {
            installed = !!choice && choice.outcome === 'accepted';
            render();
        }).catch(function () { render(); });
    }

    function copy(text) {
        var done = function () {
            var status = document.querySelector('[data-install-status]');
            if (status) status.textContent = 'Link copied. Paste it into a message to your phone.';
        };
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).then(done, function () { });
        } else {
            var t = document.createElement('textarea');
            t.value = text;
            document.body.appendChild(t);
            t.select();
            try { document.execCommand('copy'); done(); } catch (e) { /* nothing more to try */ }
            t.remove();
        }
    }

    function pageUrl() {
        var el = document.querySelector('[data-page-url]');
        return el ? el.textContent.trim() : location.href;
    }

    document.addEventListener('click', function (e) {
        var t = e.target.closest ? e.target.closest('[data-install-button],[data-share-button],[data-copy-link]') : null;
        if (!t) return;
        if (t.hasAttribute('data-install-button')) promptInstall();
        else if (t.hasAttribute('data-share-button')) {
            if (navigator.share) navigator.share({ title: 'AiPulse', text: 'Get AiPulse on your phone', url: pageUrl() }).catch(function () { });
            else copy(pageUrl());
        } else copy(pageUrl());
    });

    document.addEventListener('DOMContentLoaded', render);
    window.addEventListener('load', function () {
        render();
        if (window.Blazor && Blazor.addEventListener) Blazor.addEventListener('enhancedload', render);
    });
})();
