/**
 * Lazy load non-critical fonts
 */
export function loadFontsLazily() {
  if (typeof window !== "undefined") {
    // Load heavy Uthman bold font only when needed
    let injected = false;
    const loadUthmanBold = () => {
      if (injected) return;
      injected = true;
      const link = document.createElement("link");
      link.rel = "stylesheet";
      // Resolve via the <base> element directly. routify can remove
      // <base> from the DOM during navigation, after which document.baseURI
      // collapses to location.href and a relative path resolves wrong.
      // Reading <base> while it's intact (or falling back to a hardcoded
      // prefix that matches dmart's deployed mount) keeps this stable.
      const baseHref = document.querySelector("base")?.getAttribute("href") || "/cat/";
      link.href = `${baseHref.replace(/\/?$/, "/")}assets/uthman/uthman.css`;
      link.media = "print";
      document.head.appendChild(link);
    };

    // Load fonts when page is idle
    if ("requestIdleCallback" in window) {
      requestIdleCallback(() => {
        loadUthmanBold();
      });
    } else {
      // Fallback for browsers without requestIdleCallback
      setTimeout(() => {
        loadUthmanBold();
      }, 1000);
    }
  }
}
