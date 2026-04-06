// admin-back-to-top.js
// Injects and manages the back-to-top button. Auto-initializes on DOMContentLoaded.
(function () {
    'use strict';

    function init() {
        var btn = document.createElement('button');
        btn.id = 'backToTop';
        btn.type = 'button';
        btn.setAttribute('aria-label', 'Back to top');
        btn.innerHTML = '<i class="fa-solid fa-chevron-up fa-xs"></i>';
        btn.style.cssText = [
            'position:fixed', 'bottom:1.5rem', 'right:1.5rem',
            'width:36px', 'height:36px', 'border-radius:50%',
            'background:var(--color-primary)', 'color:#fff', 'border:none',
            'cursor:pointer', 'display:none', 'align-items:center',
            'justify-content:center', 'z-index:900', 'box-shadow:var(--shadow-md)',
            'transition:opacity 0.2s,transform 0.2s', 'opacity:0'
        ].join(';');
        document.body.appendChild(btn);

        var visible = false;
        window.addEventListener('scroll', function () {
            if (window.scrollY > 400 && !visible) {
                visible = true; btn.style.display = 'flex';
                setTimeout(function () { btn.style.opacity = '1'; }, 10);
            } else if (window.scrollY <= 400 && visible) {
                visible = false; btn.style.opacity = '0';
                setTimeout(function () { btn.style.display = 'none'; }, 200);
            }
        }, { passive: true });

        btn.addEventListener('click', function () { window.scrollTo({ top: 0, behavior: 'smooth' }); });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
}());
