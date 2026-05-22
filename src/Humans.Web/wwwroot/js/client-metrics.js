// Reports the browser's screen resolution to the admin client-stats screen.
// Fires once per session (sessionStorage guard), best-effort via sendBeacon so it
// never blocks or disrupts the page. Resolution is the one client stat the server
// cannot read from request headers.
(function () {
    try {
        if (sessionStorage.getItem('cm_beacon_sent')) return;

        var screen = window.screen || {};
        var payload = JSON.stringify({
            screenWidth: screen.width || 0,
            screenHeight: screen.height || 0
        });

        var sent = navigator.sendBeacon
            && navigator.sendBeacon('/api/client-metrics',
                new Blob([payload], { type: 'application/json' }));

        if (sent) sessionStorage.setItem('cm_beacon_sent', '1');
    } catch (e) {
        /* best-effort telemetry; ignore failures */
    }
})();
