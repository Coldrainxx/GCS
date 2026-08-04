
(function () {
    "use strict";

    var cfg = window.GCS_CONFIG || { centerLat: 40.4093, centerLon: 49.8671 };

    var map = null;
    var ready = false;

    var uavMarker = null, uavArrow = null, uavEl = null;
    var hasFirstPosition = false;
    var followUAV = true, userMovedMap = false;
    var logReviewMode = false;
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

    // ── Swarm ───────────────────────────────────────────────────────
    // One entry per vehicle: { id, lat, lng, alt, heading, roll, pitch,
    //                          leader, active, marker, el, mesh }
    var SWARM_MODEL_URL = "models/swarmdrone.stl";
    var swarm = {};                 // sysid -> vehicle
    var swarmGeometry = null;       // ONE shared BufferGeometry for every drone
    var swarmModelRadius = 1;
    var LEADER_COLOR = 0xFFB000, FOLLOWER_COLOR = 0x39D0D8;
    var LEADER_CSS = "#FFB000", FOLLOWER_CSS = "#39D0D8", ACTIVE_CSS = "#FFFFFF";

    // Followers used to share one colour, which made them impossible to tell
    // apart. Each vehicle gets a stable colour from this palette instead, used
    // for its marker, its 3D model and its trail, so all three agree.
    var VEHICLE_PALETTE = [
        "#39D0D8", "#3FB950", "#A371F7", "#F778BA",
        "#58A6FF", "#E3B341", "#FF7B72", "#7EE787"
    ];

    function colorCssFor(id, isLeader) {
        if (isLeader) return LEADER_CSS;
        return VEHICLE_PALETTE[Math.abs(id) % VEHICLE_PALETTE.length];
    }

    function colorHexFor(id, isLeader) {
        return parseInt(colorCssFor(id, isLeader).slice(1), 16);
    }

    // Per-vehicle position history: sysid -> [[lon,lat], ...]
    var swarmTrails = {};
    var MAX_SWARM_TRAIL_POINTS = 300;

    // Swarm mode is an app mode, not "more than one vehicle": in single-UAV mode
    // the map shows only the active drone, exactly like before the swarm work.
    var swarmMode = false;

    function swarmCount() { return Object.keys(swarm).length; }

    // Single source of truth for the legacy single-UAV arrow. Mode picks the
    // representation (single vs swarm), is3D picks flat arrow vs STL model.
    // Several callers update this at different rates, so letting each decide
    // for itself made it flicker.
    function refreshUavArrowVisibility() {
        // Also hidden while reviewing a log: the live aircraft's marker sitting on
        // a recorded path is confusing about which is which.
        if (uavEl) uavEl.style.display = (is3D || swarmMode || logReviewMode) ? "none" : "";
    }

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

        // One source holding a line per vehicle, coloured from each feature's own
        // property — so N trails cost one source and one layer.
        map.addSource("swarm-trails", { type: "geojson", data: empty() });
        map.addLayer({ id: "swarm-trails", type: "line", source: "swarm-trails",
            layout: { "line-cap": "round", "line-join": "round" },
            paint: { "line-color": ["get", "color"], "line-width": 2, "line-opacity": 0.55 } });

        // Formation preview: the shape from the leader out to each station, plus
        // how far each drone currently is from where it should be.
        map.addSource("formation", { type: "geojson", data: empty() });
        map.addLayer({ id: "formation-arms", type: "line", source: "formation",
            filter: ["==", ["get", "kind"], "arm"],
            layout: { "line-cap": "round" },
            paint: { "line-color": "#FFB000", "line-width": 1.5,
                     "line-dasharray": [2, 2], "line-opacity": 0.7 } });
        map.addLayer({ id: "formation-error", type: "line", source: "formation",
            filter: ["==", ["get", "kind"], "error"],
            paint: { "line-color": ["get", "color"], "line-width": 1.5, "line-opacity": 0.9 } });
        map.addLayer({ id: "formation-stations", type: "circle", source: "formation",
            filter: ["==", ["get", "kind"], "station"],
            paint: { "circle-radius": 5, "circle-color": "rgba(0,0,0,0)",
                     "circle-stroke-color": ["get", "color"], "circle-stroke-width": 2,
                     "circle-opacity": 0.9 } });

        // Recorded flight path, shown when reviewing a log. Split into armed and
        // unarmed segments so taxiing and bench time are visually distinct from
        // what was actually flown.
        map.addSource("log-track", { type: "geojson", data: empty() });
        map.addLayer({ id: "log-track-ground", type: "line", source: "log-track",
            filter: ["==", ["get", "kind"], "ground"],
            layout: { "line-cap": "round", "line-join": "round" },
            paint: { "line-color": "#58A6FF", "line-width": 2, "line-opacity": 0.5 } });
        map.addLayer({ id: "log-track-armed", type: "line", source: "log-track",
            filter: ["==", ["get", "kind"], "armed"],
            layout: { "line-cap": "round", "line-join": "round" },
            paint: { "line-color": "#3FB950", "line-width": 3, "line-opacity": 0.95 } });
        map.addLayer({ id: "log-track-ends", type: "circle", source: "log-track",
            filter: ["==", ["get", "kind"], "end"],
            paint: { "circle-radius": 6, "circle-color": ["get", "color"],
                     "circle-stroke-color": "#0D1117", "circle-stroke-width": 2 } });

        addUavModelLayer();
        addSwarmModelLayer();
    }

    // Every drone in the swarm, drawn from ONE shared geometry: the STL is large,
    // so it is loaded once and reused by every mesh rather than per vehicle.
    function addSwarmModelLayer() {
        var layer = {
            id: "swarm-3d",
            type: "custom",
            renderingMode: "3d",
            onAdd: function (m, gl) {
                this.camera = new THREE.Camera();
                this.scene = new THREE.Scene();
                this.scene.add(new THREE.AmbientLight(0xffffff, 0.8));
                var d1 = new THREE.DirectionalLight(0xffffff, 0.9); d1.position.set(0, -70, 100).normalize(); this.scene.add(d1);
                var d2 = new THREE.DirectionalLight(0xffffff, 0.5); d2.position.set(0, 70, 100).normalize(); this.scene.add(d2);

                var self = this;
                new THREE.STLLoader().load(SWARM_MODEL_URL, function (geometry) {
                    geometry.computeVertexNormals();
                    geometry.center();
                    geometry.computeBoundingBox();
                    var size = new THREE.Vector3();
                    geometry.boundingBox.getSize(size);
                    swarmModelRadius = Math.max(size.x, size.y, size.z) || 1;
                    swarmGeometry = geometry;

                    // ONE mesh, drawn once per vehicle. The model matrix must be
                    // folded into the projection matrix on the CPU (float64): the
                    // mercator scale is ~1e-8, so letting the GPU combine it with
                    // the map matrix in float32 destroys precision and shatters
                    // the mesh.
                    // One material per colour, created on demand and reused.
                    self.materials = {};
                    self.materialFor = function (id, isLeader) {
                        var hex = colorHexFor(id, isLeader);
                        return self.materials[hex] ||
                              (self.materials[hex] = new THREE.MeshPhongMaterial({ color: hex, shininess: 30 }));
                    };
                    self.mesh = new THREE.Mesh(geometry, self.materialFor(0, false));
                    self.scene.add(self.mesh);

                    if (is3D && map) map.triggerRepaint();
                }, undefined, function (err) { console.error("[map] swarm STL load failed:", err); });

                this.renderer = new THREE.WebGLRenderer({ canvas: m.getCanvas(), context: gl, antialias: true });
                this.renderer.autoClear = false;
            },
            render: function (gl, matrix) {
                if (!swarmMode) return;   // single-UAV mode draws the legacy model instead
                if (!swarmGeometry || !this.mesh || !is3D || swarmCount() === 0) return;

                var d2r = Math.PI / 180;
                var self = this;
                this.renderer.resetState();

                // One draw per vehicle. Each drone's model matrix is multiplied
                // into the map matrix here in JS (double precision) and handed to
                // the camera — the same path the single-UAV layer uses. Baking it
                // into the mesh instead would push that multiply onto the GPU in
                // float32 and tear the geometry apart at mercator scale.
                Object.keys(swarm).forEach(function (id) {
                    var v = swarm[id];
                    var merc = maplibregl.MercatorCoordinate.fromLngLat([v.lng, v.lat], v.alt);
                    var s = (MODEL_SIZE_M / swarmModelRadius) * merc.meterInMercatorCoordinateUnits();
                    if (v.leader) s *= 1.35;   // leader reads bigger

                    var l = new THREE.Matrix4()
                        .makeTranslation(merc.x, merc.y, merc.z)
                        .multiply(new THREE.Matrix4().makeScale(s, -s, s))
                        .multiply(new THREE.Matrix4().makeRotationZ(-(v.heading + HEADING_OFFSET) * d2r))
                        .multiply(new THREE.Matrix4().makeRotationX(v.roll * ROLL_SIGN * d2r))
                        .multiply(new THREE.Matrix4().makeRotationY(v.pitch * PITCH_SIGN * d2r))
                        .multiply(new THREE.Matrix4().makeRotationX(MODEL_BASE_TILT));

                    self.mesh.material = self.materialFor(v.id, v.leader);
                    self.camera.projectionMatrix = new THREE.Matrix4().fromArray(matrix).multiply(l);
                    self.renderer.render(self.scene, self.camera);
                });
            }
        };
        map.addLayer(layer);
    }

    // Flat marker used in 2D: a coloured chevron plus the vehicle label.
    function createSwarmMarker(v) {
        var el = document.createElement("div");
        el.className = "swarm-icon";
        el.style.cssText = "display:flex;flex-direction:column;align-items:center;pointer-events:none;";
        el.innerHTML =
            '<div class="swarm-arrow" style="width:0;height:0;border-left:9px solid transparent;' +
            'border-right:9px solid transparent;border-bottom:22px solid ' + FOLLOWER_CSS + ';' +
            'filter:drop-shadow(0 2px 3px rgba(0,0,0,0.6));transform-origin:center center;"></div>' +
            '<div class="swarm-label" style="margin-top:2px;font:bold 10px Consolas,monospace;' +
            'color:#fff;background:rgba(10,14,20,0.75);border-radius:3px;padding:1px 4px;white-space:nowrap;"></div>';
        v.el = el;
        v.arrowEl = el.querySelector(".swarm-arrow");
        v.labelEl = el.querySelector(".swarm-label");
        v.marker = new maplibregl.Marker({ element: el, rotationAlignment: "viewport" })
            .setLngLat([v.lng, v.lat]).addTo(map);
    }

    function styleSwarmMarker(v) {
        if (!v.arrowEl) return;
        var color = colorCssFor(v.id, v.leader);
        v.arrowEl.style.borderBottomColor = color;
        var h = v.leader ? 28 : 22, w = v.leader ? 11 : 9;
        v.arrowEl.style.borderBottomWidth = h + "px";
        v.arrowEl.style.borderLeftWidth = w + "px";
        v.arrowEl.style.borderRightWidth = w + "px";
        v.labelEl.textContent = (v.leader ? "★ " : "") + "UAV " + v.id +
                                (v.alt ? "  " + v.alt.toFixed(0) + "m" : "");
        v.labelEl.style.color = v.active ? ACTIVE_CSS : "#C9D1D9";
        v.labelEl.style.outline = v.active ? "1px solid " + ACTIVE_CSS : "none";
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
                // In swarm mode the fleet layer draws every vehicle, this one included.
                if (!modelReady || !is3D || swarmMode) return;
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
        refreshUavArrowVisibility();
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

    // ── Swarm (exposed to WPF) ──────────────────────────────────────
    // list: [{ id, lat, lon, alt, hdg, roll, pitch, leader, active }]
    // Vehicles missing from the list are removed, so this is the whole picture.
    window.updateSwarm = function (list) {
        if (!ready || !swarmMode) return;
        try {
            if (typeof list === "string") list = JSON.parse(list);
        } catch (e) { console.error("[map] bad swarm payload:", e); return; }
        if (!Array.isArray(list)) return;

        var seen = {};
        list.forEach(function (d) {
            if (d == null || d.id == null) return;
            if (d.lat === 0 && d.lon === 0) return;   // no fix yet — don't place it at null island
            var id = String(d.id);
            seen[id] = true;

            var v = swarm[id];
            if (!v) {
                v = swarm[id] = { id: d.id, lat: d.lat, lng: d.lon, alt: 0,
                                  heading: 0, roll: 0, pitch: 0, leader: false, active: false };
                createSwarmMarker(v);
            }

            v.lat = d.lat; v.lng = d.lon;
            v.alt = (d.alt || 0) * ALT_EXAGGERATION;
            v.heading = d.hdg || 0;
            v.roll = d.roll || 0;
            v.pitch = d.pitch || 0;

            v.leader = !!d.leader;
            v.active = !!d.active;
            v.stationLat = (typeof d.slat === "number") ? d.slat : null;
            v.stationLon = (typeof d.slon === "number") ? d.slon : null;

            v.marker.setLngLat([v.lng, v.lat]);
            if (v.arrowEl)
                v.arrowEl.style.transform = "rotate(" + (v.heading + ARROW_HEADING_OFFSET) + "deg)";
            styleSwarmMarker(v);
            if (v.el) v.el.style.display = is3D ? "none" : "";

            // Each vehicle keeps its own history, so switching the active drone
            // no longer makes one shared trail jump across the map.
            var track = swarmTrails[id] || (swarmTrails[id] = []);
            var last = track[track.length - 1];
            if (!last || last[0] !== v.lng || last[1] !== v.lat) {
                track.push([v.lng, v.lat]);
                if (track.length > MAX_SWARM_TRAIL_POINTS) track.shift();
            }
        });

        // Drop vehicles that are no longer reported.
        Object.keys(swarm).forEach(function (id) {
            if (seen[id]) return;
            var v = swarm[id];
            if (v.marker) v.marker.remove();
            delete swarm[id];
            delete swarmTrails[id];
        });

        refreshSwarmTrails();
        refreshFormationPreview();

        // With a swarm on screen the single-UAV arrow/model would double-draw.
        refreshUavArrowVisibility();
        if (is3D) map.triggerRepaint();
    };

    function refreshSwarmTrails() {
        if (!ready || !map.getSource("swarm-trails")) return;
        var features = [];
        Object.keys(swarmTrails).forEach(function (id) {
            var track = swarmTrails[id];
            if (!track || track.length < 2) return;
            var v = swarm[id];
            features.push({
                type: "Feature",
                properties: { color: colorCssFor(v ? v.id : parseInt(id, 10), v && v.leader) },
                geometry: { type: "LineString", coordinates: track }
            });
        });
        map.getSource("swarm-trails").setData({ type: "FeatureCollection", features: features });
    }

    // Draws the intended formation: a dashed arm from the leader to every station,
    // a ring at each station, and a solid line showing how far each drone is from
    // the station it should be holding.
    function refreshFormationPreview() {
        if (!ready || !map.getSource("formation")) return;

        var features = [];
        var leader = null;
        Object.keys(swarm).forEach(function (id) { if (swarm[id].leader) leader = swarm[id]; });

        if (leader) {
            Object.keys(swarm).forEach(function (id) {
                var v = swarm[id];
                if (v.leader || v.stationLat == null) return;
                var color = colorCssFor(v.id, false);
                var station = [v.stationLon, v.stationLat];

                features.push({
                    type: "Feature", properties: { kind: "arm" },
                    geometry: { type: "LineString", coordinates: [[leader.lng, leader.lat], station] }
                });
                features.push({
                    type: "Feature", properties: { kind: "station", color: color },
                    geometry: { type: "Point", coordinates: station }
                });
                features.push({
                    type: "Feature", properties: { kind: "error", color: color },
                    geometry: { type: "LineString", coordinates: [[v.lng, v.lat], station] }
                });
            });
        }

        map.getSource("formation").setData({ type: "FeatureCollection", features: features });
    }

    window.clearSwarmTrails = function () {
        swarmTrails = {};
        refreshSwarmTrails();
    };

    // Swarm mode on: the map draws every vehicle. Off: it goes back to the single
    // active UAV, exactly as the app behaved before swarm support.
    /**
     * Draw a recorded flight path and frame it.
     * points: [{lat, lon, armed}] in time order.
     */
    window.showLogTrack = function (points) {
        if (!ready || !map.getSource("log-track")) return;
        if (!points || points.length < 2) { window.clearLogTrack(); return; }

        var features = [];
        var current = null;
        var currentArmed = null;

        // Break the path wherever the armed state flips, so each run is drawn by
        // the layer that matches it.
        for (var i = 0; i < points.length; i++) {
            var p = points[i];
            if (p.armed !== currentArmed) {
                if (current && current.length > 1) {
                    features.push({ type: "Feature",
                        properties: { kind: currentArmed ? "armed" : "ground" },
                        geometry: { type: "LineString", coordinates: current } });
                }
                // Start the new run at the previous point so the line has no gap.
                current = (i > 0) ? [[points[i - 1].lon, points[i - 1].lat]] : [];
                currentArmed = p.armed;
            }
            current.push([p.lon, p.lat]);
        }

        if (current && current.length > 1) {
            features.push({ type: "Feature",
                properties: { kind: currentArmed ? "armed" : "ground" },
                geometry: { type: "LineString", coordinates: current } });
        }

        var first = points[0], last = points[points.length - 1];
        features.push({ type: "Feature", properties: { kind: "end", color: "#3FB950" },
            geometry: { type: "Point", coordinates: [first.lon, first.lat] } });
        features.push({ type: "Feature", properties: { kind: "end", color: "#F85149" },
            geometry: { type: "Point", coordinates: [last.lon, last.lat] } });

        map.getSource("log-track").setData({ type: "FeatureCollection", features: features });

        // Frame the flight. Auto-follow would immediately pan back to the live
        // aircraft and undo the fit.
        followUAV = false;
        updateFollowBtn();

        var minLon = points[0].lon, maxLon = points[0].lon;
        var minLat = points[0].lat, maxLat = points[0].lat;
        for (var j = 1; j < points.length; j++) {
            if (points[j].lon < minLon) minLon = points[j].lon;
            if (points[j].lon > maxLon) maxLon = points[j].lon;
            if (points[j].lat < minLat) minLat = points[j].lat;
            if (points[j].lat > maxLat) maxLat = points[j].lat;
        }

        map.fitBounds([[minLon, minLat], [maxLon, maxLat]],
            { padding: 60, duration: 800, maxZoom: 18 });
    };

    window.clearLogTrack = function () {
        if (!ready || !map.getSource("log-track")) return;
        map.getSource("log-track").setData(empty());
    };

    /**
     * Log review mode: hide the mission so the recorded path is the only thing on
     * the map. Waypoints are DOM markers rather than layers, so both have to be
     * hidden — and the mission is only hidden, never cleared, so it comes back
     * untouched when review ends.
     */
    window.setLogReviewMode = function (on) {
        if (!ready) return;
        logReviewMode = !!on;

        // A fly-to menu left open from before would still act on the live aircraft.
        if (logReviewMode) hideFlyMenu();

        var vis = logReviewMode ? "none" : "visible";
        ["wp-circles-fill", "wp-circles-line", "wp-path"].forEach(function (id) {
            if (map.getLayer(id)) map.setLayoutProperty(id, "visibility", vis);
        });

        Object.keys(waypoints).forEach(function (k) {
            var el = waypoints[k] && waypoints[k].marker && waypoints[k].marker.getElement();
            if (el) el.style.display = logReviewMode ? "none" : "";
        });

        refreshUavArrowVisibility();
    };

    window.setSwarmMode = function (on) {
        swarmMode = !!on;
        if (!swarmMode) window.clearSwarm();
        refreshUavArrowVisibility();
        if (ready && is3D) map.triggerRepaint();
    };

    window.clearSwarm = function () {
        Object.keys(swarm).forEach(function (id) {
            var v = swarm[id];
            if (v.marker) v.marker.remove();
            delete swarm[id];
        });
        swarmTrails = {};
        refreshSwarmTrails();
        refreshFormationPreview();
        refreshUavArrowVisibility();
        if (ready && is3D) map.triggerRepaint();
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
        // Flat arrows in 2D, STL models in 3D — for the single UAV and the swarm.
        refreshUavArrowVisibility();
        Object.keys(swarm).forEach(function (id) {
            var v = swarm[id];
            if (v.el) v.el.style.display = is3D ? "none" : "";
        });
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
        // Both are mission/command interactions. While reviewing a recorded flight
        // there is nothing to command and no mission on screen to add to, so they
        // are inert rather than silently editing a hidden mission.
        map.on("click", function (e) {
            hideFlyMenu();
            if (logReviewMode) return;
            postMsg("click:" + e.lngLat.lat + "," + e.lngLat.lng);
        });
        map.on("contextmenu", function (e) {
            if (logReviewMode) return;
            showFlyMenu(e.point.x, e.point.y, e.lngLat.lat, e.lngLat.lng);
        });

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
