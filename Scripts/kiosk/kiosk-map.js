// kiosk-map.js
// Idle Leaflet map: office markers, user position, walking route.
// Exposes window.KioskMap — must be loaded after kiosk-location.js.
(function () {
    'use strict';

    var _state     = null;
    var _ui        = null;

    var _idleLeafletMap      = null;
    var _idleUserMarker      = null;
    var _idleOfficeCircles   = [];
    var _idleRouteLayer      = null;
    var _idleNearestOffice   = null;
    var _idleMapBoundsFitted = false;
    var _idleMapVisible      = false;

    // ── Leaflet init ───────────────────────────────────────────────────────────

    function initIdleMap() {
        if (_idleLeafletMap) return;
        if (!_ui.idleMap) return;
        if (typeof L === 'undefined') return;

        _idleLeafletMap = L.map(_ui.idleMap, {
            zoomControl:        true,
            attributionControl: false,
            dragging:           true,
            touchZoom:          true,
            scrollWheelZoom:    false,
            doubleClickZoom:    true,
            boxZoom:            false,
            keyboard:           false
        });

        _idleLeafletMap.zoomControl.setPosition('bottomright');

        L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png', {
            maxZoom:     19,
            crossOrigin: 'anonymous'
        }).addTo(_idleLeafletMap);
    }

    // ── Route drawing ──────────────────────────────────────────────────────────

    function drawRoute(lat1, lon1, lat2, lon2) {
        if (_idleRouteLayer) {
            _idleLeafletMap.removeLayer(_idleRouteLayer);
            _idleRouteLayer = null;
        }

        var routeDist = KioskLocation.gpsDistanceMeters(lat1, lon1, lat2, lon2);
        if (routeDist > 50000) {
            _idleRouteLayer = L.polyline([[lat1, lon1], [lat2, lon2]], {
                color: '#3b82f6', weight: 3, opacity: 0.5, dashArray: '6 8'
            }).addTo(_idleLeafletMap);
            return;
        }

        var osrm = 'https://router.project-osrm.org/route/v1/walking/'
            + lon1 + ',' + lat1 + ';' + lon2 + ',' + lat2
            + '?geometries=geojson&overview=simplified';
        fetch(osrm)
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (data && data.routes && data.routes[0]) {
                    var coords = data.routes[0].geometry.coordinates.map(function (c) {
                        return [c[1], c[0]];
                    });
                    _idleRouteLayer = L.polyline(coords, {
                        color: '#3b82f6', weight: 4, opacity: 0.85
                    }).addTo(_idleLeafletMap);
                } else {
                    _idleRouteLayer = L.polyline([[lat1, lon1], [lat2, lon2]], {
                        color: '#3b82f6', weight: 3, opacity: 0.7, dashArray: '8 6'
                    }).addTo(_idleLeafletMap);
                }
            })
            .catch(function () {
                if (_idleLeafletMap) {
                    _idleRouteLayer = L.polyline([[lat1, lon1], [lat2, lon2]], {
                        color: '#3b82f6', weight: 3, opacity: 0.7, dashArray: '8 6'
                    }).addTo(_idleLeafletMap);
                }
            });
    }

    // ── Map update ─────────────────────────────────────────────────────────────

    function updateIdleMap(show) {
        var container = _ui.idleMapContainer || document.getElementById('idleMapContainer');
        if (!container) return;

        if (!show || typeof L === 'undefined') {
            container.classList.add('hidden');
            if (_idleMapVisible) {
                _idleMapVisible      = false;
                _idleMapBoundsFitted = false;
                _idleNearestOffice   = null;
            }
            return;
        }

        var lat = _state.gps.lat;
        var lon = _state.gps.lon;
        if (lat == null || lon == null) {
            container.classList.add('hidden');
            return;
        }

        container.classList.remove('hidden');
        initIdleMap();
        if (!_idleLeafletMap) return;

        if (!_idleMapVisible) {
            _idleMapVisible      = true;
            _idleMapBoundsFitted = false;
        }

        setTimeout(function () {
            _idleLeafletMap.invalidateSize();

            var accOk = _state.gps.accuracy != null && _state.gps.accuracy <= 2000;

            if (_idleUserMarker) {
                _idleUserMarker.setLatLng([lat, lon]);
                var el = _idleUserMarker.getElement();
                if (el) el.style.opacity = accOk ? '1' : '0';
            } else if (accOk) {
                var userIcon = L.divIcon({
                    className: '',
                    html:       '<div class="map-user-dot"><div class="map-user-ring"></div></div>',
                    iconSize:   [20, 20],
                    iconAnchor: [10, 10]
                });
                _idleUserMarker = L.marker([lat, lon], { icon: userIcon, zIndexOffset: 1000 })
                    .bindTooltip('You are here', { permanent: false, direction: 'top' })
                    .addTo(_idleLeafletMap);
            }

            if (KioskLocation.getOfficesCache() === null) {
                if (!_idleMapBoundsFitted) _idleLeafletMap.setView([lat, lon], 16);
                KioskLocation.fetchOfficesOnce(function () { updateIdleMap(true); });
                return;
            }

            var offices = KioskLocation.getOfficesCache();
            if (offices.length === 0) {
                if (!_idleMapBoundsFitted) {
                    _idleLeafletMap.setView([lat, lon], 16);
                    _idleMapBoundsFitted = true;
                }
                return;
            }

            var nearest = null, nearestDist = Infinity;
            offices.forEach(function (o) {
                if (!o.lat || !o.lon) return;
                var d = KioskLocation.gpsDistanceMeters(lat, lon, o.lat, o.lon);
                if (d < nearestDist) { nearest = o; nearestDist = d; }
            });
            if (!nearest) return;

            var distLabel = nearestDist < 1000
                ? Math.round(nearestDist) + ' m'
                : (nearestDist / 1000).toFixed(1) + ' km';
            var mapInfo = document.getElementById('idleMapInfo');
            if (mapInfo) {
                var accLabel = _state.gps.accuracy != null
                    ? (_state.gps.accuracy <= 50  ? '' + Math.round(_state.gps.accuracy) + 'm (good)'
                     : _state.gps.accuracy <= 500 ? '' + Math.round(_state.gps.accuracy) + 'm (low)'
                     : '' + (_state.gps.accuracy >= 1000
                         ? (_state.gps.accuracy / 1000).toFixed(1) + 'km (GPS unavailable)'
                         : Math.round(_state.gps.accuracy) + 'm (unreliable)'))
                    : 'GPS unknown';
                mapInfo.textContent = nearest.name + '    ' + distLabel + ' away    ' + accLabel;
            }

            var officeChanged = !_idleNearestOffice || _idleNearestOffice.name !== nearest.name;
            if (officeChanged) {
                _idleOfficeCircles.forEach(function (c) { _idleLeafletMap.removeLayer(c); });
                _idleOfficeCircles = [];
                _idleNearestOffice = nearest;

                var circle = L.circle([nearest.lat, nearest.lon], {
                    radius:      nearest.radius || 100,
                    color:       '#10b981',
                    fillColor:   '#10b981',
                    fillOpacity: 0.10,
                    weight:      2,
                    dashArray:   '5 4'
                }).addTo(_idleLeafletMap);

                var officeIcon = L.divIcon({
                    className: '',
                    html:       '<div class="map-office-pin"><i class="fa-solid fa-building"></i></div>',
                    iconSize:   [34, 42],
                    iconAnchor: [17, 42]
                });
                var pin = L.marker([nearest.lat, nearest.lon], { icon: officeIcon })
                    .bindTooltip(nearest.name, {
                        permanent: true,
                        direction: 'top',
                        offset:    [0, -44],
                        className: 'map-office-tooltip'
                    })
                    .addTo(_idleLeafletMap);

                _idleOfficeCircles.push(circle, pin);
                drawRoute(lat, lon, nearest.lat, nearest.lon);
            }

            if (!_idleMapBoundsFitted) {
                var pad    = window.innerWidth < 480 ? [50, 40] : [70, 60];
                var bounds = L.latLngBounds([[lat, lon], [nearest.lat, nearest.lon]]);
                _idleLeafletMap.fitBounds(bounds, { padding: pad, maxZoom: 17 });
                _idleMapBoundsFitted = true;
            } else {
                _idleLeafletMap.panTo([lat, lon], { animate: true, duration: 0.4 });
            }
        }, 100);
    }

    // ── Init ───────────────────────────────────────────────────────────────────

    function init(state, ui) {
        _state = state;
        _ui    = ui;
    }

    window.KioskMap = {
        init:         init,
        update:       updateIdleMap,
        hide:         function () { updateIdleMap(false); },
        resetBounds:  function () {
            _idleNearestOffice   = null;
            _idleMapBoundsFitted = false;
        }
    };
})();
