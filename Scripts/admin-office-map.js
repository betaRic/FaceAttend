(function () {
  function initOfficeMap() {
    if (typeof L === "undefined") return;

    var mapEl = document.getElementById("map");
    if (!mapEl) return;

    var latEl = document.getElementById("lat");
    var lonEl = document.getElementById("lon");
    var radiusEl = document.getElementById("RadiusMeters");
    if (!latEl || !lonEl || !radiusEl) return;

    var btnMyLoc = document.getElementById("btnUseMyLocation");

    var fallbackLat = parseFloat(mapEl.getAttribute("data-fallback-lat")) || 6.116386;
    var fallbackLon = parseFloat(mapEl.getAttribute("data-fallback-lon")) || 125.171617;

    var lat = parseFloat(latEl.value);
    var lon = parseFloat(lonEl.value);

    if (!isFinite(lat)) lat = fallbackLat;
    if (!isFinite(lon)) lon = fallbackLon;

    var radius = parseInt(radiusEl.value, 10);
    if (!isFinite(radius) || radius <= 0) radius = 100;

    var pos = L.latLng(lat, lon);

    var map = L.map("map");
    L.tileLayer("https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", {
      maxZoom: 19,
      attribution: "&copy; OpenStreetMap contributors"
    }).addTo(map);

    map.setView(pos, 16);

    var marker = L.marker(pos, { draggable: true }).addTo(map);
    var circle = L.circle(pos, {
      radius: radius,
      color: "#0d6efd",
      fillColor: "#0d6efd",
      fillOpacity: 0.10,
      weight: 2
    }).addTo(map);

    function syncInputs(a, b) {
      latEl.value = a.toFixed(7);
      lonEl.value = b.toFixed(7);
    }

    function syncMap() {
      var a = parseFloat(latEl.value);
      var b = parseFloat(lonEl.value);
      if (!isFinite(a) || !isFinite(b)) return;

      var p = L.latLng(a, b);
      marker.setLatLng(p);
      circle.setLatLng(p);
      map.panTo(p);
    }

    function syncRadius() {
      var r = parseInt(radiusEl.value, 10);
      if (!isFinite(r) || r <= 0) return;
      circle.setRadius(r);
    }

    map.on("click", function (e) {
      marker.setLatLng(e.latlng);
      circle.setLatLng(e.latlng);
      syncInputs(e.latlng.lat, e.latlng.lng);
    });

    marker.on("drag", function (e) {
      var p = e.target.getLatLng();
      circle.setLatLng(p);
      syncInputs(p.lat, p.lng);
    });

    latEl.addEventListener("change", syncMap);
    lonEl.addEventListener("change", syncMap);
    radiusEl.addEventListener("change", syncRadius);

      if (btnMyLoc && navigator.geolocation) {
          btnMyLoc.addEventListener("click", function () {
              btnMyLoc.disabled = true;
              btnMyLoc.textContent = "Locating...";

              navigator.geolocation.getCurrentPosition(
                  function (p) {
                      btnMyLoc.disabled = false;
                      btnMyLoc.textContent = "Use my location";

                      var acc = p.coords.accuracy;

                      // If accuracy is worse than 500m, warn the user — IP-based
                      // geolocation on Philippine networks is often off by 50-200km.
                      if (acc > 500) {
                          var proceed = confirm(
                              "GPS accuracy is low (" + Math.round(acc) + " m).\n\n" +
                              "Your network may be geolocating to the wrong city (e.g. Cotabato instead of General Santos).\n\n" +
                              "Do you still want to use this location? " +
                              "If not, click Cancel and drag the pin manually on the map."
                          );
                          if (!proceed) return;
                      }

                      syncInputs(p.coords.latitude, p.coords.longitude);
                      syncMap();
                  },
                  function (err) {
                      btnMyLoc.disabled = false;
                      btnMyLoc.textContent = "Use my location";

                      var msg = "Location error.";
                      if (err && err.code === 1) msg = "Location access denied. Enable it in browser settings.";
                      else if (err && err.code === 2) msg = "Location unavailable. Drag the pin manually.";
                      else if (err && err.code === 3) msg = "Location timed out. Drag the pin manually.";

                      alert(msg);
                  },
                  { enableHighAccuracy: true, timeout: 12000, maximumAge: 0 }
              );
          });
      }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initOfficeMap);
  } else {
    initOfficeMap();
  }
})();
