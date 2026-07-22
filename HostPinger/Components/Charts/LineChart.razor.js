// Reports the chart card's content-box width back to the component whenever it changes,
// so the SVG can be rendered at real pixel width (crisp text/strokes) instead of being
// scaled by a viewBox. The observer fires once immediately on observe for the initial size.
export function observeWidth(element, dotNetRef) {
    if (!element) {
        return null;
    }

    const observer = new ResizeObserver(entries => {
        for (const entry of entries) {
            const width = entry.contentRect.width;
            dotNetRef.invokeMethodAsync("SetWidth", width);
        }
    });
    observer.observe(element);

    return {
        dispose: () => observer.disconnect(),
    };
}
