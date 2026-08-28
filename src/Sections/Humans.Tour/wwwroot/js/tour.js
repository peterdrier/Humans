// Tour landing page — hero slideshow, scroll-triggered fade-ups, and the dust
// canvas. Patterns lifted from nobodies.team so the two pages feel like siblings.
// Plain JS, no dependencies; everything degrades to a static page without it.
(function () {
    'use strict';

    // Hero slideshow — lazy-loads each image just before it's shown.
    function initSlideshow() {
        if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

        var slides = document.querySelectorAll('.tour-hero-slide');
        if (slides.length < 2) return;

        var current = 0;

        function loadSlide(index) {
            var slide = slides[index];
            if (slide.dataset.bg && !slide.style.backgroundImage) {
                slide.style.backgroundImage = "url('" + slide.dataset.bg + "')";
            }
        }

        function advance() {
            slides[current].classList.remove('active');
            current = (current + 1) % slides.length;
            loadSlide(current);
            slides[current].classList.add('active');
            loadSlide((current + 1) % slides.length);
        }

        loadSlide(1);
        setInterval(advance, 5000);
    }

    // Scroll fade-ups.
    function initScrollAnimations() {
        var animEls = document.querySelectorAll('.anim');
        if (!animEls.length || !('IntersectionObserver' in window)) {
            animEls.forEach(function (el) { el.classList.add('visible'); });
            return;
        }

        var observer = new IntersectionObserver(function (entries) {
            entries.forEach(function (entry) {
                if (entry.isIntersecting) {
                    entry.target.classList.add('visible');
                    observer.unobserve(entry.target);
                }
            });
        }, { threshold: 0.15, rootMargin: '0px 0px -40px 0px' });

        animEls.forEach(function (el) { observer.observe(el); });
    }

    // Dust particles — low-opacity warm motes drifting upward over the hero.
    function initHeroDust() {
        var canvas = document.getElementById('tour-dust-canvas');
        if (!canvas) return;
        if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

        var ctx = canvas.getContext('2d');
        var particles = [];
        var COUNT = 45;
        var raf;

        function resize() {
            canvas.width = canvas.offsetWidth;
            canvas.height = canvas.offsetHeight;
        }

        resize();
        window.addEventListener('resize', resize, { passive: true });

        for (var i = 0; i < COUNT; i++) {
            particles.push({
                x: Math.random(),
                y: Math.random(),
                r: 0.4 + Math.random() * 1.6,
                vx: (Math.random() - 0.45) * 0.0003,
                vy: -(0.00015 + Math.random() * 0.00025),
                opacity: 0.018 + Math.random() * 0.055,
                pulse: Math.random() * Math.PI * 2
            });
        }

        var tick = 0;
        function draw() {
            tick++;
            var w = canvas.width;
            var h = canvas.height;
            ctx.clearRect(0, 0, w, h);

            particles.forEach(function (p) {
                var breathe = 1 + 0.15 * Math.sin(tick * 0.018 + p.pulse);
                ctx.beginPath();
                ctx.arc(p.x * w, p.y * h, p.r, 0, Math.PI * 2);
                ctx.fillStyle = 'rgba(215, 155, 65, ' + (p.opacity * breathe) + ')';
                ctx.fill();

                p.x += p.vx;
                p.y += p.vy;

                if (p.y < -0.02) { p.y = 1.02; p.x = Math.random(); }
                if (p.x > 1.02) { p.x = -0.02; }
                if (p.x < -0.02) { p.x = 1.02; }
            });

            raf = requestAnimationFrame(draw);
        }

        draw();

        // Stop animating while the hero is off-screen.
        var hero = document.querySelector('.tour-hero');
        if (hero && 'IntersectionObserver' in window) {
            new IntersectionObserver(function (entries) {
                entries.forEach(function (e) {
                    if (e.isIntersecting) {
                        if (!raf) draw();
                    } else {
                        cancelAnimationFrame(raf);
                        raf = null;
                    }
                });
            }, { threshold: 0 }).observe(hero);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    function init() {
        initSlideshow();
        initScrollAnimations();
        initHeroDust();
    }
})();
