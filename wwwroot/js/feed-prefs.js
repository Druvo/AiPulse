// Per-browser reading preferences for the content pages (News density), loaded as an ES module by the components that need it,
// so no global <script> tag is required. Saved to localStorage and mirrored into a first-party cookie, which lets the server
// prerender the right layout on the next visit instead of flashing the default and then switching. Storage can throw (blocked
// site data, some private modes): a missing preference must never break the page.

export function get(key) {
    try { return localStorage.getItem(key); } catch (e) { return null; }
}

export function set(key, value) {
    try { localStorage.setItem(key, value); } catch (e) { }
    try { document.cookie = key + '=' + encodeURIComponent(value) + '; path=/; max-age=31536000; samesite=lax'; } catch (e) { }
}

// The app's top bar is sticky only on wider screens and wraps to two rows on some widths, so a sticky toolbar below it can't use a
// fixed offset. Publish the bar's real height (0 when it scrolls away) as --ap-sticky-top and keep it current.
export function trackStickyOffset() {
    if (window._apStickyTracked) { update(); return; }
    window._apStickyTracked = true;
    function update() {
        const bar = document.querySelector('.top-row');
        let h = 0;
        if (bar && getComputedStyle(bar).position === 'sticky') h = Math.ceil(bar.getBoundingClientRect().height);
        document.documentElement.style.setProperty('--ap-sticky-top', h + 'px');
    }
    window._apStickyUpdate = update;
    update();
    window.addEventListener('resize', update, { passive: true });
    if (window.ResizeObserver) {
        const bar = document.querySelector('.top-row');
        if (bar) new ResizeObserver(update).observe(bar);
    }
}

function update() { if (window._apStickyUpdate) window._apStickyUpdate(); }

// Global search: keep the highlighted result in view while moving through results with the arrow keys.
export function scrollActiveIntoView(containerId) {
    const box = document.getElementById(containerId);
    const el = box && box.querySelector('[aria-selected="true"]');
    if (el) el.scrollIntoView({ block: 'nearest' });
}

// Arrow/Enter handling for the global search box lives in the browser so a held-down arrow key doesn't round-trip per keypress
// before the caret stops moving; Blazor still receives the key and moves the highlight.
export function preventArrowDefault(input) {
    if (!input || input._apArrows) return;
    input._apArrows = true;
    input.addEventListener('keydown', function (e) {
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp') e.preventDefault();
    });
}
