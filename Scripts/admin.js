(function () {
    // Footer year
    var y = document.getElementById("footerYear");
    if (y) y.textContent = new Date().getFullYear();

    // Refresh button
    var btn = document.getElementById("btnRefresh");
    if (btn) {
        btn.addEventListener("click", function () {
            window.location.reload();
        });
    }

    // Bootstrap tooltips (safe even if none exist)
    if (window.bootstrap && bootstrap.Tooltip) {
        var tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
        tooltipTriggerList.forEach(function (el) { new bootstrap.Tooltip(el); });
    }
})();
