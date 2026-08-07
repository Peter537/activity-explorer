window.activityCharts = {
    bind(groupId) {
        const group = document.getElementById(groupId);
        if (!group || group.dataset.bound === "1") return;
        group.dataset.bound = "1";
        const charts = () => Array.from(group.querySelectorAll(".time-series-chart"));

        const update = (event) => {
            const sourceSvg = event.currentTarget;
            const rect = sourceSvg.getBoundingClientRect();
            const fraction = Math.max(0, Math.min(1, (event.clientX - rect.left) / Math.max(rect.width, 1)));
            const axisKind = group.dataset.axisKind || "time";
            charts().forEach(chart => {
                const svg = chart.querySelector("svg");
                if (!svg) return;
                const cursor = chart.querySelector(".chart-cursor");
                cursor.setAttribute("x1", String(fraction * 800));
                cursor.setAttribute("x2", String(fraction * 800));
                cursor.classList.add("visible");

                const samples = Array.from(chart.querySelectorAll(".chart-sample"));
                if (!samples.length) return;
                let nearest = samples[0];
                let distance = Math.abs(Number(nearest.getAttribute("cx")) / 800 - fraction);
                for (const sample of samples.slice(1)) {
                    const candidateDistance = Math.abs(Number(sample.getAttribute("cx")) / 800 - fraction);
                    if (candidateDistance < distance) {
                        nearest = sample;
                        distance = candidateDistance;
                    }
                }

                const axis = Number(nearest.dataset.axis);
                const value = Number(nearest.dataset.value);
                const unit = chart.dataset.unit || "";
                const axisLabel = axisKind === "distance"
                    ? new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(axis / 1000) + " km"
                    : formatDuration(axis);
                const tooltip = chart.querySelector(".chart-tooltip");
                tooltip.textContent = axisLabel + " | " + new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(value) + " " + unit;
                tooltip.style.left = Math.max(8, Math.min(88, fraction * 100)) + "%";
                tooltip.classList.add("visible");
            });
        };

        const clear = () => charts().forEach(chart => {
            chart.querySelector(".chart-cursor")?.classList.remove("visible");
            chart.querySelector(".chart-tooltip")?.classList.remove("visible");
        });

        charts().forEach(chart => {
            const svg = chart.querySelector("svg");
            if (!svg || svg.dataset.bound === "1") return;
            svg.dataset.bound = "1";
            svg.addEventListener("pointermove", update);
            svg.addEventListener("pointerleave", clear);
        });
    }
};

function formatDuration(seconds) {
    const total = Math.max(0, Math.round(seconds));
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const remainder = total % 60;
    return hours > 0
        ? hours + ":" + String(minutes).padStart(2, "0") + ":" + String(remainder).padStart(2, "0")
        : minutes + ":" + String(remainder).padStart(2, "0");
}
