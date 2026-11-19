// Server clock and countdowns (spec 04 task 7, R6.1 and R6.2).
// The server pushes the clock offset; the countdown ticks here in the browser every 250 ms.
window.auctionClock = (() => {
    let offsetMs = 0;                 // server clock minus browser clock
    const timers = new Map();         // element -> interval id

    const serverNow = () => Date.now() + offsetMs;

    function format(ms) {
        const total = Math.floor(ms / 1000);
        const d = Math.floor(total / 86400);
        const h = Math.floor((total % 86400) / 3600);
        const m = Math.floor((total % 3600) / 60);
        const s = total % 60;
        const pad = (n) => String(n).padStart(2, "0");
        return d > 0 ? `${d}d ${pad(h)}:${pad(m)}:${pad(s)}` : `${pad(h)}:${pad(m)}:${pad(s)}`;
    }

    function draw(el, endMs) {
        const left = endMs - serverNow();
        if (left <= 0) {
            // Stays like this until the server says the auction ended (state_changed).
            el.textContent = "Closing\u2026";
            el.classList.add("countdown-low");
            return;
        }
        el.textContent = format(left);
        el.classList.toggle("countdown-low", left < 60000);
    }

    return {
        nowMs: () => Date.now(),
        setOffset: (ms) => { offsetMs = Number(ms) || 0; },
        startCountdown(el, endMs) {
            if (!el) return;
            this.stopCountdown(el);
            draw(el, endMs);
            timers.set(el, setInterval(() => {
                if (!el.isConnected) { this.stopCountdown(el); return; }
                draw(el, endMs);
            }, 250));
        },
        stopCountdown(el) {
            const id = timers.get(el);
            if (id !== undefined) { clearInterval(id); timers.delete(el); }
            if (el) { el.classList.remove("countdown-low"); el.textContent = ""; }
        },
    };
})();
