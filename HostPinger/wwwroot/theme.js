// The colour theme: "auto" (follow the browser), "light" or "dark", remembered per browser.
//
// This is a blocking classic script in <head> rather than a module like the two component
// scripts. A module is deferred, so it would not run until the document had been parsed and
// the page would paint light before turning dark. Blocking here costs a few milliseconds and
// gets the attribute onto <html> before the first paint instead.

(function () {
    const STORAGE_KEY = "hostpinger-theme";
    const CHOICES = ["auto", "light", "dark"];
    const DEFAULT_CHOICE = "auto";
    const RADIO_NAME = "theme-choice";

    const darkQuery = window.matchMedia("(prefers-color-scheme: dark)");

    // localStorage throws rather than returning null when the browser blocks storage — private
    // windows, third-party-cookie policies. The theme is a preference, so a failure to read or
    // write one is not worth breaking the page over: fall back to the default and carry on.
    function storedChoice() {
        try {
            const stored = localStorage.getItem(STORAGE_KEY);
            return CHOICES.includes(stored) ? stored : DEFAULT_CHOICE;
        } catch {
            return DEFAULT_CHOICE;
        }
    }

    function store(choice) {
        try {
            localStorage.setItem(STORAGE_KEY, choice);
        } catch {
            // Ignored; the theme still applies for this page load.
        }
    }

    // Bootstrap only understands the two concrete themes, so "auto" is resolved here rather
    // than handed on.
    function apply(choice) {
        const dark = choice === "dark" || (choice === "auto" && darkQuery.matches);
        document.documentElement.setAttribute("data-bs-theme", dark ? "dark" : "light");
    }

    // Everything the server's markup does not carry. Enhanced navigation diffs the new page
    // against the live one, and the server has no way of knowing what this browser chose: the
    // theme attribute is on <html>, which the new document does not have, so it is removed, and
    // the control comes back with "auto" selected, which is what the markup says. Both have to
    // be put back after every such update, and on the first load for the control.
    function restore() {
        const choice = storedChoice();
        apply(choice);

        const selected = document.querySelector(`input[name="${RADIO_NAME}"][value="${choice}"]`);
        if (selected) {
            selected.checked = true;
        }
    }

    apply(storedChoice());

    // Delegated so it holds for a control that is not in the DOM yet, and for the replacement
    // one that enhanced navigation swaps in.
    document.addEventListener("change", function (event) {
        const input = event.target;
        if (input instanceof HTMLInputElement && input.name === RADIO_NAME
            && CHOICES.includes(input.value)) {
            store(input.value);
            apply(input.value);
        }
    });

    // Only moves the page while the choice is "auto"; an explicit light or dark stays put.
    darkQuery.addEventListener("change", function () {
        const choice = storedChoice();
        if (choice === "auto") {
            apply(choice);
        }
    });

    // blazor.web.js is a plain script at the end of the body, so it has both run and put Blazor
    // on the window by the time this fires — and it fires before any enhanced navigation can
    // have happened.
    document.addEventListener("DOMContentLoaded", function () {
        restore();
        window.Blazor?.addEventListener("enhancedload", restore);
    });
})();
