window.activityExplorerMap = (() => {
    const maps = new Map();
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
            zoom: Math.round(map.getZoom()).toString()
        });
    }

    async function setLayer(entry, name, url, color, width, signal) {
        const map = entry.map;
        if (!url || !map.isStyleLoaded()) return 0;
        try {
            const response = await fetch(queryUrl(url, map), { headers: { "Accept": "application/json" }, signal });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const data = await response.json();
            if (signal.aborted) return 0;
            if (map.getSource(name)) map.getSource(name).setData(data);
            else {
                map.addSource(name, { type: "geojson", data });
                map.addLayer({
                    id: name, type: "line", source: name,
                    paint: { "line-color": color, "line-width": width, "line-opacity": name === "activities" ? 0.72 : 0.92 }
                });
            }
            return data.features.length;
        } catch (error) {
            if (signal.aborted) return 0;
            map.getSource(name)?.setData({ type: "FeatureCollection", features: [] });
            console.warn(`Activity Explorer could not load map layer "${name}".`, error);
            return null;
        }
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

    async function refresh(entry) {
        if (entry.disposed) return;
        entry.request?.abort();
        const request = new AbortController();
        entry.request = request;
        const report = (loading, failed, count) => entry.options.reportLoadStatus
            ? entry.dotnet?.invokeMethodAsync("UpdateLayerStatus", loading, failed, count).catch(() => {})
            : Promise.resolve();
        await report(true, false, 0);
        const counts = await Promise.all([
            setLayer(entry, "activities", entry.options.activityUrl, "#246b59", 2.5, request.signal),
            setLayer(entry, "routes", entry.options.routeUrl, "#3366cc", 4, request.signal),
            setLayer(entry, "segments", entry.options.segmentUrl, "#d06a35", 5, request.signal)
        ]);
        if (request.signal.aborted) return;
        refreshTrackLayers(entry);
        await report(false, counts.includes(null), counts.reduce((sum, count) => sum + (count || 0), 0));
    }

    function popup(entry, layer) {
        entry.map.on("click", layer, event => {
            const feature = event.features?.[0];
            if (!feature) return;
            const props = feature.properties || {};
            const id = props.id;
            const href = props.kind && id ? "/" + (props.kind === "activity" ? "activities" : props.kind + "s") + "/" + id : "#";
            const title = String(props.title || "Map feature").replace(/[<>&"']/g, value => ({ "<": "&lt;", ">": "&gt;", "&": "&amp;", '"': "&quot;", "'": "&#39;" }[value]));
            new entry.maplibre.Popup().setLngLat(event.lngLat).setHTML("<strong>" + title + "</strong><br><a href=\"" + href + "\">Open details &rarr;</a>").addTo(entry.map);
        });
        entry.map.on("mouseenter", layer, () => entry.map.getCanvas().style.cursor = "pointer");
        entry.map.on("mouseleave", layer, () => entry.map.getCanvas().style.cursor = "");
    }

    async function create(id, options, dotnet) {
        const maplibregl = await mapLibrePromise;
        if (!document.getElementById(id)) return;
        const map = new maplibregl.Map({
            container: id,
            style: options.blankBaseMap ? blankStyle() : options.styleUrl,
            center: [10, 50], zoom: 3,
            attributionControl: false
        });
        options.inlineCoordinates = options.inlineCoordinates || [];
        const canvas = map.getCanvas();
        canvas.setAttribute("aria-label", options.label || "Activity map");
        if (options.describedBy) canvas.setAttribute("aria-describedby", options.describedBy);

        const entry = { map, maplibre: maplibregl, options, dotnet, fallback: !!options.blankBaseMap };
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
        map.on("load", async () => {
            await refresh(entry);
            if (entry.disposed) return;
            ["activities", "routes", "segments"].forEach(layer => { if (map.getLayer(layer)) popup(entry, layer); });
            const fitCoordinates = options.activityTrack ? projectionCoordinates(options.activityTrack) : options.inlineCoordinates;
            if (fitCoordinates?.length) {
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
        map.on("moveend", () => refresh(entry));
        map.on("error", event => {
            if (!entry.fallback && event?.error) {
                entry.fallback = true;
                map.setStyle(blankStyle());
                map.once("styledata", () => refresh(entry));
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

    return { create, addCenterPoint, undo, clear, updateSelection, updateSections, updateActivity, updateActivitySelection, updateComparison, updateHighlight, destroy, reload };
})();
