/**
 * admin-map.js
 * Leaflet office map for Offices/Create and Offices/Edit.
 * Extracted from admin.js — auto-initializes on DOM ready.
 */
(function () {
    'use strict';

    function initOfficeMap() {
        if (typeof L === 'undefined') return;

        var mapEl    = document.getElementById('map');
        var latEl    = document.getElementById('lat');
        var lonEl    = document.getElementById('lon');
        var radiusEl = document.getElementById('RadiusMeters');

        if (!mapEl || !latEl || !lonEl || !radiusEl) return;

        var btnMyLoc    = document.getElementById('btnUseMyLocation');
        var fallbackLat = parseFloat(mapEl.dataset.fallbackLat) || 6.116386;
        var fallbackLon = parseFloat(mapEl.dataset.fallbackLon) || 125.171617;

        var lat = parseFloat(latEl.value);
        var lon = parseFloat(lonEl.value);
        if (!isFinite(lat)) lat = fallbackLat;
        if (!isFinite(lon)) lon = fallbackLon;

        var radius = parseInt(radiusEl.value, 10);
        if (!isFinite(radius) || radius <= 0) radius = 100;

        var pos  = L.latLng(lat, lon);
        var map  = L.map('map');

        var tiles = L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            attribution: '&copy; OpenStreetMap contributors',
            crossOrigin: 'anonymous'
        });

        tiles.on('tileerror', function () {
            if (!mapEl.dataset.tileWarned) {
                mapEl.dataset.tileWarned = '1';
                console.warn('[admin-map] Map tiles unavailable. Enter coordinates manually.');
            }
        });

        tiles.addTo(map);
        map.setView(pos, 17);

        var marker = L.marker(pos, { draggable: true }).addTo(map);
        var circle = L.circle(pos, { radius: radius, color: '#2563eb', fillOpacity: 0.12, weight: 2 }).addTo(map);

        function updateFromLatLng(newPos) {
            marker.setLatLng(newPos);
            circle.setLatLng(newPos);
            latEl.value = newPos.lat.toFixed(7);
            lonEl.value = newPos.lng.toFixed(7);
        }

        marker.on('dragend', function () { updateFromLatLng(marker.getLatLng()); });
        map.on('click', function (e) { updateFromLatLng(e.latlng); map.panTo(e.latlng); });

        radiusEl.addEventListener('input', function () {
            var r = parseInt(radiusEl.value, 10);
            if (isFinite(r) && r > 0) circle.setRadius(r);
        });

        if (btnMyLoc) {
            btnMyLoc.addEventListener('click', function () {
                if (!navigator.geolocation) {
                    alert('Geolocation not available in this browser.');
                    return;
                }
                btnMyLoc.disabled = true;
                btnMyLoc.innerHTML = '<i class="fa-solid fa-spinner fa-spin me-1"></i>Locating...';
                navigator.geolocation.getCurrentPosition(
                    function (position) {
                        var ll = L.latLng(position.coords.latitude, position.coords.longitude);
                        updateFromLatLng(ll);
                        map.setView(ll, 17);
                        btnMyLoc.disabled = false;
                        btnMyLoc.innerHTML = '<i class="fa-solid fa-location-dot me-1"></i>Use my location';
                    },
                    function (err) {
                        var msgs = {
                            1: 'Location permission denied. Enable it in browser settings.',
                            2: 'Location unavailable. Drag the pin manually.',
                            3: 'Location timed out. Drag the pin manually.'
                        };
                        alert(msgs[err && err.code] || 'Location error.');
                        btnMyLoc.disabled = false;
                        btnMyLoc.innerHTML = '<i class="fa-solid fa-location-dot me-1"></i>Use my location';
                    },
                    { enableHighAccuracy: true, timeout: 12000, maximumAge: 0 }
                );
            });
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initOfficeMap);
    } else {
        initOfficeMap();
    }

}());
