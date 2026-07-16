// News Feed keyboard navigation: j/k move focus, o/Enter open, m mark read, b bookmark, r refresh, ? help.
window.aipulseKeyboard = {
    _dotNetRef: null,
    _handler: null,

    register(dotNetRef) {
        this._dotNetRef = dotNetRef;
        if (this._handler) document.removeEventListener('keydown', this._handler);

        this._handler = (e) => {
            const tag = (document.activeElement && document.activeElement.tagName) || '';
            if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
            if (e.ctrlKey || e.metaKey || e.altKey) return;

            if (['j', 'k', 'o', 'm', 'b', 'r', '?', 'Enter', 'Escape'].includes(e.key)) {
                e.preventDefault();
                this._dotNetRef.invokeMethodAsync('HandleKey', e.key);
            }
        };
        document.addEventListener('keydown', this._handler);
    },

    unregister() {
        if (this._handler) document.removeEventListener('keydown', this._handler);
        this._handler = null;
        this._dotNetRef = null;
    },

    scrollToFocused(index) {
        const el = document.getElementById('news-item-' + index);
        if (el) el.scrollIntoView({ block: 'center', behavior: 'smooth' });
    },

    openLink(url) {
        if (url) window.open(url, '_blank', 'noopener');
    }
};
