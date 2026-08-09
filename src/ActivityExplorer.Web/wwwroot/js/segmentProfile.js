window.segmentProfile = (() => {
    const bindings = new Map();
    const inspectionEvent = "activity-explorer:segment-inspection";

    function number(value) {
        const parsed = Number(value);
        return Number.isFinite(parsed) ? parsed : null;
    }

    function formatDistance(meters) {
        if (meters >= 1000)
            return new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(meters / 1000) + " km";
        return new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 }).format(meters) + " m";
    }

    function formatSample(sample) {
        const distance = number(sample.dataset.distance) ?? 0;
        const elevation = number(sample.dataset.elevation);
        const grade = number(sample.dataset.grade);
        const elevationText = elevation === null
            ? "elevation unavailable"
            : new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(elevation) + " m elevation";
        const gradeText = grade === null
            ? "local grade unavailable"
            : new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(grade) + "% local grade";
        return formatDistance(distance) + " | " + elevationText + " | " + gradeText;
    }

    function dispatch(group, sourceIndex) {
        group.dispatchEvent(new CustomEvent(inspectionEvent, {
            detail: { sourceIndex: Number.isInteger(sourceIndex) ? sourceIndex : null }
        }));
    }

    function bind(groupId, chartId) {
        const group = document.getElementById(groupId);
        const root = document.getElementById(chartId);
        const existing = bindings.get(chartId);
        if (existing?.root === root) return;
        if (existing) unbind(chartId);
        if (!group || !root) return;
        const svg = root.querySelector(".segment-grade-chart");
        const cursor = root.querySelector(".segment-grade-cursor");
        const tooltip = root.querySelector(".segment-grade-tooltip");
        const inspector = root.querySelector(".segment-grade-inspector");
        const samples = () => Array.from(root.querySelectorAll("[data-profile-sample]"));
        if (!svg || !cursor || !tooltip || !inspector) return;

        const show = (sample, showTooltip) => {
            if (!sample) return;
            const x = number(sample.getAttribute("cx")) ?? 0;
            cursor.setAttribute("x1", String(x));
            cursor.setAttribute("x2", String(x));
            cursor.classList.add("visible");
            const sourceIndex = number(sample.dataset.sourceIndex);
            dispatch(group, sourceIndex === null ? null : Math.round(sourceIndex));
            if (!showTooltip) {
                tooltip.classList.remove("visible");
                return;
            }
            tooltip.textContent = formatSample(sample);
            tooltip.classList.add("visible");
        };

        const clear = () => {
            cursor.classList.remove("visible");
            tooltip.classList.remove("visible");
            dispatch(group, null);
        };

        const nearest = event => {
            const available = samples();
            if (!available.length) return null;
            const rect = svg.getBoundingClientRect();
            const fraction = Math.max(0, Math.min(1, (event.clientX - rect.left) / Math.max(rect.width, 1)));
            const firstX = number(available[0].getAttribute("cx")) ?? 0;
            const lastX = number(available.at(-1).getAttribute("cx")) ?? firstX;
            const target = firstX + (lastX - firstX) * fraction;
            return available.reduce((best, candidate) => {
                const bestDistance = Math.abs((number(best.getAttribute("cx")) ?? 0) - target);
                const candidateDistance = Math.abs((number(candidate.getAttribute("cx")) ?? 0) - target);
                return candidateDistance < bestDistance ? candidate : best;
            });
        };

        const onPointerMove = event => show(nearest(event), true);
        const onPointerLeave = () => {
            if (document.activeElement !== inspector) clear();
        };
        const onInspector = () => show(samples()[Number(inspector.value)], false);
        const onInspectorFocus = () => show(samples()[Number(inspector.value)], false);
        const onInspectorBlur = () => clear();
        const onInspectorKeyDown = event => {
            if (event.key === "Escape") inspector.blur();
        };

        svg.addEventListener("pointermove", onPointerMove);
        svg.addEventListener("pointerleave", onPointerLeave);
        inspector.addEventListener("input", onInspector);
        inspector.addEventListener("focus", onInspectorFocus);
        inspector.addEventListener("blur", onInspectorBlur);
        inspector.addEventListener("keydown", onInspectorKeyDown);
        root.dataset.profileBound = "true";
        bindings.set(chartId, {
            root, svg, inspector, group, clear,
            onPointerMove, onPointerLeave, onInspector, onInspectorFocus, onInspectorBlur, onInspectorKeyDown
        });
    }

    function unbind(chartId) {
        const binding = bindings.get(chartId);
        if (!binding) return;
        binding.svg.removeEventListener("pointermove", binding.onPointerMove);
        binding.svg.removeEventListener("pointerleave", binding.onPointerLeave);
        binding.inspector.removeEventListener("input", binding.onInspector);
        binding.inspector.removeEventListener("focus", binding.onInspectorFocus);
        binding.inspector.removeEventListener("blur", binding.onInspectorBlur);
        binding.inspector.removeEventListener("keydown", binding.onInspectorKeyDown);
        binding.clear();
        delete document.getElementById(chartId)?.dataset.profileBound;
        bindings.delete(chartId);
    }

    function bindAll() {
        for (const [chartId, binding] of bindings) {
            if (!document.contains(binding.root)) unbind(chartId);
        }
        document.querySelectorAll(".segment-grade-profile[data-inspection-group]").forEach(root =>
            bind(root.dataset.inspectionGroup, root.id));
    }

    const observer = new MutationObserver(bindAll);
    observer.observe(document.body, { childList: true, subtree: true });
    bindAll();

    return { bind, unbind };
})();
