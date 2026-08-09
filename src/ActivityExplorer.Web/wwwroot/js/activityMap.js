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

    async function setLayer(map, name, url, color, width) {
        if (!url || !map.isStyleLoaded()) return;
        try {
            const response = await fetch(queryUrl(url, map), { headers: { "Accept": "application/json" } });
            if (!response.ok) {
                console.warn(`Activity Explorer map layer "${name}" returned HTTP ${response.status}.`);
                return;
            }
            const data = await response.json();
            if (map.getSource(name)) map.getSource(name).setData(data);
            else {
                map.addSource(name, { type: "geojson", data });
                map.addLayer({
                    id: name, type: "line", source: name,
                    paint: { "line-color": color, "line-width": width, "line-opacity": name === "activities" ? 0.72 : 0.92 }
                });
            }
        } catch (error) {
            console.warn(`Activity Explorer could not load map layer "${name}".`, error);
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

    function selectionEndpoints(entry) {
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
        if (!Number.isInteger(index) || index < 0 || index >= coordinates.length)
            return { type: "FeatureCollection", features: [] };
        return {
            type: "FeatureCollection",
            features: [{
                type: "Feature",
                geometry: { type: "Point", coordinates: coordinates[index] },
                properties: {}
            }]
        };
    }

    function refreshInspectionLayer(entry) {
        if (!entry.map.isStyleLoaded() || !entry.options.inspectionGroupId) return;
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

    function refreshTrackLayers(entry) {
        if (!entry.map.isStyleLoaded()) return;
        const coordinates = entry.options.inlineCoordinates || [];
        const lineData = coordinates.length > 1
            ? { type: "Feature", geometry: { type: "LineString", coordinates }, properties: {} }
            : { type: "FeatureCollection", features: [] };
        if (entry.map.getSource("inline")) entry.map.getSource("inline").setData(lineData);
        else {
            entry.map.addSource("inline", { type: "geojson", data: lineData });
            entry.map.addLayer({
                id: "inline", type: "line", source: "inline",
                paint: { "line-color": "#246b59", "line-width": 4, "line-opacity": Number.isInteger(entry.options.selectionStartIndex) ? 0.38 : 0.95 }
            });
        }
        if (entry.options.editable) {
            const pointData = { type: "Feature", geometry: { type: "MultiPoint", coordinates }, properties: {} };
            if (entry.map.getSource("draw-points")) entry.map.getSource("draw-points").setData(pointData);
            else {
                entry.map.addSource("draw-points", { type: "geojson", data: pointData });
                entry.map.addLayer({ id: "draw-points", type: "circle", source: "draw-points", paint: { "circle-radius": 5, "circle-color": "#d06a35", "circle-stroke-width": 2, "circle-stroke-color": "#ffffff" } });
            }
        }
        const highlight = selectedCoordinates(entry);
        const highlightData = highlight.length > 1
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
        refreshInspectionLayer(entry);
    }

    async function refresh(entry) {
        await Promise.all([
            setLayer(entry.map, "activities", entry.options.activityUrl, "#246b59", 2.5),
            setLayer(entry.map, "routes", entry.options.routeUrl, "#3366cc", 4),
            setLayer(entry.map, "segments", entry.options.segmentUrl, "#d06a35", 5)
        ]);
        refreshTrackLayers(entry);
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
        maps.set(id, entry);
        if (options.inspectionGroupId) {
            const group = document.getElementById(options.inspectionGroupId);
            if (group) {
                entry.inspectionGroup = group;
                entry.inspectionHandler = event => setInspection(entry, event.detail?.sourceIndex);
                group.addEventListener("activity-explorer:segment-inspection", entry.inspectionHandler);
            }
        }
        map.addControl(new maplibregl.NavigationControl({ showCompass: true }), "top-right");
        map.addControl(new maplibregl.AttributionControl({
            compact: true,
            customAttribution: options.blankBaseMap ? "Local tracks - Activity Explorer" : '<a href="https://www.openstreetmap.org/copyright" target="_blank">&copy; OpenStreetMap contributors</a>'
        }));
        map.on("load", async () => {
            await refresh(entry);
            ["activities", "routes", "segments"].forEach(layer => { if (map.getLayer(layer)) popup(entry, layer); });
            if (options.inlineCoordinates?.length > 1) {
                const bounds = options.inlineCoordinates.reduce((value, coordinate) => value.extend(coordinate), new maplibregl.LngLatBounds(options.inlineCoordinates[0], options.inlineCoordinates[0]));
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

    function destroy(id) {
        const entry = maps.get(id);
        if (entry) {
            if (entry.inspectionGroup && entry.inspectionHandler)
                entry.inspectionGroup.removeEventListener("activity-explorer:segment-inspection", entry.inspectionHandler);
            entry.map.remove();
            maps.delete(id);
        }
    }

    return { create, addCenterPoint, undo, clear, updateSelection, destroy };
})();
