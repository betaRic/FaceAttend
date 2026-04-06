// kiosk-location.js
// GPS handling, office cache, client-side and server-side office resolution, location state.
// Exposes window.KioskLocation — must be loaded after kiosk-config.js and kiosk-state.js.
(function () {
    'use strict';

    var _state   = null;
    var _EP      = null;
    var _token   = null;
    var _appBase = null;
    var _isMobile = false;
    var _ui      = null;

    // ── Office cache ───────────────────────────────────────────────────────────

    var _officesCache       = null;
    var _officesCacheExpiry = 0;
    var _OFFICES_CACHE_TTL  = 5 * 60 * 1000;
    var _officesFetching    = false;

    // ── GPS smoothing ──────────────────────────────────────────────────────────

    var _gpsSmoothed      = { lat: null, lon: null };
    var _lastProcessedGps = { lat: null, lon: null };

    // ── Office-resolve retry (exponential backoff) ─────────────────────────────

    var _resolveRetryCount = 0;

    function _onRateLimited(retryAfterSeconds) {
        _resolveRetryCount++;
        var backoffMs = Math.min(30000, 1000 * Math.pow(2, _resolveRetryCount));
        _state.officeResolveRetryUntil = Date.now() + Math.max(backoffMs, (retryAfterSeconds || 0) * 1000);
    }

    function _onResolveSuccess() {
        _resolveRetryCount = 0;
        _state.officeResolveRetryUntil = 0;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    function setCenterBlock(title, sub, show) {
        if (_ui.centerBlock)      _ui.centerBlock.classList.toggle('hidden', !show);
        if (_ui.centerBlockTitle) _ui.centerBlockTitle.textContent = title || '';
        if (_ui.centerBlockSub)   _ui.centerBlockSub.textContent  = sub   || '';
    }

    function _updateMap(show) {
        if (window.KioskMap) {
            KioskMap.update(_state.gps.lat, _state.gps.lon, _state.gps.accuracy, show);
        }
    }

    // ── humanizeResolveError ───────────────────────────────────────────────────

    function humanizeResolveError(code, retryAfter, requiredAccuracy) {
        var c = (code || '').toString().toUpperCase();

        if (c === 'RATE_LIMIT_EXCEEDED') {
            return {
                title:  'Location check is busy',
                sub:    'The kiosk is verifying too often. Please wait a moment and try again.',
                banner: 'Checking location...'
            };
        }
        if (c === 'GPS_REQUIRED') {
            return {
                title:  'Location is required',
                sub:    'Enable location services so the kiosk can verify the assigned office.',
                banner: 'Location required'
            };
        }
        if (c === 'GPS_ACCURACY') {
            return {
                title:  'Location is not accurate enough',
                sub:    requiredAccuracy
                    ? ('Move to an open area and wait until accuracy is within ' + requiredAccuracy + ' meters.')
                    : 'Move to an open area and try again.',
                banner: 'Accuracy too low'
            };
        }
        if (c === 'NO_OFFICES') {
            return {
                title:  'No active office is configured',
                sub:    'Please contact the system administrator.',
                banner: 'Office not configured'
            };
        }
        if (c === 'NO_OFFICE_NEARBY') {
            return {
                title:  'Outside allowed office area',
                sub:    'Move inside the DILG Region XII office radius to continue.',
                banner: 'Not in allowed area'
            };
        }
        if (c === 'GPS_DENIED') {
            return {
                title:  'Location access denied',
                sub:    'Allow location access to continue using the kiosk.',
                banner: 'Location denied'
            };
        }
        if (c === 'GPS_UNAVAILABLE') {
            return {
                title:  'Location unavailable',
                sub:    'The device could not detect the current location. Move to an open area and try again.',
                banner: 'Location unavailable'
            };
        }
        if (c === 'GPS_TIMEOUT') {
            return {
                title:  'Location request timed out',
                sub:    'Please wait a moment and try again.',
                banner: 'Location timeout'
            };
        }
        return {
            title:  'Unable to verify location',
            sub:    'Please wait a moment, then try again or contact the system administrator.',
            banner: 'Location check failed'
        };
    }

    // ── Office cache ───────────────────────────────────────────────────────────

    function fetchOfficesOnce(callback) {
        var now = Date.now();

        if (_officesCache !== null && now < _officesCacheExpiry) {
            if (callback) callback();
            return;
        }

        if (_officesFetching) {
            if (callback) setTimeout(function () { fetchOfficesOnce(callback); }, 300);
            return;
        }

        if (_officesCache !== null) {
            _officesCache = null;
            _state.officeVerifiedUntil = 0;
            _state.lastVerifiedLat     = null;
            _state.lastVerifiedLon     = null;
            if (window.KioskMap) KioskMap.resetBounds();
        }

        _officesFetching = true;

        fetch(_appBase + 'Kiosk/GetOfficesForMap', {
            method:      'GET',
            credentials: 'same-origin',
            headers:     { 'X-Requested-With': 'XMLHttpRequest' }
        })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (data) {
            _officesCache       = (data && data.offices) ? data.offices : [];
            _officesCacheExpiry = Date.now() + _OFFICES_CACHE_TTL;
            _officesFetching    = false;
            if (callback) callback();
        })
        .catch(function () {
            if (_officesCache === null) _officesCache = [];
            _officesCacheExpiry = Date.now() + (30 * 1000);
            _officesFetching    = false;
            if (callback) callback();
        });
    }

    // ── Location state ─────────────────────────────────────────────────────────

    function applyLocationUi() {
        var kind    = _state.locationState || 'pending';
        var orgName = (document.body.getAttribute('data-org-name') || 'DILG Region XII');

        if (_ui.idleOrgName) _ui.idleOrgName.textContent = orgName;
        if (_ui.kioskRoot)   _ui.kioskRoot.setAttribute('data-location-state', kind);
        if (document.body)   document.body.setAttribute('data-location-state', kind);

        if (_ui.officeLine) {
            _ui.officeLine.textContent = _state.currentOffice.name || _state.locationBanner || 'Checking location...';
        }

        if (_ui.idleStatusBadge) {
            _ui.idleStatusBadge.classList.remove('is-pending', 'is-ready', 'is-blocked');
            _ui.idleStatusBadge.classList.add(
                kind === 'allowed' ? 'is-ready'   :
                kind === 'blocked' ? 'is-blocked'  :
                'is-pending'
            );
            _ui.idleStatusBadge.textContent =
                kind === 'allowed' ? 'Location verified' :
                kind === 'blocked' ? 'Location blocked'  :
                'Checking location';
        }

        if (_ui.idleLocationTitle) _ui.idleLocationTitle.textContent = _state.locationTitle || 'Preparing kiosk';
        if (_ui.idleLocationSub)   _ui.idleLocationSub.textContent   = _state.locationSub   || '';

        var showCenter = (kind === 'blocked' && _ui.idleOverlay && _ui.idleOverlay.classList.contains('hidden'));
        setCenterBlock(_state.locationTitle, _state.locationSub, showCenter);

        _updateMap(kind === 'blocked' && _state.gps.lat != null);
    }

    function setLocationState(kind, title, sub, banner) {
        _state.locationState  = kind   || 'pending';
        _state.allowedArea    = (_state.locationState === 'allowed');
        _state.locationTitle  = title  || '';
        _state.locationSub    = sub    || '';
        _state.locationBanner = banner || '';
        applyLocationUi();
    }

    // ── Haversine ──────────────────────────────────────────────────────────────

    function gpsDistanceMeters(lat1, lon1, lat2, lon2) {
        if (lat1 == null || lon1 == null || lat2 == null || lon2 == null) return 0;
        var R    = 6371000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a    = Math.sin(dLat / 2) * Math.sin(dLat / 2) +
                   Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) *
                   Math.sin(dLon / 2) * Math.sin(dLon / 2);
        return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
    }

    // ── Client-side office resolve (Haversine) ─────────────────────────────────

    function resolveOfficeClientSide() {
        if (_officesCache === null) {
            fetchOfficesOnce(resolveOfficeClientSide);
            return;
        }

        var lat = _state.gps.lat;
        var lon = _state.gps.lon;
        var acc = _state.gps.accuracy;

        if (lat == null || lon == null) return;

        var requiredAcc = 50;
        if (acc == null || acc > requiredAcc) {
            setLocationState(
                'pending',
                'Low GPS accuracy',
                'Move to an open area. Current accuracy: ' + (acc ? Math.round(acc) + 'm' : 'unknown') + '.',
                'Low accuracy'
            );
            _updateMap(true);
            return;
        }

        if (_officesCache.length === 0) {
            setLocationState(
                'blocked',
                'No active office is configured',
                'Please contact the system administrator.',
                'Office not configured'
            );
            _updateMap(false);
            return;
        }

        var best = null, bestDist = Infinity;
        _officesCache.forEach(function (o) {
            if (!o.lat || !o.lon) return;
            var d = gpsDistanceMeters(lat, lon, o.lat, o.lon);
            var r = o.radius || 100;
            if (d <= r && d < bestDist) { best = o; bestDist = d; }
        });

        if (!best) {
            var nearest = null, nearestDist = Infinity;
            _officesCache.forEach(function (o) {
                if (!o.lat || !o.lon) return;
                var d = gpsDistanceMeters(lat, lon, o.lat, o.lon);
                if (d < nearestDist) { nearest = o; nearestDist = d; }
            });
            var distHint = nearest
                ? Math.round(nearestDist) + 'm from ' + nearest.name
                : 'No office nearby';
            setLocationState(
                'blocked',
                'Outside allowed office area',
                distHint + '. Move inside the office radius to continue.',
                'Not in allowed area'
            );
            _updateMap(true);
            return;
        }

        if (_state.locationState === 'allowed' && _state.currentOffice.name === best.name) return;

        _state.currentOffice.id        = best.id   || null;
        _state.currentOffice.name      = best.name;
        _state.officeVerifiedUntil     = Date.now() + (60 * 60 * 1000);
        _state.lastVerifiedByGPS       = true;
        _state.officeResolveRetryUntil = 0;
        _state.lastVerifiedLat         = lat;
        _state.lastVerifiedLon         = lon;

        setLocationState(
            'allowed',
            'Location verified',
            'You may now look at the camera for attendance.',
            best.name || 'Office verified'
        );
        _updateMap(false);
    }

    // ── Server-side office resolve (GPS) ───────────────────────────────────────

    function resolveOfficeIfNeeded() {
        var t = Date.now();

        if (_state.locationState === 'allowed' &&
            _state.lastVerifiedLat != null &&
            _state.gps.lat != null)
        {
            var drift = gpsDistanceMeters(
                _state.lastVerifiedLat, _state.lastVerifiedLon,
                _state.gps.lat,         _state.gps.lon);
            if (drift > 60) {
                _state.officeVerifiedUntil = 0;
                _state.lastVerifiedLat     = null;
                _state.lastVerifiedLon     = null;
            }
        }

        var gpsCoordsAvailable = (_state.gps.lat != null && _state.gps.lon != null && _state.gps.accuracy != null);
        var cacheIsGpsTrusted  = _state.lastVerifiedByGPS || !gpsCoordsAvailable;
        if (_state.locationState === 'allowed' &&
            _state.currentOffice &&
            _state.currentOffice.id &&
            t < (_state.officeVerifiedUntil || 0) &&
            cacheIsGpsTrusted) {
            return Promise.resolve();
        }

        if (t < (_state.officeResolveRetryUntil || 0)) return Promise.resolve();

        var CFG = KioskConfig.CFG;
        if (t - _state.lastResolveAt < CFG.server.resolveMs) return Promise.resolve();

        _state.lastResolveAt = t;

        if (_state.gps.lat == null || _state.gps.lon == null || _state.gps.accuracy == null) {
            if (!_isMobile) return resolveOfficeDesktopOnce();
            if (_state.locationState === 'pending') {
                setLocationState(
                    'pending',
                    'Checking location',
                    'Waiting for a GPS fix. Stay within the DILG Region XII office area.',
                    'Locating...'
                );
            }
            return Promise.resolve();
        }

        if (_state.locationState === 'pending') {
            setLocationState(
                'pending',
                'Checking location',
                'Please wait while we verify the office radius.',
                'Checking location...'
            );
        }

        var fd = new FormData();
        fd.append('__RequestVerificationToken', _token);
        fd.append('lat',      _state.gps.lat);
        fd.append('lon',      _state.gps.lon);
        fd.append('accuracy', _state.gps.accuracy);

        return fetch(_EP.resolveOffice, { method: 'POST', body: fd })
            .then(function (r) {
                if (r.status === 429) {
                    return { ok: false, error: 'RATE_LIMIT_EXCEEDED', retryAfter: Number(r.headers.get('Retry-After') || 0) };
                }
                return r.json();
            })
            .then(function (j) {
                if (!j || j.ok !== true) {
                    if (j && j.error === 'RATE_LIMIT_EXCEEDED') {
                        _onRateLimited(Number(j.retryAfter || 0));
                        var mappedBusy = humanizeResolveError(j.error, j.retryAfter, j.requiredAccuracy);
                        setLocationState('pending', mappedBusy.title, mappedBusy.sub, mappedBusy.banner);
                        return;
                    }
                    setLocationState(
                        'blocked',
                        'Unable to verify location',
                        (j && j.error) ? String(j.error) : 'Please try again or contact the system administrator.',
                        'Location check failed'
                    );
                    return;
                }

                if (j.allowed === false) {
                    _state.currentOffice.id   = null;
                    _state.currentOffice.name = null;
                    _state.officeVerifiedUntil = 0;
                    var mappedBlocked = humanizeResolveError(j.reason || j.error, j.retryAfter, j.requiredAccuracy);
                    setLocationState('blocked', mappedBlocked.title, mappedBlocked.sub, mappedBlocked.banner);
                    return;
                }

                _state.currentOffice.id        = j.officeId;
                _state.currentOffice.name      = j.officeName;
                _state.officeVerifiedUntil     = Date.now() + (_isMobile ? 60 * 1000 : 5 * 60 * 1000);
                _state.lastVerifiedByGPS       = true;
                _state.lastVerifiedLat         = _state.gps.lat;
                _state.lastVerifiedLon         = _state.gps.lon;
                _onResolveSuccess();

                setLocationState(
                    'allowed',
                    'Location verified',
                    'You may now look at the camera for attendance.',
                    _state.currentOffice.name || 'Office verified'
                );
            })
            .catch(function () {
                setLocationState(
                    'blocked',
                    'Location check failed',
                    'The kiosk could not verify the office location. Please try again.',
                    'Location check failed'
                );
            });
    }

    // ── Desktop/fallback office resolve (server, no GPS) ──────────────────────

    function resolveOfficeDesktopOnce() {
        var t = Date.now();

        if (_isMobile) return Promise.resolve();

        if (_state.currentOffice &&
            _state.currentOffice.name &&
            _state.locationState === 'allowed' &&
            t < (_state.officeVerifiedUntil || 0)) {
            return Promise.resolve();
        }

        if (t < (_state.officeResolveRetryUntil || 0)) return Promise.resolve();

        if (_state.locationState === 'pending') {
            setLocationState(
                'pending',
                'Checking kiosk office',
                'Looking for the registered office profile for this kiosk.',
                'Checking office...'
            );
        }

        var fd = new FormData();
        fd.append('__RequestVerificationToken', _token);

        return fetch(_EP.resolveOffice, { method: 'POST', body: fd })
            .then(function (r) {
                if (r.status === 429) {
                    var retry = Number(r.headers.get('Retry-After') || 0);
                    return { ok: false, error: 'RATE_LIMIT_EXCEEDED', retryAfter: retry };
                }
                return r.json();
            })
            .then(function (j) {
                if (j && j.ok === true && j.allowed !== false) {
                    _state.currentOffice.id        = j.officeId;
                    _state.currentOffice.name      = j.officeName;
                    _state.officeVerifiedUntil     = Date.now() + (5 * 60 * 1000);
                    _state.lastVerifiedByGPS       = false;
                    _state.lastVerifiedLat         = _state.gps.lat;
                    _state.lastVerifiedLon         = _state.gps.lon;
                    _onResolveSuccess();
                    setLocationState(
                        'allowed',
                        'Location verified',
                        'You may now look at the camera for attendance.',
                        _state.currentOffice.name || 'Office verified'
                    );
                    return;
                }

                _onRateLimited(Number(j && j.retryAfter || 0));

                var mapped = humanizeResolveError(j && (j.reason || j.error), j && j.retryAfter, j && j.requiredAccuracy);
                setLocationState('blocked', mapped.title, mapped.sub, mapped.banner);
            })
            .catch(function () {
                var mapped = humanizeResolveError('UNKNOWN');
                setLocationState('blocked', mapped.title, mapped.sub, mapped.banner);
            });
    }

    // ── GPS watchPosition ──────────────────────────────────────────────────────

    function startGpsIfAvailable() {
        setLocationState(
            'pending',
            'Preparing kiosk',
            'Please wait while the kiosk verifies the current office location.',
            'Checking location...'
        );

        if (!('geolocation' in navigator)) {
            if (!_isMobile) { resolveOfficeDesktopOnce(); return; }
            setLocationState('blocked', 'Location not available', 'Enable location services to use the DILG Region XII kiosk.', 'Location not available');
            return;
        }

        var isSecure = (location.protocol === 'https:' || location.hostname === 'localhost' || location.hostname === '127.0.0.1');
        if (!isSecure) {
            if (!_isMobile) { resolveOfficeDesktopOnce(); return; }
            setLocationState('blocked', 'Secure connection required', 'Use HTTPS so the kiosk can access location services.', 'HTTPS required');
            return;
        }

        navigator.geolocation.watchPosition(
            function (pos) {
                var rawLat = pos.coords.latitude;
                var rawLon = pos.coords.longitude;
                var acc    = pos.coords.accuracy;

                var alpha = 0.25;
                if (_gpsSmoothed.lat === null) {
                    _gpsSmoothed.lat = rawLat;
                    _gpsSmoothed.lon = rawLon;
                } else {
                    _gpsSmoothed.lat = alpha * rawLat + (1 - alpha) * _gpsSmoothed.lat;
                    _gpsSmoothed.lon = alpha * rawLon + (1 - alpha) * _gpsSmoothed.lon;
                }

                _state.gps.lat      = _gpsSmoothed.lat;
                _state.gps.lon      = _gpsSmoothed.lon;
                _state.gps.accuracy = acc;

                var phLat = _gpsSmoothed.lat, phLon = _gpsSmoothed.lon;
                if (phLat < 4.5 || phLat > 21.0 || phLon < 116.0 || phLon > 127.0) {
                    _gpsSmoothed.lat = null;
                    _gpsSmoothed.lon = null;
                    return;
                }

                var movedM = gpsDistanceMeters(
                    _lastProcessedGps.lat, _lastProcessedGps.lon,
                    _gpsSmoothed.lat, _gpsSmoothed.lon
                );
                if (_lastProcessedGps.lat !== null && movedM < 25) return;

                _lastProcessedGps.lat = _gpsSmoothed.lat;
                _lastProcessedGps.lon = _gpsSmoothed.lon;

                if (_isMobile) {
                    resolveOfficeClientSide();
                } else {
                    resolveOfficeDesktopOnce();
                }
            },
            function (err) {
                _state.gps.lat = _state.gps.lon = _state.gps.accuracy = null;

                if (!_isMobile) {
                    if (_state.locationState === 'pending') {
                        setLocationState('pending', 'Checking kiosk office', 'Looking for the registered office profile for this kiosk.', 'Checking office...');
                    }
                    resolveOfficeDesktopOnce();
                    return;
                }

                var title = 'Unable to get location';
                var sub   = 'Turn on location services and try again.';
                var banner = 'Location unavailable';

                if (err && err.code === 1) {
                    title  = 'Location access denied';
                    sub    = 'Allow location access to continue using the DILG Region XII kiosk.';
                    banner = 'Location denied';
                } else if (err && err.code === 2) {
                    title  = 'Location unavailable';
                    sub    = 'The device could not detect the current location. Move to an open area and try again.';
                    banner = 'Location unavailable';
                } else if (err && err.code === 3) {
                    title  = 'Location timed out';
                    sub    = 'The location request took too long. Please try again.';
                    banner = 'Location timeout';
                }

                setLocationState('blocked', title, sub, banner);
            },
            { enableHighAccuracy: true, maximumAge: 15000, timeout: 20000 }
        );
    }

    // ── Init ───────────────────────────────────────────────────────────────────

    function init(state, EP, token, appBase, isMobile, ui) {
        _state    = state;
        _EP       = EP;
        _token    = token;
        _appBase  = appBase;
        _isMobile = isMobile;
        _ui       = ui;
    }

    window.KioskLocation = {
        init:                    init,
        startGpsIfAvailable:     startGpsIfAvailable,
        resolveOfficeIfNeeded:   resolveOfficeIfNeeded,
        resolveOfficeDesktopOnce: resolveOfficeDesktopOnce,
        resolveOfficeClientSide: resolveOfficeClientSide,
        applyLocationUi:         applyLocationUi,
        setLocationState:        setLocationState,
        gpsDistanceMeters:       gpsDistanceMeters,
        fetchOfficesOnce:        fetchOfficesOnce,
        getOfficesCache:         function () { return _officesCache; }
    };
})();
