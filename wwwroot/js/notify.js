// Browser desktop-notification helpers for AiPulse.
window.aipulseNotify = {
    async requestPermission() {
        if (!("Notification" in window)) return "unsupported";
        if (Notification.permission === "granted" || Notification.permission === "denied")
            return Notification.permission;
        return await Notification.requestPermission();
    },
    permission() {
        return ("Notification" in window) ? Notification.permission : "unsupported";
    },
    show(title, body, url) {
        if (!("Notification" in window) || Notification.permission !== "granted") return;
        const n = new Notification(title, { body: body, icon: "/favicon.png", tag: url || title });
        n.onclick = function () {
            window.focus();
            if (url) window.open(url, "_blank", "noopener");
            n.close();
        };
    }
};
