// Nav scroll-frost (N1b): transparent at rest over the hero, frosts past a small scroll threshold.
// rAF-throttled per Hallmark's motion discipline - never a raw 'scroll' listener doing layout work.
(function () {
    var nav = document.querySelector('.landing-nav');
    if (!nav) return;

    var ticking = false;
    function update() {
        nav.classList.toggle('is-scrolled', window.scrollY > 24);
        ticking = false;
    }
    window.addEventListener('scroll', function () {
        if (!ticking) {
            requestAnimationFrame(update);
            ticking = true;
        }
    }, { passive: true });
    update();
})();
