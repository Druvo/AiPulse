// "Back to top" button - lives once in MainLayout, so it persists across Blazor Server's
// in-app navigation (no full page reload), meaning this only needs to run once per page load.
(function () {
    init();

    function init() {
        var btn = document.getElementById('scroll-top-btn');
        if (!btn) return;

        window.addEventListener('scroll', function () {
            btn.classList.toggle('show', window.scrollY > 400);
        }, { passive: true });

        btn.addEventListener('click', function () {
            window.scrollTo({ top: 0, behavior: 'smooth' });
        });
    }
})();
