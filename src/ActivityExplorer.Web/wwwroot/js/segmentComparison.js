window.segmentComparison = (() => {
    const bindings = new Map();
    const eventName = "activity-explorer:comparison-inspection";
    const number = (value, digits = 1) => new Intl.NumberFormat(undefined, { maximumFractionDigits: digits }).format(value);
    const distance = meters => meters >= 1000 ? `${number(meters / 1000, 3)} km` : `${number(meters)} m`;
    const signed = seconds => `${seconds > 0 ? "+" : seconds < 0 ? "−" : ""}${number(Math.abs(seconds), 3)} s`;
    const sensor = (value, unit) => Number.isFinite(value) ? `${number(value)} ${unit}` : "Not recorded";

    function speed(value, sport) {
        if (!Number.isFinite(value)) return "Not recorded";
        if (value === 0) return sport === "Cycling" ? "0 km/h" : "Stopped (0 m/s)";
        if (sport === "Cycling") return `${number(value * 3.6)} km/h`;
        const seconds = (sport === "Rowing" ? 500 : 1000) / value;
        return `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, "0")} ${sport === "Rowing" ? "/500 m" : "/km"}`;
    }

    function metricValue(sample, metric, baseline, sport) {
        const value = baseline ? sample.baseline : sample.comparison;
        if (metric === 0) return sample.deltaSeconds;
        if (metric === 2) return value.heartRate;
        if (metric === 3) return value.powerWatts;
        if (!Number.isFinite(value.speedMetersPerSecond)) return null;
        if (sport === "Cycling") return value.speedMetersPerSecond * 3.6;
        return value.speedMetersPerSecond > 0 ? (sport === "Rowing" ? 500 : 1000) / value.speedMetersPerSecond / 60 : null;
    }

    function bind(id, groupId, data) {
        unbind(id);
        const root = document.getElementById(id);
        const group = document.getElementById(groupId);
        const slider = root?.querySelector(".comparison-inspector");
        if (!root || !group || !slider || !data.samples?.length) return;
        const listeners = [];
        const listen = (element, name, handler) => { element.addEventListener(name, handler); listeners.push([element, name, handler]); };
        const plots = Array.from(root.querySelectorAll("[data-comparison-metric]")).map(panel => ({
            panel, metric: Number(panel.dataset.comparisonMetric), svg: panel.querySelector(".comparison-plot")
        })).filter(plot => plot.svg);
        const location = root.querySelector(".comparison-location");
        const output = (name, value) => { root.querySelector(`[data-inspection-value="${name}"]`).textContent = value; };
        const dispatch = sample => {
            if (!sample && group.comparisonInspectionOwner !== id) return;
            const detail = {
                distanceMeters: sample?.distanceMeters ?? null,
                baseline: sample?.baseline.position ?? null,
                comparison: sample?.comparison.position ?? null
            };
            if (sample) group.comparisonInspectionOwner = id;
            else delete group.comparisonInspectionOwner;
            group.comparisonInspection = detail;
            group.dispatchEvent(new CustomEvent(eventName, { detail }));
        };
        const show = (index, announce = true) => {
            index = Math.max(0, Math.min(data.samples.length - 1, index));
            const sample = data.samples[index];
            slider.value = String(index);
            slider.setAttribute("aria-valuetext", `${distance(sample.distanceMeters)}; time difference ${signed(sample.deltaSeconds)}`);
            const text = `${distance(sample.distanceMeters)} · Time difference ${signed(sample.deltaSeconds)}`;
            location.setAttribute("aria-live", announce ? "polite" : "off");
            location.textContent = text;
            for (const side of ["baseline", "comparison"]) {
                const value = sample[side];
                output(`${side}-elapsed`, `${number(value.elapsedSeconds, 3)} s`);
                output(`${side}-speed`, speed(value.speedMetersPerSecond, data.sport));
                output(`${side}-heart`, sensor(value.heartRate, "bpm"));
                output(`${side}-power`, sensor(value.powerWatts, "W"));
            }
            for (const plot of plots) {
                const x = 800 * sample.distanceMeters / Math.max(data.distance, 1e-9);
                const cursor = plot.svg.querySelector(".comparison-cursor");
                cursor.setAttribute("x1", String(x));
                cursor.setAttribute("x2", String(x));
                cursor.setAttribute("visibility", "visible");
                const scale = data.scales.find(candidate => candidate.metric === plot.metric);
                for (const [selector, baseline] of [[".baseline", true], [".compared", false]]) {
                    const marker = plot.svg.querySelector(`.comparison-marker${selector}`);
                    const value = metricValue(sample, plot.metric, baseline, data.sport);
                    const visible = Number.isFinite(value) && (baseline || plot.metric !== 0);
                    marker.setAttribute("visibility", visible ? "visible" : "hidden");
                    if (visible) {
                        marker.setAttribute("cx", String(x));
                        marker.setAttribute("cy", String(180 - 180 * Math.max(0, Math.min(1, (value - scale.minimum) / Math.max(scale.maximum - scale.minimum, 1e-9)))));
                    }
                }
            }
            root.dataset.inspectionDistance = String(sample.distanceMeters);
            root.dataset.inspectionIndex = String(index);
            dispatch(sample);
        };
        const nearest = target => {
            let lower = 0;
            let upper = data.samples.length - 1;
            while (lower < upper) {
                const middle = Math.floor((lower + upper) / 2);
                if (data.samples[middle].distanceMeters < target) lower = middle + 1;
                else upper = middle;
            }
            if (lower > 0 && target - data.samples[lower - 1].distanceMeters < data.samples[lower].distanceMeters - target) lower--;
            // Arrival samples are excluded by the component; repeated-distance inspection uses departure.
            while (lower + 1 < data.samples.length && data.samples[lower + 1].distanceMeters === data.samples[lower].distanceMeters) lower++;
            return lower;
        };
        for (const plot of plots) {
            const point = event => {
                const rect = plot.svg.getBoundingClientRect();
                show(nearest(Math.max(0, Math.min(1, (event.clientX - rect.left) / Math.max(rect.width, 1))) * data.distance), false);
            };
            listen(plot.svg, "pointermove", point);
            listen(plot.svg, "pointerdown", point);
        }
        listen(slider, "input", () => show(Number(slider.value)));
        listen(slider, "focus", () => show(Number(slider.value)));
        root.dataset.comparisonBound = "true";
        bindings.set(id, { root, listeners, dispatch });
        show(0, false);
    }

    function unbind(id) {
        const binding = bindings.get(id);
        if (!binding) return;
        for (const [element, name, handler] of binding.listeners) element.removeEventListener(name, handler);
        binding.dispatch(null);
        delete binding.root.dataset.comparisonBound;
        bindings.delete(id);
    }

    return { bind, unbind };
})();
