window.errorLensTheme = {
    // Dark is the default; the light theme is an explicit choice, remembered in this browser
    toggle() {
        const root = document.documentElement;
        root.dataset.theme = root.dataset.theme === "light" ? "dark" : "light";
        try { localStorage.setItem("errorlens-theme", root.dataset.theme); } catch { /* private window */ }
    }
};
