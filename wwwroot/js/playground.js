// Keeps the Playground chat transcript scrolled to the latest message as tokens stream in.
window.scrollToBottom = function (id) {
    var el = document.getElementById(id);
    if (el) el.scrollTop = el.scrollHeight;
};

// Copies a full message's raw text (used by the per-bubble copy button).
window.copyText = function (text) {
    navigator.clipboard.writeText(text);
};

// Copies a single rendered code block's text - button is the element right before the <pre> it belongs to.
window.copyCodeBlock = function (btn) {
    var pre = btn.nextElementSibling;
    if (!pre) return;
    var code = pre.querySelector('code');
    var text = code ? code.innerText : pre.innerText;
    navigator.clipboard.writeText(text).then(function () {
        var original = btn.textContent;
        btn.textContent = 'Copied!';
        setTimeout(function () { btn.textContent = original; }, 1500);
    });
};
