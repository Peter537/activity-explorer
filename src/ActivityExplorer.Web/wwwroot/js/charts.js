window.activityCharts = (() => {
    const bindings = new Map();

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
            const svg = event.target.closest?.(".chart-plot");
            if (!svg || !root.contains(svg)) return;
            const nextPlot = event.relatedTarget?.closest?.(".chart-plot");
            if (nextPlot && root.contains(nextPlot)) return;
            clear(root);
            restoreFocusedInspector(root);
        };

        const onPointerLeave = () => {
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

    return { bindAll };
})();
