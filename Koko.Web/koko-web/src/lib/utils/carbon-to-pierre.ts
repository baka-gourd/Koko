import type { TreeThemeStyles } from "@pierre/trees";

export type CarbonTreeThemeName = "white" | "g100";

const FALLBACKS: Record<CarbonTreeThemeName, Record<string, string>> = {
  white: {
    background: "#ffffff",
    backgroundHover: "#e8e8e8",
    backgroundSelected: "#e0e0e0",
    textPrimary: "#161616",
    textSecondary: "#525252",
    borderSubtle: "#e0e0e0",
    borderStrong: "#8d8d8d",
  },
  g100: {
    background: "#161616",
    backgroundHover: "#333333",
    backgroundSelected: "#393939",
    textPrimary: "#f4f4f4",
    textSecondary: "#c6c6c6",
    borderSubtle: "#393939",
    borderStrong: "#8d8d8d",
  },
};

function token(name: string, fallback: string): string {
  return `var(--cds-${name}, ${fallback})`;
}

function createCarbonTokens(themeName: CarbonTreeThemeName) {
  const fallback = FALLBACKS[themeName];

  return {
    background: token("background", fallback.background),
    backgroundHover: token("background-hover", fallback.backgroundHover),
    backgroundSelected: token("background-selected", fallback.backgroundSelected),

    textPrimary: token("text-primary", fallback.textPrimary),
    textSecondary: token("text-secondary", fallback.textSecondary),

    borderSubtle: token("border-subtle", fallback.borderSubtle),
    borderStrong: token("border-strong", fallback.borderStrong),

    focus: token("focus", "#0f62fe"),

    success: token("support-success", "#24a148"),
    info: token("support-info", "#4589ff"),
    error: token("support-error", "#da1e28"),
  } as const;
}

export function getCarbonTreeThemeName(): CarbonTreeThemeName {
  if (typeof document === "undefined") {
    return "white";
  }

  const theme = document.documentElement.getAttribute("theme");

  if (theme === "white" || theme === "g100") {
    return theme;
  }

  return "white";
}

export function createCarbonTreeStyles(
  themeName: CarbonTreeThemeName = getCarbonTreeThemeName(),
): TreeThemeStyles {
  const cds = createCarbonTokens(themeName);

  return {
    background: cds.background,
    color: cds.textPrimary,
    borderColor: cds.borderSubtle,
    borderRadius: "0",
    colorScheme: themeName === "g100" ? "dark" : "light",

    "--trees-bg-override": cds.background,
    "--trees-bg-muted-override": cds.backgroundHover,
    "--trees-fg-override": cds.textPrimary,
    "--trees-fg-muted-override": cds.textSecondary,
    "--trees-border-color-override": cds.borderSubtle,
    "--trees-focus-ring-color-override": cds.focus,
    "--trees-focus-ring-width-override": "2px",
    "--trees-focus-ring-offset-override": "-2px",

    "--trees-selected-bg-override": cds.backgroundSelected,
    "--trees-selected-fg-override": cds.textPrimary,
    "--trees-selected-focused-border-color-override": cds.focus,

    "--trees-search-bg-override": cds.background,
    "--trees-search-fg-override": cds.textPrimary,
    "--trees-input-bg-override": cds.background,
    "--trees-scrollbar-thumb-override": cds.borderStrong,

    "--trees-status-added-override": cds.success,
    "--trees-status-modified-override": cds.info,
    "--trees-status-deleted-override": cds.error,
    "--trees-git-added-color-override": cds.success,
    "--trees-git-modified-color-override": cds.info,
    "--trees-git-deleted-color-override": cds.error,

    "--trees-border-radius-override": "0px",
    "--trees-item-margin-x-override": "0px",
    "--trees-padding-inline-override": "0px",
    "--truncate-marker-background-color": cds.background,
  };
}

export function applyInlineStyles(
  element: HTMLElement,
  styles: Record<string, string>,
): void {
  for (const [key, value] of Object.entries(styles)) {
    if (value.length === 0) {
      continue;
    }

    if (key.startsWith("--")) {
      element.style.setProperty(key, value);
    } else {
      (element.style as unknown as Record<string, string>)[key] = value;
    }
  }
}
