// Starts a download without navigating the page. Firefox closes the page's connection the moment
// the page starts to navigate, even when the response turns out to be an attachment and the page
// stays. The circuit reconnects, but the browser's answer to the call that asked for the
// navigation went down with the old connection, and a minute later Blazor gives up waiting for it
// and ends the circuit. A link with the download attribute is a download from the start, in every
// browser, so the connection is left alone; Blazor's own link handling passes over such links.
export function download(url) {
    const link = document.createElement("a");
    link.href = url;
    link.download = "";
    document.body.appendChild(link);
    link.click();
    link.remove();
}
