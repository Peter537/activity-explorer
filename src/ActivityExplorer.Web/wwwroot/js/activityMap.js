window.activityExplorerMap = (() => {
    const maps = new Map();
    const cameras = new Map();
    const lineLayers = [
        { name: "activities", option: "activityUrl", kind: "activity", label: "Activity", color: "#246b59", width: 2.5 },
        { name: "routes", option: "routeUrl", kind: "route", label: "Route", color: "#3366cc", width: 4 },
        { name: "segments", option: "segmentUrl", kind: "segment", label: "Segment", color: "#d06a35", width: 5 }
    ];
    const viewportPageSize = 20;
    const mapLibrePromise = import("/vendor/maplibre-gl.mjs");
    const blankStyle = () => ({
        version: 8,
        sources: {},
        layers: [{ id: "background", type: "background", paint: { "background-color": "#eef1ec" } }]
    });

    function wrapLongitude(longitude) {
        const wrapped = ((longitude + 180) % 360 + 360) % 360 - 180;
        return Object.is(wrapped, -0) ? 0 : wrapped;
    }

    function normalizedBounds(map) {
        const bounds = map.getBounds();
        const rawWest = bounds.getWest();
        const rawEast = bounds.getEast();
        const longitudeSpan = rawEast - rawWest;
        const fullWorld = Number.isFinite(longitudeSpan) && longitudeSpan >= 360;
        return {
            west: fullWorld ? -180 : wrapLongitude(rawWest),
            south: Math.max(-90, Math.min(90, bounds.getSouth())),
            east: fullWorld ? 180 : wrapLongitude(rawEast),
            north: Math.max(-90, Math.min(90, bounds.getNorth()))
        };
    }

    function queryUrl(baseUrl, map) {
        if (!baseUrl) return null;
        const bounds = normalizedBounds(map);
        const separator = baseUrl.includes("?") ? "&" : "?";
        return baseUrl + separator + new URLSearchParams({
            west: bounds.west.toString(), south: bounds.south.toString(),
            east: bounds.east.toString(), north: bounds.north.toString(),
            zoom: Math.floor(map.getZoom()).toString()
        });
    }

    async function readLineLayer(entry, layer, signal) {
        const url = entry.options[layer.option];
        if (!url) return null;
        try {
            const response = await fetch(queryUrl(url, entry.map), { headers: { "Accept": "application/json" }, signal });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const data = await response.json();
            if (!Array.isArray(data.features) || data.features.some(feature =>
                feature?.geometry?.type !== "LineString" || !Array.isArray(feature.geometry.coordinates)))
                throw new Error("Expected a collection of map lines.");
            const count = data.features.length;
            return { data, status: { layer: layer.name, loading: false, failed: false, returnedCount: count,
                matchingCount: data.coverage?.matchingCount ?? count, limit: data.coverage?.limit ?? count,
                truncated: data.coverage?.truncated === true } };
        } catch (error) {
            if (signal.aborted || entry.disposed) return null;
            console.warn(`Activity Explorer could not load map layer "${layer.name}".`, error);
            return { data: null, status: { layer: layer.name, loading: false, failed: true,
                returnedCount: 0, matchingCount: 0, limit: 0, truncated: false } };
        }
    }

    function clearLineLayer(entry, name) {
        entry.map.getSource(name)?.setData({ type: "FeatureCollection", features: [] });
        entry.map.getCanvas().dataset[`${name}Features`] = "0";
        entry.visibleLines.delete(name);
    }

    function drawLineLayer(entry, layer, data) {
        const { name, color, width } = layer;
        if (!data) {
            clearLineLayer(entry, name);
            return;
        }
        for (const feature of data.features) unwrapLine(feature.geometry.coordinates);
        const map = entry.map;
        if (map.getSource(name)) map.getSource(name).setData(data);
        else {
            map.addSource(name, { type: "geojson", data });
            map.addLayer({ id: name, type: "line", source: name,
                paint: { "line-color": color, "line-width": width, "line-opacity": name === "activities" ? 0.72 : 0.92 } });
        }
        map.getCanvas().dataset[`${name}Features`] = String(data.features.length);
        if (entry.viewport) entry.visibleLines.set(name, data.features.map(feature => feature.properties));
    }

    function lineDetail(properties) {
        const layer = lineLayers.find(x => x.kind === properties?.kind);
        if (!layer || !/^[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}$/i.test(properties.id)) return null;
        return { href: `/${layer.name}/${properties.id}`, title: properties.title || layer.label,
            context: [layer.label, properties.sport, properties.owner].filter(Boolean).join(" · ") };
    }

    function unwrapLine(coordinates) {
        let offset = 0;
        // Neighboring vertices should draw in the same world copy at the antimeridian.
        for (let i = 1; i < coordinates.length; i++) {
            const longitude = coordinates[i][0];
            const delta = longitude + offset - coordinates[i - 1][0];
            if (delta > 180) offset -= 360;
            if (delta < -180) offset += 360;
            if (offset) coordinates[i] = [longitude + offset, coordinates[i][1]];
        }
    }

    function createViewport(entry) {
        if (!entry.options.viewportListId) return null;
        const container = document.getElementById(entry.options.viewportListId);
        if (!container) return null;
        const heading = document.createElement("h2");
        heading.id = `${container.id}-heading`;
        heading.tabIndex = -1;
        heading.textContent = "Viewport results";
        const summary = document.createElement("p");
        const list = document.createElement("ul");
        // Explicit list semantics persist in Safari when list-style is none.
        list.setAttribute("role", "list");
        const navigation = document.createElement("div");
        navigation.className = "button-row";
        const previous = document.createElement("button");
        const next = document.createElement("button");
        const counter = document.createElement("span");
        const announcement = document.createElement("span");
        announcement.className = "visually-hidden";
        announcement.setAttribute("role", "status");
        for (const [button, label, step] of [[previous, "Previous results", -1], [next, "Next results", 1]]) {
            button.type = "button";
            button.className = "button";
            button.textContent = label;
            button.addEventListener("click", () => {
                if (button.getAttribute("aria-disabled") === "true") return;
                entry.viewport.page += step;
                renderViewport(entry, true);
            });
        }
        navigation.append(previous, counter, next);
        container.append(heading, summary, list, navigation, announcement);
        return { container, heading, summary, list, navigation, previous, next, counter, announcement, page: 0 };
    }

    function renderViewport(entry, announce = false) {
        const viewport = entry.viewport;
        if (!viewport) return;
        const { container, heading, summary, list, navigation, previous, next, counter, announcement } = viewport;
        container.hidden = entry.options.explorationView !== "lines";
        if (container.hidden) return;
        const rows = lineLayers.flatMap(layer => entry.visibleLines.get(layer.name) || []).map(lineDetail).filter(Boolean);
        const active = document.activeElement;
        const focusedHref = !announce && list.contains(active) ? active.getAttribute("href") : null;
        const focusIndex = focusedHref ? rows.findIndex(row => row.href === focusedHref) : -1;
        const pageCount = Math.max(1, Math.ceil(rows.length / viewportPageSize));
        viewport.page = focusIndex >= 0 ? Math.floor(focusIndex / viewportPageSize) : Math.min(viewport.page, pageCount - 1);
        const pageStart = viewport.page * viewportPageSize;
        const existing = new Map(Array.from(list.children, item => [item.firstChild.getAttribute("href"), item]));
        const visible = rows.slice(pageStart, pageStart + viewportPageSize);
        visible.forEach((row, index) => {
            let item = existing.get(row.href);
            if (!item) {
                item = document.createElement("li");
                const link = document.createElement("a");
                link.href = row.href;
                link.append(document.createElement("span"), document.createElement("small"));
                item.append(link);
            }
            existing.delete(row.href);
            const [title, context] = item.firstChild.children;
            if (title.textContent !== row.title) title.textContent = row.title;
            if (context.textContent !== row.context) context.textContent = row.context;
            const atIndex = list.children[index];
            if (atIndex !== item) list.insertBefore(item, atIndex || null);
        });
        for (const item of existing.values()) item.remove();

        const showPages = pageCount > 1;
        navigation.hidden = !showPages;
        summary.textContent = entry.linesLoading ? "Updating viewport results…" : rows.length
            ? `${rows.length.toLocaleString()} displayed map lines.` : "No displayed map lines to open.";
        container.setAttribute("aria-busy", String(!!entry.linesLoading));
        previous.setAttribute("aria-disabled", String(viewport.page === 0));
        next.setAttribute("aria-disabled", String(viewport.page === pageCount - 1));
        counter.textContent = `Results ${pageStart + 1}–${pageStart + visible.length} of ${rows.length}`;
        if (announce) announcement.textContent = counter.textContent;

        if (focusedHref && document.activeElement !== active) {
            const retained = focusIndex < 0 ? heading : list.children[focusIndex - pageStart].firstChild;
            retained.focus({ preventScroll: true });
        } else if (!showPages && navigation.contains(active)) {
            heading.focus({ preventScroll: true });
        }
    }

    function readCamera(key) {
        if (!key) return null;
        try {
            const camera = cameras.get(key) || JSON.parse(sessionStorage.getItem(`activity-explorer:camera:${key}`) || "null");
            return camera && Array.isArray(camera.center) && camera.center.length === 2 &&
                camera.center.every(Number.isFinite) && Math.abs(camera.center[1]) <= 90 &&
                Number.isFinite(camera.zoom) && camera.zoom >= 0 && camera.zoom <= 24 &&
                Number.isFinite(camera.bearing) && Number.isFinite(camera.pitch) && camera.pitch >= 0 && camera.pitch <= 85
                ? camera : null;
        } catch { return null; }
    }

    function rememberCamera(entry) {
        if (entry.disposed) return;
        const center = entry.map.getCenter();
        const camera = { center: [center.lng, center.lat], zoom: entry.map.getZoom(),
            bearing: entry.map.getBearing(), pitch: entry.map.getPitch() };
        const canvas = entry.map.getCanvas();
        canvas.dataset.cameraCenter = camera.center.join(",");
        canvas.dataset.cameraZoom = String(camera.zoom);
        if (!entry.options.cameraStorageKey) return;
        cameras.set(entry.options.cameraStorageKey, camera);
        try { sessionStorage.setItem(`activity-explorer:camera:${entry.options.cameraStorageKey}`, JSON.stringify(camera)); }
        catch { /* The in-memory camera still preserves navigation within this page session. */ }
    }

    function selectedCoordinates(entry) {
        const coordinates = entry.options.inlineCoordinates || [];
        const start = entry.options.selectionStartIndex;
        const end = entry.options.selectionEndIndex;
        if (Number.isInteger(start) && Number.isInteger(end) && start >= 0 && end >= start && end < coordinates.length)
            return coordinates.slice(start, end + 1);
        return entry.options.highlightCoordinates || [];
    }

    function projectionCoordinates(projection) {
        return (projection?.runs || []).flatMap(run => run.map(point => [point.longitude, point.latitude]));
    }

    function projectionLines(projection) {
        return {
            type: "FeatureCollection",
            features: (projection?.runs || []).filter(run => run.length > 1).map(run => ({
                type: "Feature",
                geometry: { type: "LineString", coordinates: run.map(point => [point.longitude, point.latitude]) },
                properties: { sourcePositions: run.map(point => point.sourcePosition) }
            }))
        };
    }

    function selectionEndpoints(entry) {
        if (entry.options.activityTrack) {
            const selection = entry.options.activitySelection;
            return [["start", selection?.start], ["end", selection?.end]]
                .filter(([, point]) => point)
                .map(([kind, point]) => ({
                    type: "Feature",
                    geometry: { type: "Point", coordinates: [point.longitude, point.latitude] },
                    properties: { kind, sourcePosition: point.sourcePosition }
                }));
        }
        if (!entry.options.showSelectionEndpoints) return [];
        const coordinates = entry.options.inlineCoordinates || [];
        const start = entry.options.selectionStartIndex;
        const end = entry.options.selectionEndIndex;
        if (!Number.isInteger(start) || !Number.isInteger(end) || start < 0 || end < start || end >= coordinates.length) return [];
        const reversed = !!entry.options.selectionReversed;
        return [
            { type: "Feature", geometry: { type: "Point", coordinates: coordinates[reversed ? end : start] }, properties: { kind: "start" } },
            { type: "Feature", geometry: { type: "Point", coordinates: coordinates[reversed ? start : end] }, properties: { kind: "end" } }
        ];
    }

    function inspectionFeature(entry) {
        const coordinates = entry.options.inlineCoordinates || [];
        const index = entry.options.inspectionIndex;
        const coordinate = entry.options.activityTrack
            ? entry.activityCoordinates?.get(index)
            : Number.isInteger(index) && index >= 0 && index < coordinates.length ? coordinates[index] : null;
        if (!coordinate)
            return { type: "FeatureCollection", features: [] };
        return {
            type: "FeatureCollection",
            features: [{
                type: "Feature",
                geometry: { type: "Point", coordinates: coordinate },
                properties: {}
            }]
        };
    }

    function refreshInspectionLayer(entry) {
        if (entry.disposed || !entry.options.inspectionGroupId) return;
        if (!entry.map.isStyleLoaded()) {
            if (!entry.inspectionPending) {
                entry.inspectionPending = true;
                entry.map.once("idle", () => {
                    entry.inspectionPending = false;
                    refreshInspectionLayer(entry);
                });
            }
            return;
        }
        const data = inspectionFeature(entry);
        if (entry.map.getSource("inspection-point")) entry.map.getSource("inspection-point").setData(data);
        else {
            entry.map.addSource("inspection-point", { type: "geojson", data });
            entry.map.addLayer({
                id: "inspection-point",
                type: "circle",
                source: "inspection-point",
                paint: {
                    "circle-radius": 7,
                    "circle-color": "#17211d",
                    "circle-stroke-width": 3,
                    "circle-stroke-color": "#ffffff"
                }
            });
        }
    }

    function setInspection(entry, sourceIndex) {
        const index = typeof sourceIndex === "number" ? sourceIndex : Number.NaN;
        const nextIndex = Number.isInteger(index) ? index : null;
        if (entry.options.inspectionIndex === nextIndex) return;
        entry.options.inspectionIndex = nextIndex;
        const container = entry.map.getContainer();
        entry.inspectionRevision = (entry.inspectionRevision || 0) + 1;
        container.dataset.inspectionRevision = String(entry.inspectionRevision);
        if (Number.isInteger(entry.options.inspectionIndex))
            container.dataset.inspectionIndex = String(entry.options.inspectionIndex);
        else
            delete container.dataset.inspectionIndex;
        refreshInspectionLayer(entry);
    }

    function refreshComparisonLayers(entry, markersOnly = false) {
        if (entry.disposed) return;
        if (!entry.map.isStyleLoaded()) {
            if (!entry.comparisonPending) {
                entry.comparisonPending = true;
                entry.map.once("idle", () => {
                    entry.comparisonPending = false;
                    refreshComparisonLayers(entry);
                });
            }
            return;
        }
        const sides = [
            ["comparison-baseline", entry.options.baselineComparisonTrack, "#246b59", false, entry.comparisonInspection?.baseline],
            ["comparison-effort", entry.options.comparisonTrack, "#3366cc", true, entry.comparisonInspection?.comparison]
        ];
        for (const [name, track, color, dashed, point] of sides) {
            if (!markersOnly || !entry.map.getSource(name)) {
                const data = projectionLines(track);
                if (entry.map.getSource(name)) entry.map.getSource(name).setData(data);
                else if (track) {
                    entry.map.addSource(name, { type: "geojson", data });
                    entry.map.addLayer({
                        id: name, type: "line", source: name,
                        paint: { "line-color": color, "line-width": 4, "line-opacity": 0.95,
                            ...(dashed ? { "line-dasharray": [2, 1.5] } : {}) }
                    });
                }
            }
            const marker = `${name}-inspection`;
            const markerData = {
                type: "FeatureCollection",
                features: track && point && Number.isFinite(point.latitude) && Number.isFinite(point.longitude)
                    ? [{ type: "Feature", geometry: { type: "Point", coordinates: [point.longitude, point.latitude] }, properties: { sourcePosition: point.sourcePosition } }]
                    : []
            };
            if (entry.map.getSource(marker)) entry.map.getSource(marker).setData(markerData);
            else if (track) {
                entry.map.addSource(marker, { type: "geojson", data: markerData });
                entry.map.addLayer({
                    id: marker, type: "circle", source: marker,
                    paint: { "circle-radius": dashed ? 5 : 9, "circle-color": dashed ? color : "#ffffff",
                        "circle-stroke-width": dashed ? 2 : 3, "circle-stroke-color": dashed ? "#ffffff" : color }
                });
            }
        }
    }

    function setComparisonInspection(entry, detail) {
        entry.comparisonInspection = detail;
        const container = entry.map.getContainer();
        container.dataset.comparisonRevision = String((entry.comparisonRevision || 0) + 1);
        entry.comparisonRevision = Number(container.dataset.comparisonRevision);
        for (const [name, value] of [["comparisonDistance", detail?.distanceMeters],
            ["comparisonBaselinePosition", detail?.baseline?.sourcePosition], ["comparisonPosition", detail?.comparison?.sourcePosition]]) {
            if (Number.isFinite(value)) container.dataset[name] = String(value);
            else delete container.dataset[name];
        }
        refreshComparisonLayers(entry, true);
    }

    function refreshSectionLayers(entry) {
        if (entry.disposed) return;
        if (!entry.map.isStyleLoaded()) {
            if (!entry.sectionsPending) {
                entry.sectionsPending = true;
                entry.map.once("idle", () => {
                    entry.sectionsPending = false;
                    refreshSectionLayers(entry);
                });
            }
            return;
        }
        const features = [];
        const coordinates = entry.options.inlineCoordinates || [];
        const markers = entry.sectionMarkers ||= new Map();
        const retained = new Set();
        for (const section of entry.options.sections || []) {
            const selected = section.id === entry.options.selectedSectionId;
            section.placements.forEach((placement, occurrence) => {
                const path = coordinates.slice(placement.startIndex, placement.endIndex + 1);
                if (path.length < 2) return;
                features.push({ type: "Feature", geometry: { type: "LineString", coordinates: path }, properties: { selected } });
                const key = `${section.id}:${occurrence}`;
                retained.add(key);
                let marker = markers.get(key);
                if (!marker) {
                    const button = document.createElement("button");
                    button.type = "button";
                    button.textContent = String(section.number);
                    button.setAttribute("aria-label", `Show ${section.name}, occurrence ${occurrence + 1}`);
                    button.title = `${section.number}. ${section.name}`;
                    button.addEventListener("click", () => entry.dotnet?.invokeMethodAsync("SelectSection", section.id));
                    marker = new entry.maplibre.Marker({ element: button }).setLngLat(path[0]).addTo(entry.map);
                    markers.set(key, marker);
                }
                const button = marker.getElement();
                button.textContent = String(section.number);
                button.title = `${section.number}. ${section.name}`;
                button.setAttribute("aria-label", `Show ${section.name}, occurrence ${occurrence + 1}`);
                button.classList.add("segment-section-marker");
                button.classList.toggle("selected", selected);
                button.setAttribute("aria-pressed", String(selected));
            });
        }
        for (const [key, marker] of markers) {
            if (!retained.has(key)) { marker.remove(); markers.delete(key); }
        }
        const data = { type: "FeatureCollection", features };
        if (entry.map.getSource("subsegments")) entry.map.getSource("subsegments").setData(data);
        else if (features.length) {
            entry.map.addSource("subsegments", { type: "geojson", data });
            entry.map.addLayer({ id: "subsegment-outline", type: "line", source: "subsegments", paint: { "line-color": "#ffffff", "line-width": 9 } });
            entry.map.addLayer({ id: "subsegment-paths", type: "line", source: "subsegments", paint: { "line-color": "#17211d", "line-width": 4, "line-dasharray": [2, 1] } });
            entry.map.addLayer({ id: "subsegment-active", type: "line", source: "subsegments", filter: ["==", ["get", "selected"], true], paint: { "line-color": "#246b59", "line-width": 6 } });
        }
        entry.map.getContainer().dataset.selectedSection = entry.options.selectedSectionId || "";
    }

    function refreshTrackLayers(entry, selectionOnly = false) {
        if (entry.disposed) return;
        if (!entry.map.isStyleLoaded()) {
            entry.trackPendingFull = entry.trackPendingFull || !selectionOnly;
            if (!entry.trackPending) {
                entry.trackPending = true;
                entry.map.once("idle", () => {
                    entry.trackPending = false;
                    const onlySelection = !entry.trackPendingFull;
                    entry.trackPendingFull = false;
                    refreshTrackLayers(entry, onlySelection);
                });
            }
            return;
        }
        const coordinates = entry.options.inlineCoordinates || [];
        const hasSelection = entry.options.activityTrack
            ? !!entry.options.activitySelection : Number.isInteger(entry.options.selectionStartIndex);
        if (!selectionOnly || !entry.map.getSource("inline")) {
            const lineData = entry.options.activityTrack ? projectionLines(entry.options.activityTrack) : coordinates.length > 1
                ? { type: "Feature", geometry: { type: "LineString", coordinates }, properties: {} }
                : { type: "FeatureCollection", features: [] };
            if (entry.map.getSource("inline")) entry.map.getSource("inline").setData(lineData);
            else {
                entry.map.addSource("inline", { type: "geojson", data: lineData });
                entry.map.addLayer({
                    id: "inline", type: "line", source: "inline",
                    paint: { "line-color": "#246b59", "line-width": 4, "line-opacity": hasSelection ? 0.38 : 0.95 }
                });
            }
        }
        entry.map.setPaintProperty("inline", "line-opacity", hasSelection ? 0.38 : 0.95);
        if (entry.options.editable && !selectionOnly) {
            const pointData = { type: "Feature", geometry: { type: "MultiPoint", coordinates }, properties: {} };
            if (entry.map.getSource("draw-points")) entry.map.getSource("draw-points").setData(pointData);
            else {
                entry.map.addSource("draw-points", { type: "geojson", data: pointData });
                entry.map.addLayer({ id: "draw-points", type: "circle", source: "draw-points", paint: { "circle-radius": 5, "circle-color": "#d06a35", "circle-stroke-width": 2, "circle-stroke-color": "#ffffff" } });
            }
        }
        const highlight = selectedCoordinates(entry);
        const highlightData = entry.options.activityTrack ? projectionLines(entry.options.activitySelection) : highlight.length > 1
            ? { type: "Feature", geometry: { type: "LineString", coordinates: highlight }, properties: {} }
            : { type: "FeatureCollection", features: [] };
        if (entry.map.getSource("selected-effort")) entry.map.getSource("selected-effort").setData(highlightData);
        else {
            entry.map.addSource("selected-effort", { type: "geojson", data: highlightData });
            entry.map.addLayer({ id: "selected-effort", type: "line", source: "selected-effort", paint: { "line-color": "#ed7d31", "line-width": 6, "line-opacity": 0.95 } });
        }

        const endpointData = { type: "FeatureCollection", features: selectionEndpoints(entry) };
        if (entry.map.getSource("selection-endpoints")) entry.map.getSource("selection-endpoints").setData(endpointData);
        else {
            entry.map.addSource("selection-endpoints", { type: "geojson", data: endpointData });
            entry.map.addLayer({
                id: "selection-start", type: "circle", source: "selection-endpoints", filter: ["==", ["get", "kind"], "start"],
                paint: { "circle-radius": 7, "circle-color": "#2f8f58", "circle-stroke-width": 3, "circle-stroke-color": "#ffffff" }
            });
            entry.map.addLayer({
                id: "selection-end", type: "circle", source: "selection-endpoints", filter: ["==", ["get", "kind"], "end"],
                paint: { "circle-radius": 7, "circle-color": "#c74632", "circle-stroke-width": 3, "circle-stroke-color": "#ffffff" }
            });
        }
        if (!selectionOnly) refreshSectionLayers(entry);
        refreshInspectionLayer(entry);
        refreshComparisonLayers(entry);
    }

    function paintExploration(entry) {
        const map = entry.map;
        if (!map.getLayer("exploration-fill")) return;
        map.setPaintProperty("exploration-fill", "fill-color", entry.options.explorationView === "frequency"
            ? ["interpolate", ["linear"], ["get", "weight"], 0, "#dcece5", 0.5, "#68a28d", 1, "#174c3f"]
            : ["case", [">", ["get", "newCells"], 0], "#d06a35", "#246b59"]);
        map.setPaintProperty("exploration-outline", "line-color", ["case",
            ["==", ["get", "cellId"], entry.options.selectedCellId ?? -2], "#17211d", "#45695b"]);
        map.setPaintProperty("exploration-outline", "line-width", ["case",
            ["==", ["get", "cellId"], entry.options.selectedCellId ?? -2], 3, 0.7]);
    }

    async function setExploration(entry, signal) {
        const map = entry.map;
        if (!entry.options.explorationUrl) {
            map.getSource("exploration")?.setData({ type: "FeatureCollection", features: [] });
            return 0;
        }
        try {
            const response = await fetch(queryUrl(entry.options.explorationUrl, map), { headers: { "Accept": "application/json" }, signal });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const result = await response.json();
            if (signal.aborted || entry.disposed) return 0;
            if (!result.index.isComplete || !result.summary) {
                map.getSource("exploration")?.setData({ type: "FeatureCollection", features: [] });
                await entry.dotnet?.invokeMethodAsync("InvalidateExploration");
                return 0;
            }
            const maximum = Math.max(1, result.summary.maximumFrequency);
            const data = { type: "FeatureCollection", features: result.cells.map(cell => ({
                type: "Feature",
                geometry: { type: "Polygon", coordinates: [[[cell.west, cell.south], [cell.east, cell.south],
                    [cell.east, cell.north], [cell.west, cell.north], [cell.west, cell.south]]] },
                properties: { ...cell, cellId: cell.zoom === 14 ? cell.y * 16384 + cell.x : -1,
                    weight: Math.log1p(cell.activityCount) / Math.log1p(maximum) }
            })) };
            if (map.getSource("exploration")) map.getSource("exploration").setData(data);
            else {
                map.addSource("exploration", { type: "geojson", data });
                map.addLayer({ id: "exploration-fill", type: "fill", source: "exploration", paint: { "fill-color": "#246b59", "fill-opacity": 0.68 } });
                map.addLayer({ id: "exploration-outline", type: "line", source: "exploration", paint: { "line-color": "#45695b", "line-width": 0.7 } });
            }
            paintExploration(entry);
            map.getCanvas().dataset.explorationRenderZoom = result.renderZoom;
            map.getCanvas().dataset.explorationFeatures = result.cells.length;
            await entry.dotnet?.invokeMethodAsync("UpdateExplorationSummary", result.summary);
            return result.cells.length;
        } catch (error) {
            if (signal.aborted || entry.disposed) return 0;
            map.getSource("exploration")?.setData({ type: "FeatureCollection", features: [] });
            console.warn("Activity Explorer could not load exploration cells.", error);
            return null;
        }
    }

    function explorationInteraction(entry) {
        const map = entry.map;
        map.on("click", "exploration-fill", event => {
            const cell = event.features?.[0]?.properties;
            if (!cell) return;
            entry.cellPopup?.remove();
            const description = document.createElement("div");
            if (cell.zoom < 14) {
                description.textContent = `${cell.visitedCells} visited / ${cell.newCells} new cells. Highest frequency: ${cell.activityCount} activities.`;
                const shift = Math.round((map.getCenter().lng - (cell.west + cell.east) / 2) / 360) * 360;
                map.fitBounds([[cell.west + shift, cell.south], [cell.east + shift, cell.north]], { padding: 40, maxZoom: 16, duration: 0 });
            } else {
                description.textContent = `Cell ${cell.x} / ${cell.y} · ${cell.activityCount} activities. Activity details are below the map.`;
                entry.dotnet?.invokeMethodAsync("SelectExplorationCell", cell.cellId).catch(() => {});
            }
            entry.cellPopup = new entry.maplibre.Popup().setLngLat(event.lngLat).setDOMContent(description).addTo(map);
        });
        map.on("mouseenter", "exploration-fill", () => map.getCanvas().style.cursor = "pointer");
        map.on("mouseleave", "exploration-fill", () => map.getCanvas().style.cursor = "");
    }

    function updateLayers(id, options) {
        const entry = maps.get(id);
        if (!entry || entry.disposed) return;
        const changedLines = lineLayers.filter(layer => entry.options[layer.option] !== options[layer.option]);
        const changedView = entry.options.explorationView !== options.explorationView;
        const changed = changedLines.length || changedView || entry.options.explorationUrl !== options.explorationUrl;
        if (changed) entry.cellPopup?.remove();
        Object.assign(entry.options, options);
        if (changed) {
            entry.request?.abort();
            entry.linePopup?.remove();
            for (const layer of changedLines) clearLineLayer(entry, layer.name);
            entry.linesLoading = lineLayers.some(layer => !!entry.options[layer.option]);
            renderViewport(entry);
        }
        entry.map.getCanvas().setAttribute("aria-label", options.label || "Activity map");
        paintExploration(entry);
        if (changed) return refresh(entry);
    }

    async function refresh(entry) {
        if (entry.disposed) return;
        entry.request?.abort();
        // Source updates temporarily make isStyleLoaded false. Only a style replacement
        // should defer a request; style.load will use the latest layer options.
        if (!entry.styleReady) return;
        const request = new AbortController();
        entry.request = request;
        const revision = entry.layerRevision = (entry.layerRevision || 0) + 1;
        const report = (loading, failed, count) => entry.options.reportLoadStatus
            ? entry.dotnet?.invokeMethodAsync("UpdateLayerStatus", loading, failed, count, revision).catch(() => {})
            : Promise.resolve();
        const reportLines = layers => entry.options.reportLoadStatus
            ? entry.dotnet?.invokeMethodAsync("UpdateLineCoverage", layers, revision).catch(() => {})
            : Promise.resolve();
        const enabled = lineLayers.filter(layer => !!entry.options[layer.option]).map(({ name }) => ({ layer: name, loading: true, failed: false,
                returnedCount: 0, matchingCount: 0, limit: 0, truncated: false }));
        entry.linesLoading = enabled.length > 0;
        renderViewport(entry);
        await report(true, false, 0);
        await reportLines(enabled);
        if (request.signal.aborted || entry.disposed) return;
        const [lines, cellCount] = await Promise.all([
            Promise.all(lineLayers.map(layer => readLineLayer(entry, layer, request.signal))),
            setExploration(entry, request.signal)
        ]);
        if (request.signal.aborted || entry.disposed) return;
        for (const [index, layer] of lineLayers.entries()) drawLineLayer(entry, layer, lines[index]?.data);
        entry.linesLoading = false;
        renderViewport(entry);
        refreshTrackLayers(entry);
        await reportLines(lines.filter(Boolean).map(line => line.status));
        await report(false, cellCount === null, cellCount || 0);
    }

    function popup(entry, layer) {
        entry.map.on("click", layer, event => {
            const properties = event.features?.[0]?.properties;
            const row = lineDetail(properties);
            if (!row || entry.viewport && !entry.visibleLines.get(layer)?.some(line => line.id === properties.id)) return;
            const content = document.createElement("div");
            const title = document.createElement("strong");
            title.textContent = row.title;
            const link = document.createElement("a");
            link.href = row.href;
            link.textContent = "Open details →";
            content.append(title, document.createElement("br"), link);
            entry.linePopup?.remove();
            entry.linePopup = new entry.maplibre.Popup().setLngLat(event.lngLat).setDOMContent(content).addTo(entry.map);
        });
        entry.map.on("mouseenter", layer, () => entry.map.getCanvas().style.cursor = "pointer");
        entry.map.on("mouseleave", layer, () => entry.map.getCanvas().style.cursor = "");
    }

    async function create(id, options, dotnet) {
        const maplibregl = await mapLibrePromise;
        if (!document.getElementById(id)) return;
        const camera = readCamera(options.cameraStorageKey);
        const map = new maplibregl.Map({
            container: id,
            style: options.blankBaseMap ? blankStyle() : options.styleUrl,
            center: camera?.center || [10, 50], zoom: camera?.zoom ?? 3,
            bearing: camera?.bearing ?? 0, pitch: camera?.pitch ?? 0,
            attributionControl: false
        });
        options.inlineCoordinates = options.inlineCoordinates || [];
        if (!options.editable) unwrapLine(options.inlineCoordinates);
        const canvas = map.getCanvas();
        canvas.setAttribute("aria-label", options.label || "Activity map");
        if (options.describedBy) canvas.setAttribute("aria-describedby", options.describedBy);

        const entry = { map, maplibre: maplibregl, options, dotnet, fallback: !!options.blankBaseMap, styleReady: false,
            visibleLines: new Map(), linesLoading: lineLayers.some(layer => !!options[layer.option]) };
        entry.viewport = createViewport(entry);
        renderViewport(entry);
        canvas.dataset.cameraRestored = String(!!camera);
        rememberCamera(entry);
        lineLayers.forEach(layer => popup(entry, layer.name));
        explorationInteraction(entry);
        indexActivityCoordinates(entry);
        maps.set(id, entry);
        if (options.inspectionGroupId) {
            const group = document.getElementById(options.inspectionGroupId);
            if (group) {
                entry.inspectionGroup = group;
                entry.inspectionHandler = event => setInspection(entry, event.detail?.sourceIndex);
                group.addEventListener("activity-explorer:segment-inspection", entry.inspectionHandler);
                entry.comparisonInspectionHandler = event => setComparisonInspection(entry, event.detail);
                group.addEventListener("activity-explorer:comparison-inspection", entry.comparisonInspectionHandler);
                setComparisonInspection(entry, group.comparisonInspection);
            }
        }
        map.addControl(new maplibregl.NavigationControl({ showCompass: true }), "top-right");
        map.addControl(new maplibregl.AttributionControl({
            compact: true,
            customAttribution: options.blankBaseMap ? "Local tracks - Activity Explorer" : '<a href="https://www.openstreetmap.org/copyright" target="_blank">&copy; OpenStreetMap contributors</a>'
        }));
        map.on("style.load", () => {
            entry.styleReady = true;
            refresh(entry);
        });
        map.on("load", () => {
            if (entry.disposed) return;
            const fitCoordinates = options.activityTrack ? projectionCoordinates(options.activityTrack) : options.inlineCoordinates;
            if (!camera && fitCoordinates?.length) {
                const bounds = fitCoordinates.reduce((value, coordinate) => value.extend(coordinate), new maplibregl.LngLatBounds(fitCoordinates[0], fitCoordinates[0]));
                map.fitBounds(bounds, { padding: 48, maxZoom: 15, duration: 0 });
            }
            if (options.editable) {
                map.getCanvas().style.cursor = "crosshair";
                map.on("click", event => {
                    options.inlineCoordinates.push([event.lngLat.lng, event.lngLat.lat]);
                    refresh(entry);
                    dotnet?.invokeMethodAsync("UpdateDrawing", options.inlineCoordinates);
                });
            }
        });
        map.on("movestart", () => { entry.request?.abort(); entry.linePopup?.remove(); });
        map.on("moveend", () => { rememberCamera(entry); refresh(entry); });
        map.on("error", event => {
            if (!entry.fallback && event?.error) {
                entry.fallback = true;
                entry.styleReady = false;
                entry.request?.abort();
                map.setStyle(blankStyle());
            }
        });
    }

    function addCenterPoint(id) {
        const entry = maps.get(id);
        if (!entry?.options.editable) return;
        const center = entry.map.getCenter();
        entry.options.inlineCoordinates.push([center.lng, center.lat]);
        refresh(entry);
        entry.dotnet?.invokeMethodAsync("UpdateDrawing", entry.options.inlineCoordinates);
    }

    function undo(id) {
        const entry = maps.get(id);
        if (!entry?.options.editable || !entry.options.inlineCoordinates.length) return;
        entry.options.inlineCoordinates.pop();
        refresh(entry);
        entry.dotnet?.invokeMethodAsync("UpdateDrawing", entry.options.inlineCoordinates);
    }

    function clear(id) {
        const entry = maps.get(id);
        if (!entry?.options.editable) return;
        entry.options.inlineCoordinates.length = 0;
        refresh(entry);
        entry.dotnet?.invokeMethodAsync("UpdateDrawing", entry.options.inlineCoordinates);
    }

    function updateSelection(id, startIndex, endIndex, reversed) {
        const entry = maps.get(id);
        if (!entry) return;
        entry.options.selectionStartIndex = startIndex;
        entry.options.selectionEndIndex = endIndex;
        entry.options.selectionReversed = !!reversed;
        refreshTrackLayers(entry);
    }

    function updateSections(id, sections, selectedSectionId) {
        const entry = maps.get(id);
        if (!entry) return;
        entry.options.sections = sections;
        entry.options.selectedSectionId = selectedSectionId;
        refreshSectionLayers(entry);
    }

    function indexActivityCoordinates(entry) {
        entry.activityCoordinates = new Map();
        for (const run of entry.options.activityTrack?.runs || [])
            for (const point of run)
                if (Number.isInteger(point.sourcePosition))
                    entry.activityCoordinates.set(point.sourcePosition, [point.longitude, point.latitude]);
    }

    function updateActivity(id, track, selection) {
        const entry = maps.get(id);
        if (!entry) return;
        entry.options.activityTrack = track;
        entry.options.activitySelection = selection;
        indexActivityCoordinates(entry);
        refreshTrackLayers(entry);
    }

    function updateActivitySelection(id, selection) {
        const entry = maps.get(id);
        if (!entry) return;
        entry.options.activitySelection = selection;
        refreshTrackLayers(entry, true);
    }

    function updateComparison(id, baseline, comparison) {
        const entry = maps.get(id);
        if (!entry) return;
        entry.options.baselineComparisonTrack = baseline;
        entry.options.comparisonTrack = comparison;
        entry.comparisonInspection = entry.inspectionGroup?.comparisonInspection;
        refreshComparisonLayers(entry);
    }

    function updateHighlight(id, coordinates) {
        const entry = maps.get(id);
        if (!entry) return;
        entry.options.highlightCoordinates = coordinates || [];
        refreshTrackLayers(entry, true);
    }

    function destroy(id) {
        const entry = maps.get(id);
        if (entry) {
            rememberCamera(entry);
            entry.disposed = true;
            entry.request?.abort();
            if (entry.inspectionGroup && entry.inspectionHandler)
                entry.inspectionGroup.removeEventListener("activity-explorer:segment-inspection", entry.inspectionHandler);
            if (entry.inspectionGroup && entry.comparisonInspectionHandler)
                entry.inspectionGroup.removeEventListener("activity-explorer:comparison-inspection", entry.comparisonInspectionHandler);
            for (const marker of entry.sectionMarkers?.values() || []) marker.remove();
            entry.map.remove();
            maps.delete(id);
        }
    }

    function reload(id) { const entry = maps.get(id); if (entry) return refresh(entry); }

    function focusCell(id, cellId) {
        const entry = maps.get(id);
        if (!entry || entry.disposed || !Number.isInteger(cellId) || cellId < 0 || cellId >= 268435456) return;
        const size = 16384;
        const x = cellId % size;
        const y = Math.floor(cellId / size);
        const longitude = column => column / size * 360 - 180;
        const latitude = row => Math.atan(Math.sinh(Math.PI * (1 - 2 * row / size))) * 180 / Math.PI;
        const west = longitude(x);
        const east = longitude(x + 1);
        const shift = Math.round((entry.map.getCenter().lng - (west + east) / 2) / 360) * 360;
        entry.map.fitBounds([[west + shift, latitude(y + 1)], [east + shift, latitude(y)]], { padding: 32, maxZoom: 16, duration: 0 });
        const canvas = entry.map.getCanvas();
        canvas.dataset.focusedCell = String(cellId);
        entry.map.getContainer().scrollIntoView({ block: "center", behavior: "instant" });
        canvas.focus({ preventScroll: true });
        rememberCamera(entry);
    }

    function zoomIn(id) {
        const entry = maps.get(id);
        if (!entry || entry.disposed) return;
        entry.map.zoomIn({ duration: 0 });
        entry.map.getCanvas().focus({ preventScroll: true });
    }

    return { create, addCenterPoint, undo, clear, updateLayers, updateSelection, updateSections, updateActivity, updateActivitySelection, updateComparison, updateHighlight, destroy, reload, focusCell, zoomIn };
})();
