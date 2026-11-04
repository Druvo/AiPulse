// "Sign in with Clerk" glue. Clerk's own component handles sign-in, sign-up and email verification; this script only
//   1. on /login   (#clerk-signin) mounts that component and sends the person on to /login/clerk when they finish, and
//   2. on /login/clerk (#clerk-finish) hands the new Clerk session token to Lumen, which verifies it and issues its own cookie.
// Clerk's session is then ended on purpose: Lumen keeps its own session, and a lingering Clerk one would sign the person
// straight back in right after they log out of Lumen.
(function () {
    'use strict';

    function say(el, message, isError) {
        var p = el.querySelector('.clerk-status');
        if (!p) { p = document.createElement('p'); p.className = 'clerk-status small mb-0'; el.appendChild(p); }
        p.textContent = message;
        p.classList.toggle('text-danger', !!isError);
        p.classList.toggle('text-muted', !isError);
    }

    function loadClerk(host, key) {
        if (window.Clerk) return Promise.resolve();
        return new Promise(function (resolve, reject) {
            var s = document.createElement('script');
            s.async = true;
            s.crossOrigin = 'anonymous';
            s.setAttribute('data-clerk-publishable-key', key);
            s.src = 'https://' + host + '/npm/@clerk/clerk-js@5/dist/clerk.browser.js';
            s.onload = resolve;
            s.onerror = function () { reject(new Error('Could not reach Clerk. Use your username and password instead.')); };
            document.head.appendChild(s);
        });
    }

    async function start(el, onReady) {
        if (el.dataset.started) return;
        el.dataset.started = '1';
        try {
            say(el, 'Loading…', false);
            await loadClerk(el.dataset.host, el.dataset.key);
            await window.Clerk.load();
            el.querySelector('.clerk-status').remove();
            await onReady(el);
        } catch (e) {
            say(el, (e && e.message) || 'Clerk sign-in failed.', true);
        }
    }

    async function mountSignIn(el) {
        var next = '/login/clerk?returnUrl=' + encodeURIComponent(el.dataset.returnUrl || '/dashboard') + '&remember=' + (el.dataset.remember || 'true');
        var box = document.createElement('div');
        el.appendChild(box);
        window.Clerk.mountSignIn(box, { forceRedirectUrl: next, signUpForceRedirectUrl: next });
    }

    async function finish(el) {
        var clerk = window.Clerk;
        if (!clerk.session) { location.replace('/login'); return; }
        say(el, 'Signing you in…', false);
        var token = await clerk.session.getToken();
        var res = await fetch('/auth/clerk/session', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ token: token, remember: el.dataset.remember !== 'false', returnUrl: el.dataset.returnUrl || null })
        });
        var body = await res.json().catch(function () { return {}; });
        try { await clerk.signOut(); } catch (e) { /* the Lumen cookie is what matters */ }
        if (res.ok && body.redirect) { location.replace(body.redirect); return; }
        say(el, (body && body.error) || 'Lumen did not accept that sign-in.', true);
        var back = document.createElement('a');
        back.href = '/login';
        back.className = 'd-block mt-2 small';
        back.textContent = 'Back to sign in';
        el.appendChild(back);
    }

    function init() {
        var signIn = document.getElementById('clerk-signin');
        if (signIn) start(signIn, mountSignIn);
        var done = document.getElementById('clerk-finish');
        if (done) start(done, finish);
    }

    document.addEventListener('DOMContentLoaded', init);
    // Blazor's enhanced navigation swaps page content without a full load (and does not run scripts inside it).
    window.addEventListener('load', function () { if (window.Blazor && Blazor.addEventListener) Blazor.addEventListener('enhancedload', init); });
})();
