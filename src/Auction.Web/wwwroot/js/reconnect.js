// Browser reconnect (spec 04 task 8, R5.1 and R5.2).
// Blazor adds these classes to #components-reconnect-modal:
//   components-reconnect-show      lost the connection, trying again  -> banner, page blocked
//   components-reconnect-failed    gave up trying                     -> banner with a Reload link
//   components-reconnect-rejected  the server no longer has our page  -> reload at once
// A reload starts a new page connection (on this or another web copy), which loads a fresh snapshot.
(() => {
    const modal = document.getElementById("components-reconnect-modal");
    if (modal) {
        new MutationObserver(() => {
            if (modal.classList.contains("components-reconnect-rejected")) {
                location.reload();
            }
        }).observe(modal, { attributes: true, attributeFilter: ["class"] });

        const reload = document.getElementById("components-reconnect-reload");
        if (reload) {
            reload.addEventListener("click", (e) => { e.preventDefault(); location.reload(); });
        }
    }

    // Retry every 2 s for about a minute, instead of the default 20 s gap.
    Blazor.start({
        circuit: {
            reconnectionOptions: { maxRetries: 30, retryIntervalMilliseconds: 2000 },
        },
    });
})();
