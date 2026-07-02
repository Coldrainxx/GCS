
(function () {
    "use strict";

    var cfg = window.GCS_CONFIG || { centerLat: 40.4093, centerLon: 49.8671 };

    var map = null;
    var ready = false;

    var uavMarker = null, uavArrow = null, uavEl = null;
    var hasFirstPosition = false;
    var followUAV = true, userMovedMap = false;
    var is3D = false;

    var waypoints = [];        // { marker, lat, lon, type, radius }
    var distanceLabels = [];   // HTML markers
    var trail = [];            // [lon,lat] history
    var MAX_TRAIL_POINTS = 500;

    // 3D UAV model (Three.js custom layer). 
    var MODEL_URL = "models/WCR.master_1.stl";
    var MODEL_SIZE_M = 30;      // approx on-ground size in metres (visibility)
    var HEADING_OFFSET = 0;     // degrees, if the nose points the wrong way
    var MODEL_BASE_TILT = 0; // STL Z-up -> map ground plane
    var ARROW_HEADING_OFFSET = 90;  // 2D arrow rotation offset (deg); flip sign if it points the wrong way
    var ALT_EXAGGERATION = 1;       // multiply altitude for visibility in 3D (1 = true scale)
    var PITCH_SIGN = -1;             // flip to -1 if the model noses up when it should nose down
    var ROLL_SIGN = 1;              // flip to -1 if the model banks the wrong way
    var modelReady = false;
    var uav = { lng: cfg.centerLon, lat: cfg.centerLat, heading: 0, alt: 0, roll: 0, pitch: 0 };

    // ── Basemap style ───────────────────────────────────────────────
    // Note the Esri tile URL order is {z}/{y}/{x}, not {z}/{x}/{y}.
    function buildStyle() {
        return {
            version: 8,
            sources: {
                satellite: {
                    type: "raster",
                    tiles: ["https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"],
                    tileSize: 256, maxzoom: 19,
                    attribution: "Imagery © Esri, Maxar, Earthstar Geographics"
                },
                // Transparent label/road overlays for place + road names on the imagery.
                ref_transport: {
                    type: "raster",
                    tiles: ["https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Transportation/MapServer/tile/{z}/{y}/{x}"],
                    tileSize: 256, maxzoom: 19
                },
                ref_places: {
                    type: "raster",
                    tiles: ["https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}"],
                    tileSize: 256, maxzoom: 19
                }
            },
            layers: [
                { id: "bg", type: "background", paint: { "background-color": "#0b1a2b" } },
                { id: "satellite", type: "raster", source: "satellite" },
                { id: "ref-transport", type: "raster", source: "ref_transport" },
                { id: "ref-places", type: "raster", source: "ref_places" }
            ]
        };
    }

    // ── Overlay sources (circles, path, trail) ──────────────────────
    function empty() { return { type: "FeatureCollection", features: [] }; }
    function lineFeature(coords) {
        return { type: "Feature", geometry: { type: "LineString", coordinates: coords } };
    }

    function addOverlays() {
        map.addSource("wp-circles", { type: "geojson", data: empty() });
        map.addLayer({ id: "wp-circles-fill", type: "fill", source: "wp-circles",
            paint: { "fill-color": ["get", "color"], "fill-opacity": 0.08 } });
        map.addLayer({ id: "wp-circles-line", type: "line", source: "wp-circles",
            paint: { "line-color": ["get", "color"], "line-dasharray": [2, 2], "line-width": 1 } });

        map.addSource("wp-path", { type: "geojson", data: lineFeature([]) });
        map.addLayer({ id: "wp-path", type: "line", source: "wp-path",
            layout: { "line-cap": "round", "line-join": "round" },
            paint: { "line-color": "#58A6FF", "line-dasharray": [2, 2], "line-width": 3, "line-opacity": 0.8 } });

        map.addSource("trail", { type: "geojson", data: lineFeature([]) });
        map.addLayer({ id: "trail", type: "line", source: "trail",
            layout: { "line-cap": "round", "line-join": "round" },
            paint: { "line-color": "#FF9500", "line-width": 2, "line-opacity": 0.6 } });

        addUavModelLayer();
    }

    // 3D UAV model rendered with Three.js inside a MapLibre custom layer.
    // Only drawn in 3D (tilted) view; 2D uses the flat DOM arrow marker.
    function addUavModelLayer() {
        var layer = {
            id: "uav-3d",
            type: "custom",
            renderingMode: "3d",
            onAdd: function (m, gl) {
                this.camera = new THREE.Camera();
                this.scene = new THREE.Scene();
                this.scene.add(new THREE.AmbientLight(0xffffff, 0.75));
                var d1 = new THREE.DirectionalLight(0xffffff, 0.9); d1.position.set(0, -70, 100).normalize(); this.scene.add(d1);
                var d2 = new THREE.DirectionalLight(0xffffff, 0.5); d2.position.set(0, 70, 100).normalize(); this.scene.add(d2);

                var self = this;
                new THREE.STLLoader().load(MODEL_URL, function (geometry) {
                    geometry.computeVertexNormals();
                    geometry.center();
                    geometry.computeBoundingBox();
                    var size = new THREE.Vector3();
                    geometry.boundingBox.getSize(size);
                    self.modelRadius = Math.max(size.x, size.y, size.z) || 1;
                    self.mesh = new THREE.Mesh(
                        geometry,
                        new THREE.MeshPhongMaterial({ color: 0xff9500, shininess: 25 }));
                    self.scene.add(self.mesh);
                    modelReady = true;
                    if (is3D && map) map.triggerRepaint();
                }, undefined, function (err) { console.error("[map] STL load failed:", err); });

                this.renderer = new THREE.WebGLRenderer({ canvas: m.getCanvas(), context: gl, antialias: true });
                this.renderer.autoClear = false;
            },
            render: function (gl, matrix) {
                if (!modelReady || !is3D) return;
                var merc = maplibregl.MercatorCoordinate.fromLngLat([uav.lng, uav.lat], uav.alt);
                var scale = (MODEL_SIZE_M / this.modelRadius) * merc.meterInMercatorCoordinateUnits();

                var d2r = Math.PI / 180;
                var l = new THREE.Matrix4()
                    .makeTranslation(merc.x, merc.y, merc.z)
                    .multiply(new THREE.Matrix4().makeScale(scale, -scale, scale))
                    .multiply(new THREE.Matrix4().makeRotationZ(-(uav.heading + HEADING_OFFSET) * d2r)) 
                    .multiply(new THREE.Matrix4().makeRotationX(uav.roll * ROLL_SIGN * d2r))           
                    .multiply(new THREE.Matrix4().makeRotationY(uav.pitch * PITCH_SIGN * d2r))             
                    .multiply(new THREE.Matrix4().makeRotationX(MODEL_BASE_TILT));                       

                this.camera.projectionMatrix = new THREE.Matrix4().fromArray(matrix).multiply(l);
                this.renderer.resetState();
                this.renderer.render(this.scene, this.camera);
            }
        };
        map.addLayer(layer);
    }

    // ── Geometry helpers ────────────────────────────────────────────
    function getColor(type) {
        switch (type) {
            case "HOME": return "#39D0D8";
            case "TKOF": return "#3FB950";
            case "LAND": return "#F85149";
            case "LOIT": return "#FF9500";
            case "RTL":  return "#A371F7";
            default:     return "#58A6FF";
        }
    }

    function geodesicCircle(lat, lon, radiusM, points) {
        points = points || 64;
        var coords = [];
        var latR = lat * Math.PI / 180;
        var dLat = (radiusM / 6378137) * 180 / Math.PI;
        var dLon = dLat / Math.cos(latR);
        for (var i = 0; i <= points; i++) {
            var t = (i / points) * 2 * Math.PI;
            coords.push([lon + dLon * Math.cos(t), lat + dLat * Math.sin(t)]);
        }
        return coords;
    }

    function getBearing(lat1, lon1, lat2, lon2) {
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var y = Math.sin(dLon) * Math.cos(lat2 * Math.PI / 180);
        var x = Math.cos(lat1 * Math.PI / 180) * Math.sin(lat2 * Math.PI / 180) -
                Math.sin(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) * Math.cos(dLon);
        return (Math.atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    function haversine(a, b) {
        var R = 6371000;
        var dLat = (b[1] - a[1]) * Math.PI / 180;
        var dLon = (b[0] - a[0]) * Math.PI / 180;
        var la1 = a[1] * Math.PI / 180, la2 = b[1] * Math.PI / 180;
        var h = Math.sin(dLat / 2) ** 2 + Math.cos(la1) * Math.cos(la2) * Math.sin(dLon / 2) ** 2;
        return 2 * R * Math.asin(Math.sqrt(h));
    }

    // ── Waypoints (exposed to WPF) ──────────────────────────────────
    function wpElement(index, color) {
        var el = document.createElement("div");
        el.className = "wp-marker";
        el.innerHTML =
            '<svg width="32" height="32" viewBox="0 0 32 32">' +
            '<circle cx="16" cy="16" r="14" fill="' + color + '" stroke="white" stroke-width="3"/>' +
            '<text x="16" y="21" text-anchor="middle" fill="white" font-size="12" font-weight="bold">' + index + '</text>' +
            '</svg>';
        return el;
    }

    window.addWaypoint = function (lat, lon, index, type, radius) {
        if (!ready) return;
        var color = getColor(type);
        var marker = new maplibregl.Marker({ element: wpElement(index, color), draggable: true })
            .setLngLat([lon, lat]).addTo(map);

        marker.on("dragend", function () {
            var p = marker.getLngLat();
            postMsg("drag:" + index + "," + p.lat + "," + p.lng);
        });

        waypoints[index] = { marker: marker, lat: lat, lon: lon, type: type, radius: radius };
        refreshGeometry();
    };

    window.updateWaypoint = function (index, lat, lon, type, radius) {
        if (!ready) return;
        var wp = waypoints[index];
        if (!wp) return;
        wp.lat = lat; wp.lon = lon; wp.type = type; wp.radius = radius;
        wp.marker.setLngLat([lon, lat]);
        refreshGeometry();
    };

    window.clearWaypoints = function () {
        if (!ready) return;
        waypoints.forEach(function (wp) { if (wp) wp.marker.remove(); });
        waypoints = [];
        refreshGeometry();
    };

    window.updatePathLine = function () { if (ready) refreshGeometry(); };

    function refreshGeometry() {
        var pts = waypoints.filter(Boolean);

        // Acceptance-radius circles.
        var circles = pts.map(function (wp) {
            return {
                type: "Feature",
                properties: { color: getColor(wp.type) },
                geometry: { type: "Polygon", coordinates: [geodesicCircle(wp.lat, wp.lon, wp.radius || 10)] }
            };
        });
        map.getSource("wp-circles").setData({ type: "FeatureCollection", features: circles });

        // Path line + distance/bearing labels.
        var coords = pts.map(function (wp) { return [wp.lon, wp.lat]; });
        map.getSource("wp-path").setData(lineFeature(coords));

        distanceLabels.forEach(function (m) { m.remove(); });
        distanceLabels = [];
        for (var i = 0; i < coords.length - 1; i++) {
            var p1 = coords[i], p2 = coords[i + 1];
            var dist = haversine(p1, p2);
            var brg = getBearing(p1[1], p1[0], p2[1], p2[0]);
            var txt = (dist > 1000 ? (dist / 1000).toFixed(2) + " km" : dist.toFixed(0) + " m") + " | " + brg.toFixed(0) + "°";
            var el = document.createElement("div");
            el.className = "distance-label";
            el.textContent = txt;
            var m = new maplibregl.Marker({ element: el })
                .setLngLat([(p1[0] + p2[0]) / 2, (p1[1] + p2[1]) / 2]).addTo(map);
            distanceLabels.push(m);
        }
    }

    // ── UAV (exposed to WPF) ────────────────────────────────────────
    function createUav() {
        var el = document.createElement("div");
        el.className = "uav-icon";
        el.style.cssText = "width:24px;height:28px;";
        el.innerHTML = '<div id="uav-arrow" style="width:0;height:0;border-left:12px solid transparent;' +
            'border-right:12px solid transparent;border-bottom:28px solid #FF9500;' +
            'filter:drop-shadow(0 2px 4px rgba(0,0,0,0.5));transform-origin:center center;"></div>';
        uavMarker = new maplibregl.Marker({ element: el, rotationAlignment: "viewport" })
            .setLngLat([cfg.centerLon, cfg.centerLat]).addTo(map);
        uavArrow = el.querySelector("#uav-arrow");
        uavEl = el;
    }

    window.updateUAV = function (lat, lon, alt, gs, as, heading, centerMap, altRel) {
        if (!ready) return;
        if (lat === 0 && lon === 0) return;

        uav.lng = lon; uav.lat = lat; uav.heading = heading;
        uav.alt = (altRel || 0) * ALT_EXAGGERATION;   // height above launch, for the 3D model

        uavMarker.setLngLat([lon, lat]);
        if (uavArrow) uavArrow.style.transform = "rotate(" + (heading + ARROW_HEADING_OFFSET) + "deg)";
        if (uavEl) uavEl.style.display = is3D ? "none" : "";  // arrow hidden in 3D (model shown)
        if (is3D) map.triggerRepaint();

        trail.push([lon, lat]);
        if (trail.length > MAX_TRAIL_POINTS) trail.shift();
        if (trail.length > 1) map.getSource("trail").setData(lineFeature(trail));

        if (centerMap || (!hasFirstPosition && (lat !== 0 || lon !== 0))) {
            map.easeTo({ center: [lon, lat], zoom: 16, pitch: is3D ? 60 : 0 });
            followUAV = true; userMovedMap = false; updateFollowBtn();
        } else if (followUAV && !userMovedMap) {
            map.panTo([lon, lat], { duration: 200 });
        }
        hasFirstPosition = hasFirstPosition || (lat !== 0 || lon !== 0);

        document.getElementById("infoLat").textContent = lat.toFixed(6) + "°";
        document.getElementById("infoLon").textContent = lon.toFixed(6) + "°";
        document.getElementById("infoAlt").textContent = alt.toFixed(1) + " m";
        document.getElementById("infoGS").textContent = gs.toFixed(1) + " m/s";
        document.getElementById("infoHdg").textContent = heading.toFixed(0) + "°";
    };

    // Attitude (degrees) for the 3D model's pitch/roll. Updated separately from
    // position since ATTITUDE arrives at a different rate than GLOBAL_POSITION_INT.
    window.updateAttitude = function (roll, pitch) {
        uav.roll = roll;
        uav.pitch = pitch;
        if (is3D && map && ready) map.triggerRepaint();
    };

    window.updateGpsStatus = function (sats, fixType, hdop) {
        document.getElementById("infoSats").textContent = sats;
        document.getElementById("infoHdop").textContent = hdop.toFixed(1);
        var fixEl = document.getElementById("infoFix");
        fixEl.textContent = fixType;
        fixEl.className = "gps-fix " +
            (fixType.indexOf("RTK") >= 0 ? "gps-fix-rtk" :
             (fixType.indexOf("3D") >= 0 || fixType.indexOf("DGPS") >= 0) ? "gps-fix-3d" :
             fixType.indexOf("2D") >= 0 ? "gps-fix-2d" : "gps-fix-none");
    };

    window.clearTrail = function () {
        trail = [];
        if (ready) map.getSource("trail").setData(lineFeature([]));
    };

    // ── Buttons ─────────────────────────────────────────────────────
    function updateFollowBtn() {
        var b = document.getElementById("followBtn");
        b.textContent = followUAV ? "📍 Follow UAV" : "❌ Free mode";
        b.style.background = followUAV ? "#3FB950" : "#F85149";
    }

    function updateViewBtn() {
        var b = document.getElementById("viewBtn");
        b.textContent = is3D ? "🗺️ 2D View" : "🧭 3D View";
        b.style.background = is3D ? "#8957E5" : "#1F6FEB";
    }

    window.setView3D = function (on) {
        is3D = !!on;
        if (uavEl) uavEl.style.display = is3D ? "none" : "";  // arrow in 2D, STL model in 3D
        map.easeTo({ pitch: is3D ? 60 : 0, duration: 400 });
        if (is3D) map.triggerRepaint();
        updateViewBtn();
    };

    function postMsg(s) {
        if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(s);
    }

    // Right-click "Fly here" context menu -> asks the WPF side (which confirms).
    var flyMenu = null;
    function hideFlyMenu() { if (flyMenu) { flyMenu.remove(); flyMenu = null; } }
    function showFlyMenu(x, y, lat, lng) {
        hideFlyMenu();
        flyMenu = document.createElement("div");
        flyMenu.style.cssText = "position:absolute;z-index:1200;left:" + x + "px;top:" + y + "px;" +
            "background:#1C2128;border:1px solid #2D333D;border-radius:6px;padding:4px;box-shadow:0 2px 8px rgba(0,0,0,0.5);";
        var btn = document.createElement("button");
        btn.textContent = "✈ Fly here";
        btn.style.cssText = "background:transparent;color:#E6EDF3;border:none;cursor:pointer;" +
            "font:bold 12px 'Segoe UI';padding:6px 12px;white-space:nowrap;";
        btn.onmouseover = function () { btn.style.background = "#2D333D"; };
        btn.onmouseout = function () { btn.style.background = "transparent"; };
        btn.onclick = function (ev) { ev.stopPropagation(); postMsg("flyto:" + lat + "," + lng); hideFlyMenu(); };
        flyMenu.appendChild(btn);
        document.body.appendChild(flyMenu);
    }

    // ── Init ────────────────────────────────────────────────────────
    function init() {
        map = new maplibregl.Map({
            container: "map",
            style: buildStyle(),
            center: [cfg.centerLon, cfg.centerLat],
            zoom: 15,
            pitch: 0,
            attributionControl: false
        });

        map.on("dragstart", function () { userMovedMap = true; followUAV = false; updateFollowBtn(); hideFlyMenu(); });
        map.on("click", function (e) { hideFlyMenu(); postMsg("click:" + e.lngLat.lat + "," + e.lngLat.lng); });
        map.on("contextmenu", function (e) { showFlyMenu(e.point.x, e.point.y, e.lngLat.lat, e.lngLat.lng); });

        document.getElementById("followBtn").onclick = function () {
            followUAV = !followUAV; userMovedMap = !followUAV; updateFollowBtn();
            if (followUAV && uavMarker) map.easeTo({ center: uavMarker.getLngLat() });
        };
        document.getElementById("viewBtn").onclick = function () { window.setView3D(!is3D); };

        map.on("load", function () {
            addOverlays();
            createUav();
            ready = true;
            updateFollowBtn();
            updateViewBtn();
            postMsg("ready:1");
        });
    }

    if (document.readyState === "loading")
        document.addEventListener("DOMContentLoaded", init);
    else
        init();
})();
