// Remembers how many points per series the graph's plot is wide enough for, so that the next
// opening of the graph can be prerendered at that density rather than at a guess. Read back by
// PingGraph.razor's prerender under the same name; a year, so a reader's usual screen is
// remembered between visits.
export function rememberDensity(name, points) {
    document.cookie = `${name}=${points}; path=/; max-age=31536000; samesite=lax`;
}
