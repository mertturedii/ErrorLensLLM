// Light/dark choice: follows the system until the user picks one, then remembered in this browser
window.errorLensTheme = {
    toggle() {
        const root = document.documentElement;
        const dark = root.dataset.theme
            ? root.dataset.theme === "dark"
            : window.matchMedia("(prefers-color-scheme: dark)").matches;
        root.dataset.theme = dark ? "light" : "dark";
        try { localStorage.setItem("errorlens-theme", root.dataset.theme); } catch { /* private window */ }
    }
};
