let activityExplorerAntiforgeryToken;

async function activityExplorerUploadHeaders() {
    if (!activityExplorerAntiforgeryToken) {
        const response = await fetch("/internal/antiforgery/token", {
            credentials: "same-origin",
            headers: { "Accept": "application/json", "X-Activity-Explorer": "1" }
        });
        if (!response.ok) throw new Error("Could not initialize the secure upload session.");
        activityExplorerAntiforgeryToken = (await response.json()).token;
    }
    return { "X-Activity-Explorer": "1", "X-CSRF-TOKEN": activityExplorerAntiforgeryToken };
}

window.activityExplorerImports = {
    async uploadMany(inputId, ownerId, sourceKind, concurrency = 2) {
        const input = document.getElementById(inputId);
        const files = Array.from(input?.files || []);
        if (!files.length) throw new Error("Choose at least one file first.");
        if (!ownerId || ownerId === "00000000-0000-0000-0000-000000000000") throw new Error("Select a profile before importing.");

        const headers = await activityExplorerUploadHeaders();
        const ids = new Array(files.length);
        let next = 0;
        let completed = 0;
        const worker = async () => {
            while (next < files.length) {
                const index = next++;
                const file = files[index];
                const form = new FormData();
                form.append("file", file, file.name);
                const query = new URLSearchParams({ ownerId });
                if (sourceKind) query.set("sourceKind", sourceKind);
                const response = await fetch("/internal/imports?" + query, {
                    method: "POST", headers, body: form, credentials: "same-origin"
                });
                const result = await response.json();
                if (!response.ok) throw new Error(result.error || `Could not queue ${file.name}.`);
                ids[index] = result.id;
                completed++;
                input.closest("article")?.style.setProperty("--upload-progress", `${completed / files.length * 100}%`);
            }
        };
        await Promise.all(Array.from({ length: Math.min(Math.max(1, concurrency), files.length) }, worker));
        input.value = "";
        return { ids };
    },

    async upload(inputId, ownerId, sourceKind) {
        const input = document.getElementById(inputId);
        if (!input?.files?.length) throw new Error("Choose a file first.");
        const form = new FormData();
        form.append("file", input.files[0], input.files[0].name);
        const response = await fetch("/internal/imports?ownerId=" + encodeURIComponent(ownerId) + "&sourceKind=" + encodeURIComponent(sourceKind), {
            method: "POST",
            headers: await activityExplorerUploadHeaders(),
            body: form,
            credentials: "same-origin"
        });
        const result = await response.json();
        if (!response.ok) throw new Error(result.error || "The upload could not be queued.");
        input.value = "";
        return result;
    },

    async uploadRoute(inputId, ownerId, sport, name) {
        const input = document.getElementById(inputId);
        if (!input?.files?.length) throw new Error("Choose a GPX file first.");
        const form = new FormData();
        form.append("file", input.files[0], input.files[0].name);
        const query = new URLSearchParams({ ownerId, sport, name });
        const response = await fetch("/internal/routes/import?" + query, {
            method: "POST",
            headers: await activityExplorerUploadHeaders(),
            body: form,
            credentials: "same-origin"
        });
        const result = await response.json();
        if (!response.ok) throw new Error(result.error || "The GPX route could not be imported.");
        input.value = "";
        return result.id;
    },

    async uploadSegment(inputId, ownerId, sport, name, toleranceMeters, startIndex, endIndex, reverseDirection) {
        const input = document.getElementById(inputId);
        if (!input?.files?.length) throw new Error("Choose a segment path file first.");
        if (!ownerId || ownerId === "00000000-0000-0000-0000-000000000000") throw new Error("Select a profile before importing.");
        const form = new FormData();
        form.append("file", input.files[0], input.files[0].name);
        const query = new URLSearchParams({ ownerId, sport, name, toleranceMeters, reverseDirection });
        if (startIndex !== null && startIndex !== undefined && startIndex !== "") query.set("startIndex", startIndex);
        if (endIndex !== null && endIndex !== undefined && endIndex !== "") query.set("endIndex", endIndex);
        const response = await fetch("/internal/segments/import?" + query, {
            method: "POST",
            headers: await activityExplorerUploadHeaders(),
            body: form,
            credentials: "same-origin"
        });
        const result = await response.json();
        if (!response.ok) throw new Error(result.error || "The segment path could not be imported.");
        input.value = "";
        return result.id;
    }
};
