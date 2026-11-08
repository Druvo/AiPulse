// AiPulse productivity helpers (ES module, imported on demand by CommandPalette / KeyboardShortcuts / Reader / BookmarkCollections).
// Global keys: Ctrl/Cmd+K or "/" opens the command palette, "?" the shortcuts overlay, "g" then a letter jumps to a page.
// One listener for the whole page (module scope is a singleton), in the capture phase so it runs before page-level handlers.

const RECENT_KEY = 'aipulse-palette-recent';
const READER_KEY = 'aipulse-reader-prefs';
const state = { palette: null, shortcuts: null, go: {}, gAt: 0, installed: false, installPrompt: null };

function isTyping(el) {
    if (!el) return false;
    const tag = (el.tagName || '').toUpperCase();
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable;
}

function navigate(href) {
    if (window.Blazor && typeof window.Blazor.navigateTo === 'function') window.Blazor.navigateTo(href);
    else window.location.assign(href);
}

function call(ref, method, ...args) {
    if (!ref) return;
    ref.invokeMethodAsync(method, ...args).catch(() => { /* circuit gone; next init re-registers */ });
}

function onKeyDown(e) {
    if (e.defaultPrevented || e.isComposing) return;
    const key = e.key;

    if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && (key === 'k' || key === 'K')) {
        if (!state.palette) return;
        e.preventDefault();
        e.stopPropagation();
        call(state.palette, 'Toggle');
        return;
    }

    if (e.ctrlKey || e.metaKey || e.altKey || isTyping(e.target) || document.querySelector('.cp-dialog, .ks-dialog')) return;

    if (state.gAt && Date.now() - state.gAt < 1500) {
        state.gAt = 0;
        const href = state.go[key.toLowerCase()];
        if (href) {
            e.preventDefault();
            e.stopPropagation();
            navigate(href);
        }
        return;
    }

    if (key === '/' && state.palette) {
        e.preventDefault();
        e.stopPropagation();
        call(state.palette, 'Open');
    } else if (key === '?' && state.shortcuts) {
        e.preventDefault();
        e.stopPropagation();
        call(state.shortcuts, 'Toggle');
    } else if (key === 'g' && !e.shiftKey) {
        state.gAt = Date.now();
    }
}

if (!window.__aipulseProductivity) {
    window.__aipulseProductivity = true;
    window.addEventListener('keydown', onKeyDown, true);
    window.addEventListener('beforeinstallprompt', (e) => { e.preventDefault(); state.installPrompt = e; });
    window.addEventListener('appinstalled', () => { state.installed = true; });
    // A tiny global so other components (a nav button, a help page) can open the palette without interop plumbing.
    window.aipulsePalette = {
        open: () => call(state.palette, 'Open'),
        shortcuts: () => call(state.shortcuts, 'Toggle'),
    };
}

// Tokens (not the references) identify a registration: each interop call wraps a .NET reference in a fresh JS object.
export function registerPalette(ref, goMap, token) {
    state.palette = ref;
    state.paletteToken = token;
    state.go = goMap || {};
}

export function unregisterPalette(token) {
    if (state.paletteToken === token) { state.palette = null; state.go = {}; }
}

export function registerShortcuts(ref, token) { state.shortcuts = ref; state.shortcutsToken = token; }
export function unregisterShortcuts(token) { if (state.shortcutsToken === token) state.shortcuts = null; }
export function showShortcuts() { call(state.shortcuts, 'Open'); }

// Arrow keys and Enter drive the listbox; stop them moving the caret or submitting anything.
export function bindCombobox(input) {
    if (!input || input.__cpBound) return;
    input.__cpBound = true;
    input.addEventListener('keydown', (e) => {
        if (['ArrowDown', 'ArrowUp', 'Enter', 'PageDown', 'PageUp', 'Tab'].includes(e.key)) e.preventDefault();
    });
}

export function focus(el) { if (el) { try { el.focus({ preventScroll: true }); } catch { el.focus(); } } }

export function rememberFocus() { state.lastFocus = document.activeElement; }
export function restoreFocus() {
    const el = state.lastFocus;
    state.lastFocus = null;
    if (el && document.contains(el) && typeof el.focus === 'function') el.focus();
}

export function scrollOptionIntoView(id) {
    const el = document.getElementById(id);
    if (el) el.scrollIntoView({ block: 'nearest' });
}

export function setScrollLock(on) { document.documentElement.style.overflow = on ? 'hidden' : ''; }

export function getRecent() {
    try { return JSON.parse(localStorage.getItem(RECENT_KEY) || '[]').slice(0, 8); } catch { return []; }
}

export function pushRecent(item) {
    try {
        const list = getRecent().filter(r => r.href !== item.href);
        list.unshift(item);
        localStorage.setItem(RECENT_KEY, JSON.stringify(list.slice(0, 8)));
    } catch { /* storage blocked: recents are a convenience */ }
}

export function clearRecent() { try { localStorage.removeItem(RECENT_KEY); } catch { } }

export function go(href) { navigate(href); }
export function openExternal(href) { window.open(href, '_blank', 'noopener,noreferrer'); }

export function toggleTheme() { if (typeof window.toggleTheme === 'function') window.toggleTheme(); }

export function signOut() {
    const form = document.createElement('form');
    form.method = 'post';
    form.action = '/auth/logout';
    document.body.appendChild(form);
    form.submit();
}

export function downloadText(filename, text, mime) {
    const blob = new Blob([text], { type: mime || 'text/markdown;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function getReaderPrefs() {
    try { return JSON.parse(localStorage.getItem(READER_KEY) || 'null'); } catch { return null; }
}

export function setReaderPrefs(prefs) {
    try { localStorage.setItem(READER_KEY, JSON.stringify(prefs)); } catch { }
}

export function isStandalone() {
    return state.installed || (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) || window.navigator.standalone === true;
}

export function canPromptInstall() { return !!state.installPrompt; }

export async function promptInstall() {
    const p = state.installPrompt;
    if (!p) return false;
    state.installPrompt = null;
    p.prompt();
    try { const choice = await p.userChoice; return choice && choice.outcome === 'accepted'; } catch { return false; }
}
