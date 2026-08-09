// Hands the pointer to the chart for the rest of a drag, so a selection that leaves the plot — or
// the window — still reports its release back to the element it started on. Without this the
// pointerup lands on whatever the pointer happens to be over and the drag never ends.
//
// The capture is released by the browser on pointerup, so there is no matching teardown. Failing
// is harmless and expected: by the time this round-trip lands the pointer may already be gone, and
// the component ends the drag itself on the next move with no button held.
export function capturePointer(el, pointerId) {
    try {
        el.setPointerCapture(pointerId);
    } catch {
        /* no such pointer any more */
    }
}

// Keeps the chart sized to its available space in both dimensions and reports the plot area's
// real pixel size back to the component, so the SVG is rendered (not viewBox-scaled) and text and
// stroke widths stay crisp at any size.
//
// Width comes for free from the flex layout. For height, the card is stretched to fill from its
// top down to the bottom of the viewport; the plot area (a flex:1 child) then takes whatever is
// left after the legend, and its measured size drives the SVG. On narrow layouts, where the
// settings column wraps below the chart, filling to the viewport bottom would push content off
// screen, so we fall back to a fixed height instead.
export function observeSize(cardEl, plotEl, dotNetRef) {
    if (!cardEl || !plotEl) {
        return null;
    }

    const FALLBACK_HEIGHT = 340;
    const BOTTOM_GAP = 16;
    const MIN_HEIGHT = 200;
    const isWide = () => window.matchMedia("(min-width: 900px)").matches;

    // Stretch the card to the viewport bottom (wide layouts only). Never sets a style on the
    // observed element (plotEl), so this can't feed back into the ResizeObserver below.
    const applyHeight = () => {
        if (isWide()) {
            const top = cardEl.getBoundingClientRect().top;
            const available = Math.max(MIN_HEIGHT, window.innerHeight - top - BOTTOM_GAP);
            cardEl.style.height = `${available}px`;
        } else {
            cardEl.style.height = "";
        }
    };

    const report = () => {
        const width = plotEl.clientWidth;
        const height = isWide() ? plotEl.clientHeight : FALLBACK_HEIGHT;
        if (width > 0 && height > 0) {
            dotNetRef.invokeMethodAsync("SetSize", width, height, window.devicePixelRatio || 1);
        }
    };

    // devicePixelRatio has no change event of its own, and a resolution media query is the only
    // reliable way to hear about a move to a display of a different density: browser zoom also
    // fires a resize, but moving between monitors need not change the window's CSS size at all.
    // The query has to be rebuilt on every change, since it is pinned to one ratio.
    let ratioQuery = null;
    const onRatioChange = () => {
        watchRatio();
        report();
    };
    const watchRatio = () => {
        ratioQuery?.removeEventListener("change", onRatioChange);
        ratioQuery = window.matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`);
        ratioQuery.addEventListener("change", onRatioChange);
    };

    // Fires whenever the plot area changes size — covers width (flex reflow) and the height
    // change that applyHeight() induces via the flex:1 plot child.
    const observer = new ResizeObserver(() => report());
    observer.observe(plotEl);

    const onWindowResize = () => {
        applyHeight();
        report();
    };
    window.addEventListener("resize", onWindowResize);

    applyHeight();
    watchRatio();
    report();

    return {
        dispose: () => {
            observer.disconnect();
            window.removeEventListener("resize", onWindowResize);
            ratioQuery?.removeEventListener("change", onRatioChange);
        },
    };
}
