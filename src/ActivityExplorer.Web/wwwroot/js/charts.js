window.activityCharts = (() => {
    const bindings = new Map();
    const rangeBindings = new Map();
    const rangeStates = new WeakMap();

    function number(value) {
        if (value === null || value === undefined || value === "") return null;
        const parsed = Number(value);
        return Number.isFinite(parsed) ? parsed : null;
    }

    function chartRoots(root) {
        return root.classList.contains("spark-chart")
            ? [root]
            : Array.from(root.querySelectorAll(".time-series-chart"));
    }

    function samples(chart) {
        return Array.from(chart.querySelectorAll(".chart-sample")).filter(sample =>
            number(sample.getAttribute("cx")) !== null &&
            number(sample.getAttribute("cy")) !== null &&
            number(sample.dataset.axis) !== null);
    }

    function nearestByX(available, targetX) {
        if (!available.length) return null;
        return available.reduce((best, candidate) => {
            const bestDistance = Math.abs((number(best.getAttribute("cx")) ?? 0) - targetX);
            const candidateDistance = Math.abs((number(candidate.getAttribute("cx")) ?? 0) - targetX);
            return candidateDistance < bestDistance ? candidate : best;
        });
    }

    function nearestByAxis(available, targetAxis) {
        if (!available.length) return null;
        return available.reduce((best, candidate) => {
            const bestDistance = Math.abs((number(best.dataset.axis) ?? 0) - targetAxis);
            const candidateDistance = Math.abs((number(candidate.dataset.axis) ?? 0) - targetAxis);
            return candidateDistance < bestDistance ? candidate : best;
        });
    }

    function pointerCoordinate(svg, event) {
        const rect = svg.getBoundingClientRect();
        const viewBox = svg.viewBox?.baseVal;
        const fraction = Math.max(0, Math.min(1, (event.clientX - rect.left) / Math.max(rect.width, 1)));
        return (viewBox?.x ?? 0) + fraction * (viewBox?.width || 800);
    }

    function show(chart, cursorX, sample, showTooltip) {
        if (!sample) return;
        const cursor = chart.querySelector(".chart-cursor");
        const marker = chart.querySelector(".chart-marker");
        const tooltip = chart.querySelector(".chart-tooltip");
        const sampleX = number(sample.getAttribute("cx"));
        const sampleY = number(sample.getAttribute("cy"));
        if (!cursor || !marker || !tooltip || sampleX === null || sampleY === null) return;

        cursor.setAttribute("x1", String(cursorX));
        cursor.setAttribute("x2", String(cursorX));
        cursor.classList.add("visible");
        marker.setAttribute("cx", String(sampleX));
        marker.setAttribute("cy", String(sampleY));
        marker.classList.add("visible");

        if (!showTooltip) {
            tooltip.classList.remove("visible");
            return;
        }

        const axisLabel = sample.dataset.axisLabel || "";
        const valueLabel = sample.dataset.valueLabel || "";
        tooltip.textContent = axisLabel && valueLabel
            ? axisLabel + " | " + valueLabel
            : axisLabel || valueLabel;
        tooltip.classList.add("visible");
    }

    function clearChart(chart) {
        chart.querySelector(".chart-cursor")?.classList.remove("visible");
        chart.querySelector(".chart-marker")?.classList.remove("visible");
        chart.querySelector(".chart-tooltip")?.classList.remove("visible");
    }

    function clear(root) {
        chartRoots(root).forEach(clearChart);
    }

    function showInspector(root, inspector) {
        const chart = inspector.closest(".time-series-chart, .spark-chart");
        if (!chart || !root.contains(chart)) return;
        const available = samples(chart);
        const index = Math.max(0, Math.min(available.length - 1, Number(inspector.value) || 0));
        const sample = available[index];
        const x = sample ? number(sample.getAttribute("cx")) : null;
        clear(root);
        if (sample && x !== null) show(chart, x, sample, false);
    }

    function restoreFocusedInspector(root) {
        const active = document.activeElement;
        if (active?.matches?.(".chart-inspector") && root.contains(active)) {
            showInspector(root, active);
            return true;
        }
        return false;
    }

    function bind(root) {
        if (bindings.has(root)) return;
        const plots = Array.from(root.querySelectorAll(".chart-plot"));
        if (!plots.some(plot => plot.closest(".time-series-chart, .spark-chart")?.querySelector(".chart-sample"))) return;

        const synchronized = root.classList.contains("synchronized-charts");
        const onPointerMove = event => {
            if (event.target.closest?.(".range-selecting")) return;
            const svg = event.target.closest?.(".chart-plot");
            if (!svg || !root.contains(svg)) return;
            const sourceChart = svg.closest(".time-series-chart, .spark-chart");
            if (!sourceChart) return;
            const sourceSample = nearestByX(samples(sourceChart), pointerCoordinate(svg, event));
            if (!sourceSample) return;
            const sourceAxis = number(sourceSample.dataset.axis);
            const cursorX = number(sourceSample.getAttribute("cx"));
            if (sourceAxis === null || cursorX === null) return;

            clear(root);
            if (!synchronized) {
                show(sourceChart, cursorX, sourceSample, true);
                return;
            }

            chartRoots(root).forEach(chart => {
                const sample = nearestByAxis(samples(chart), sourceAxis);
                if (sample) show(chart, cursorX, sample, true);
            });
        };

        const onPointerOut = event => {
            // Touch emits out/leave when the finger lifts; keep a benchmark tap readable.
            if (event.pointerType === "touch" && root.classList.contains("benchmark-progress-chart")) return;
            const svg = event.target.closest?.(".chart-plot");
            if (!svg || !root.contains(svg)) return;
            const nextPlot = event.relatedTarget?.closest?.(".chart-plot");
            if (nextPlot && root.contains(nextPlot)) return;
            clear(root);
            restoreFocusedInspector(root);
        };

        const onPointerLeave = event => {
            if (event.pointerType === "touch" && root.classList.contains("benchmark-progress-chart")) return;
            clear(root);
            restoreFocusedInspector(root);
        };

        const onInspector = event => {
            if (event.target.matches?.(".chart-inspector")) showInspector(root, event.target);
        };

        const onFocusOut = event => {
            if (!event.target.matches?.(".chart-inspector")) return;
            clear(root);
        };

        const onKeyDown = event => {
            if (event.key === "Escape" && event.target.matches?.(".chart-inspector")) event.target.blur();
        };

        root.addEventListener("pointermove", onPointerMove);
        root.addEventListener("pointerdown", onPointerMove);
        root.addEventListener("pointerout", onPointerOut);
        root.addEventListener("pointerleave", onPointerLeave);
        root.addEventListener("input", onInspector);
        root.addEventListener("focusin", onInspector);
        root.addEventListener("focusout", onFocusOut);
        root.addEventListener("keydown", onKeyDown);
        root.dataset.chartsBound = "true";
        bindings.set(root, {
            onPointerMove, onPointerOut, onPointerLeave, onInspector, onFocusOut, onKeyDown
        });
    }

    function unbind(root) {
        const binding = bindings.get(root);
        if (!binding) return;
        root.removeEventListener("pointermove", binding.onPointerMove);
        root.removeEventListener("pointerdown", binding.onPointerMove);
        root.removeEventListener("pointerout", binding.onPointerOut);
        root.removeEventListener("pointerleave", binding.onPointerLeave);
        root.removeEventListener("input", binding.onInspector);
        root.removeEventListener("focusin", binding.onInspector);
        root.removeEventListener("focusout", binding.onFocusOut);
        root.removeEventListener("keydown", binding.onKeyDown);
        clear(root);
        delete root.dataset.chartsBound;
        bindings.delete(root);
    }

    function bindAll() {
        for (const chart of rangeBindings.keys()) {
            if (!document.contains(chart)) unbindRange(chart);
        }
        for (const root of bindings.keys()) {
            if (!document.contains(root)) unbind(root);
        }
        document.querySelectorAll(".synchronized-charts, .spark-chart").forEach(bind);
    }

    const observer = new MutationObserver(mutations => {
        const rootsToClear = new Set();
        let structureChanged = false;
        const relevantNode = node => node.nodeType === Node.ELEMENT_NODE &&
            (node.matches?.(".synchronized-charts, .spark-chart, .chart-plot, .chart-sample") ||
                node.querySelector?.(".synchronized-charts, .spark-chart, .chart-plot, .chart-sample"));

        mutations.forEach(mutation => {
            if (mutation.type === "attributes") {
                const root = mutation.target.closest?.(".synchronized-charts, .spark-chart");
                if (root) rootsToClear.add(root);
                return;
            }

            const changedNodes = [...mutation.addedNodes, ...mutation.removedNodes];
            if (!changedNodes.some(relevantNode)) return;
            structureChanged = true;
            const root = mutation.target.closest?.(".synchronized-charts, .spark-chart");
            if (root && changedNodes.some(node => node.nodeType === Node.ELEMENT_NODE &&
                (node.matches?.(".chart-sample") || node.querySelector?.(".chart-sample"))))
                rootsToClear.add(root);
        });

        rootsToClear.forEach(clear);
        if (structureChanged) bindAll();
    });
    observer.observe(document.body, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: [
            "data-axis-kind",
            "data-axis",
            "data-value",
            "data-axis-label",
            "data-value-label"
        ]
    });
    bindAll();

    function rangeGroup(chart) {
        return chart.closest(".synchronized-charts") || chart;
    }

    function rangeState(group) {
        if (!rangeStates.has(group)) rangeStates.set(group, { anchor: null, gesture: null, axisKey: null, axisPromise: null, revision: 0 });
        return rangeStates.get(group);
    }

    function clearRangePreview(group) {
        group.querySelectorAll(".chart-range-preview").forEach(preview => preview.replaceChildren());
    }

    function cancelRange(group) {
        const state = rangeState(group);
        state.anchor = null;
        state.gesture = null;
        state.revision++;
        clearRangePreview(group);
    }

    function interpolateRange(before, after, position, startsNewSegment) {
        const fraction = (position - before.position) / (after.position - before.position);
        return {
            position,
            x: before.x * (1 - fraction) + after.x * fraction,
            y: before.y * (1 - fraction) + after.y * fraction,
            startsNewSegment
        };
    }

    function clippedRange(samples, start, end) {
        const result = [];
        let previous = null;
        for (const sample of samples) {
            if (previous && !sample.startsNewSegment && previous.position < start && sample.position > start)
                result.push(interpolateRange(previous, sample, start, true));
            if (sample.position >= start && sample.position <= end)
                result.push({ ...sample, startsNewSegment: !result.length || sample.startsNewSegment });
            if (previous && !sample.startsNewSegment && previous.position < end && sample.position > end)
                result.push(interpolateRange(previous, sample, end, !result.length));
            if (sample.position > end) break;
            previous = sample;
        }
        return result;
    }

    function rangeHit(binding, svg, event) {
        const rect = svg.getBoundingClientRect();
        const xScale = rect.width / 800;
        const x = event.clientX - rect.left;
        let best = null;
        let bestDistance = Infinity;
        let previous = null;
        for (const sample of binding.available) {
            const dx = sample.x * xScale - x;
            const distance = dx * dx;
            if (distance < bestDistance) {
                best = sample.position;
                bestDistance = distance;
            }
            if (previous && !sample.startsNewSegment) {
                const edgeX = (sample.x - previous.x) * xScale;
                const length = edgeX * edgeX;
                if (length > 0) {
                    const fraction = Math.max(0, Math.min(1,
                        ((x - previous.x * xScale) * edgeX) / length));
                    const projectedX = previous.x * xScale + fraction * edgeX;
                    const edgeDistance = (projectedX - x) ** 2;
                    if (edgeDistance < bestDistance) {
                        best = previous.position + fraction * (sample.position - previous.position);
                        bestDistance = edgeDistance;
                    }
                }
            }
            previous = sample;
        }
        return best;
    }

    function previewRange(group, first, last) {
        const start = Math.min(first, last);
        const end = Math.max(first, last);
        for (const [chart, binding] of rangeBindings) {
            if (rangeGroup(chart) !== group || !binding.options.enabled) continue;
            const preview = chart.querySelector(".chart-range-preview");
            if (!preview) continue;
            const samples = clippedRange(binding.options.samples || [], start, end);
            const stride = Math.max(1, Math.ceil(samples.length / 600));
            const commands = [];
            for (let index = 0; index < samples.length; index++) {
                const sample = samples[index];
                if (index % stride && index !== samples.length - 1 && !sample.startsNewSegment && !samples[index + 1].startsNewSegment) continue;
                commands.push(`${sample.startsNewSegment ? "M" : "L"}${sample.x},${sample.y}`);
            }
            const nodes = [];
            if (commands.length) {
                const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
                path.classList.add("chart-range-preview-line");
                path.setAttribute("d", commands.join(" "));
                nodes.push(path);
            }
            samples.filter(sample => sample.position === start || sample.position === end).forEach(sample => {
                const endpoint = document.createElementNS("http://www.w3.org/2000/svg", "circle");
                endpoint.classList.add("chart-range-preview-endpoint");
                endpoint.setAttribute("cx", String(sample.x));
                endpoint.setAttribute("cy", String(sample.y));
                endpoint.setAttribute("r", "5");
                nodes.push(endpoint);
            });
            preview.replaceChildren(...nodes);
        }
    }

    function unbindRange(chart, preserveAxis = false) {
        const binding = rangeBindings.get(chart);
        if (!binding) return;
        const group = rangeGroup(chart);
        cancelRange(group);
        for (const [name, handler] of Object.entries(binding.handlers)) chart.removeEventListener(name, handler);
        rangeBindings.delete(chart);
        delete chart.dataset.rangeReady;
        if (!preserveAxis && !Array.from(rangeBindings.keys()).some(candidate => rangeGroup(candidate) === group)) {
            const state = rangeState(group);
            state.axisKey = null;
            state.axisPromise = null;
        }
    }

    function bindRange(chart, reference, options) {
        unbindRange(chart, options.enabled);
        if (!options.enabled) return;
        const binding = { reference, options, available: [], handlers: {} };
        const group = rangeGroup(chart);
        clear(group);
        const shared = rangeState(group);
        const axisKey = `${options.streamIdentity}:${options.axis}`;
        if (shared.axisKey !== axisKey || !shared.axisPromise) {
            cancelRange(group);
            for (const [otherChart, otherBinding] of rangeBindings) {
                if (rangeGroup(otherChart) !== group) continue;
                otherBinding.available = [];
                delete otherChart.dataset.rangeReady;
            }
            shared.axisKey = axisKey;
            shared.axisPromise = reference.invokeMethodAsync("GetRangeAxis", options.streamIdentity, options.axis)
                .then(rows => rows.map(row => ({ position: row[0], x: row[1], y: 0, startsNewSegment: row[2] === 1 })));
        }
        const plot = event => {
            const svg = event.target.closest?.(".chart-plot");
            return svg && chart.contains(svg) ? svg : null;
        };
        binding.handlers.pointerdown = event => {
            const svg = plot(event);
            if (!svg || !event.isPrimary || event.button !== 0) return;
            const position = rangeHit(binding, svg, event);
            if (position === null) return;
            const state = rangeState(group);
            state.revision++;
            state.gesture = {
                pointerId: event.pointerId,
                touch: event.pointerType === "touch",
                anchor: state.anchor ?? position,
                hadAnchor: state.anchor !== null,
                x: event.clientX,
                y: event.clientY,
                moved: false
            };
            svg.setPointerCapture(event.pointerId);
            svg.focus({ preventScroll: true });
            previewRange(group, state.gesture.anchor, position);
            if (event.pointerType !== "touch") event.preventDefault();
        };
        binding.handlers.pointermove = event => {
            const svg = plot(event);
            const state = rangeState(group);
            if (!svg || (!state.gesture && state.anchor === null)) return;
            if (state.gesture && state.gesture.pointerId !== event.pointerId) return;
            const position = rangeHit(binding, svg, event);
            if (position === null) return;
            if (state.gesture) {
                state.gesture.moved ||= Math.hypot(event.clientX - state.gesture.x, event.clientY - state.gesture.y) >= 5;
                if (state.gesture.touch && state.gesture.moved) { clearRangePreview(group); return; }
                previewRange(group, state.gesture.anchor, position);
            } else previewRange(group, state.anchor, position);
        };
        binding.handlers.pointerup = event => {
            const state = rangeState(group);
            const gesture = state.gesture;
            const svg = plot(event);
            if (!gesture || gesture.pointerId !== event.pointerId || !svg) return;
            const position = rangeHit(binding, svg, event);
            state.gesture = null;
            if (svg.hasPointerCapture(event.pointerId)) svg.releasePointerCapture(event.pointerId);
            if (gesture.touch && gesture.moved) { cancelRange(group); return; }
            if (position === null) { cancelRange(group); return; }
            if (!gesture.moved && !gesture.hadAnchor) {
                state.anchor = gesture.anchor;
                previewRange(group, state.anchor, state.anchor);
                return;
            }
            const start = Math.min(gesture.anchor, position);
            const end = Math.max(gesture.anchor, position);
            if (start === end) { cancelRange(group); return; }
            state.anchor = null;
            previewRange(group, start, end);
            const revision = state.revision;
            reference.invokeMethodAsync("CommitRange", start, end).then(() => {
                if (rangeBindings.get(chart) === binding && state.revision === revision) clearRangePreview(group);
            }).catch(() => {
                if (rangeBindings.get(chart) === binding && state.revision === revision) cancelRange(group);
            });
        };
        binding.handlers.pointercancel = () => cancelRange(group);
        binding.handlers.keydown = event => {
            if (event.key === "Escape") { cancelRange(group); event.preventDefault(); }
        };
        for (const [name, handler] of Object.entries(binding.handlers)) chart.addEventListener(name, handler);
        rangeBindings.set(chart, binding);
        return shared.axisPromise.then(samples => {
            if (rangeBindings.get(chart) !== binding || shared.axisKey !== axisKey || !document.contains(chart)) return;
            binding.available = clippedRange(samples, options.start, options.end);
            chart.dataset.rangeReady = "true";
        }).catch(() => {
            if (rangeBindings.get(chart) === binding) unbindRange(chart);
        });
    }

    return { bindAll, bindRange, unbindRange };
})();
