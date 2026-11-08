// Small ES module for the learning/tools/admin pages (Settings section nav, Glossary deep links, copy buttons).
// Loaded on demand with JS.InvokeAsync<IJSObjectReference>("import", "./js/page-ui.js"), so no <script> tag is needed.

const reduceMotion = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

/** Scrolls to an element by id (smoothly unless the user prefers reduced motion) and moves focus there for keyboard users. */
export function scrollToId(id, focus) {
    const el = document.getElementById(id);
    if (!el) return false;
    el.scrollIntoView({ behavior: reduceMotion() ? 'auto' : 'smooth', block: 'start' });
    if (focus) {
        if (!el.hasAttribute('tabindex')) el.setAttribute('tabindex', '-1');
        el.focus({ preventScroll: true });
    }
    return true;
}

/** Copies text; falls back to a hidden textarea where the async clipboard API is unavailable (non-secure origins). */
export async function copy(text) {
    try {
        if (navigator.clipboard && window.isSecureContext) {
            await navigator.clipboard.writeText(text);
            return true;
        }
    } catch (e) { /* fall through */ }
    try {
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.setAttribute('readonly', '');
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        const ok = document.execCommand('copy');
        ta.remove();
        return ok;
    } catch (e) {
        return false;
    }
}

let spyObserver = null;

/**
 * Scroll-spy for a sectioned page: marks the nav link for the section currently in view with aria-current="true"
 * and keeps a mobile <select> in sync. Touches only DOM attributes Blazor does not render, so re-renders keep them.
 */
export function spy(navSelector, selectSelector, sectionSelector) {
    if (spyObserver) spyObserver.disconnect();
    const sections = Array.from(document.querySelectorAll(sectionSelector));
    if (!sections.length || !('IntersectionObserver' in window)) return;
    const visible = new Map();
    const mark = id => {
        document.querySelectorAll(navSelector + ' [data-section]').forEach(a => {
            if (a.dataset.section === id) a.setAttribute('aria-current', 'true'); else a.removeAttribute('aria-current');
        });
        const sel = document.querySelector(selectSelector);
        if (sel && sel.value !== id && document.activeElement !== sel) sel.value = id;
    };
    spyObserver = new IntersectionObserver(entries => {
        entries.forEach(e => visible.set(e.target.id, e.isIntersecting ? e.intersectionRatio : 0));
        const first = sections.find(s => (visible.get(s.id) || 0) > 0);
        if (first) mark(first.id);
    }, { rootMargin: '-80px 0px -55% 0px', threshold: [0, 0.01, 0.25] });
    sections.forEach(s => spyObserver.observe(s));
}

/** Briefly highlights an element (used after jumping to a glossary term). */
export function flash(id) {
    const el = document.getElementById(id);
    if (!el) return;
    el.classList.remove('is-flash');
    void el.offsetWidth;
    el.classList.add('is-flash');
    setTimeout(() => el.classList.remove('is-flash'), 1600);
}

/** Focuses an input by id (e.g. the "/" shortcut on the glossary search). */
export function focusId(id) {
    const el = document.getElementById(id);
    if (el) el.focus();
}

let slashHandler = null;
/** Pressing "/" anywhere outside a text field focuses the given input. */
export function slashFocus(id) {
    if (slashHandler) document.removeEventListener('keydown', slashHandler);
    slashHandler = e => {
        if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey) return;
        const t = e.target;
        if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
        const el = document.getElementById(id);
        if (!el) return;
        e.preventDefault();
        el.focus();
    };
    document.addEventListener('keydown', slashHandler);
}
