// Keeps the sections of a page marked data-live current: every 15 s it fetches the same page
// and swaps those sections in by id. Nothing runs while the tab is hidden, and a section is
// left alone while one of its details windows is open or it has focus, so nothing closes or
// moves under the reader.
(() => {
    const every = 15000;

    const busy = (section) => {
        if (section.contains(document.activeElement)) return true;
        try { return section.querySelector(":popover-open") !== null; } catch { return false; }
    };

    async function refresh() {
        if (document.hidden) return;
        try {
            const response = await fetch(location.href, { cache: "no-cache", credentials: "same-origin" });
            if (!response.ok || response.redirected) return;   // e.g. an admin session that ran out
            const fresh = new DOMParser().parseFromString(await response.text(), "text/html");
            for (const section of document.querySelectorAll("[data-live]")) {
                const next = fresh.getElementById(section.id);
                if (next && !busy(section)) section.replaceWith(next);
            }
        } catch {
            // Offline or restarting: the next tick tries again.
        }
    }

    setInterval(refresh, every);
    document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(); });
})();
