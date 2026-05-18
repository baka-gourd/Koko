<script module lang="ts">
    type TreesModule = typeof import("@pierre/trees");

    let treesModulePromise: Promise<TreesModule> | null = null;

    function loadTrees(): Promise<TreesModule> {
        treesModulePromise ??= import("@pierre/trees");
        return treesModulePromise;
    }

    const TREE_UNSAFE_CSS = `
        :host,
        button[data-type='item'],
        [data-type='context-menu-trigger'] {
            border-radius: 0;
        }

        [data-file-tree-virtualized-scroll='true'] {
            padding-block: 8px;
            scroll-padding-block: 8px;
        }

        [data-file-tree-search-container] {
            box-sizing: border-box;
            margin: 0;
            padding: 8px
                var(--trees-scrollbar-gutter-measured, var(--trees-scrollbar-gutter))
                8px 0;
        }

        [data-file-tree-search-input] {
            box-sizing: border-box;
            min-width: 0;
            width: 100%;
            margin: 0;
            border: 0;
            border-block-end: 1px solid var(--cds-border-strong, var(--trees-border-color));
            border-radius: 0;
            background: var(--cds-background, var(--trees-bg));
            outline: none;
        }

        [data-file-tree-search-input]:hover {
            background: var(--cds-background-hover, var(--trees-bg-muted));
        }

        [data-file-tree-search-input]:focus,
        [data-file-tree-search-input][data-file-tree-search-input-fake-focus='true'] {
            border-block-end-color: transparent;
            outline: 2px solid var(--cds-focus, var(--trees-focus-ring-color));
            outline-offset: -2px;
        }

        button[data-type='item'][data-item-selected='true'] {
            outline: 2px solid var(--trees-selected-focused-border-color);
            outline-offset: -2px;
        }
    `;
</script>

<script lang="ts">
    import { onMount } from "svelte";
    import type { FileTreeDensity, FileTreeOptions } from "@pierre/trees";
    import {
        applyInlineStyles,
        createCarbonTreeStyles,
    } from "$lib/utils/carbon-to-pierre";

    type FileTreeInstance = InstanceType<TreesModule["FileTree"]>;

    let {
        paths,
        pathsVersion = 0,
        selectedPath = $bindable<string | null>(null),
        density = "default",
        search = true,
    } = $props<{
        paths: readonly string[];
        pathsVersion?: number;
        selectedPath?: string | null;
        density?: FileTreeDensity;
        search?: boolean;
    }>();

    let treeHost: HTMLElement;
    let tree = $state<FileTreeInstance | null>(null);
    let treesModule: TreesModule | null = null;
    let mountedVersion = $state<number | null>(null);
    let lastAppliedSelectedPath = $state<string | null>(null);

    function applyCarbonTheme(): void {
        if (!treeHost) {
            return;
        }

        applyInlineStyles(treeHost, createCarbonTreeStyles());

        treeHost.dataset.carbonFileTree = "true";
    }

    function observeCarbonTheme(): () => void {
        const observer = new MutationObserver(() => {
            applyCarbonTheme();
        });

        observer.observe(document.documentElement, {
            attributes: true,
            attributeFilter: ["theme"],
        });

        return () => observer.disconnect();
    }

    function createTreeOptions(loaded: TreesModule): FileTreeOptions {
        return {
            paths,
            preparedInput: loaded.prepareFileTreeInput(paths),
            presorted: false,
            initialSelectedPaths: selectedPath ? [selectedPath] : [],

            icons: {
                colored: false
            },
            search,
            searchBlurBehavior: "retain",
            flattenEmptyDirectories: true,
            initialExpansion: "closed",
            density,
            unsafeCSS: TREE_UNSAFE_CSS,

            onSelectionChange: (selectedPaths) => {
                selectedPath = selectedPaths[0] ?? null;
            },
        };
    }

    function getAncestorPaths(path: string): string[] {
        const segments = path.split("/").filter(Boolean);
        return segments
            .slice(0, -1)
            .map((_, index) => segments.slice(0, index + 1).join("/"));
    }

    function getCanonicalSelectedPath(
        path: string | null | undefined,
    ): string | null {
        const trimmed = path?.trim();
        if (!trimmed || !tree) {
            return null;
        }

        return tree.getItem(trimmed)?.getPath() ?? trimmed;
    }

    function syncSelectionToTree(): void {
        if (!tree) {
            return;
        }

        const nextSelectedPath = getCanonicalSelectedPath(selectedPath);
        const currentSelectedPath = tree.getSelectedPaths()[0] ?? null;

        if (
            currentSelectedPath === nextSelectedPath &&
            lastAppliedSelectedPath === nextSelectedPath
        ) {
            return;
        }

        if (currentSelectedPath && currentSelectedPath !== nextSelectedPath) {
            tree.getItem(currentSelectedPath)?.deselect();
        }

        if (nextSelectedPath) {
            for (const ancestorPath of getAncestorPaths(nextSelectedPath)) {
                const ancestor = tree.getItem(ancestorPath);
                if (ancestor && "expand" in ancestor) {
                    ancestor.expand();
                }
            }

            const item = tree.getItem(nextSelectedPath);
            item?.select();
            item?.focus();
        }

        lastAppliedSelectedPath = nextSelectedPath;
    }

    onMount(() => {
        let disposed = false;
        let stopThemeObserver: (() => void) | null = null;
        let mountedTree: FileTreeInstance | null = null;

        void (async () => {
            applyCarbonTheme();
            stopThemeObserver = observeCarbonTheme();

            const loaded = await loadTrees();

            if (disposed) {
                return;
            }

            treesModule = loaded;

            const instance = new loaded.FileTree(createTreeOptions(loaded));

            instance.render({
                fileTreeContainer: treeHost,
            });

            if (disposed) {
                instance.cleanUp();
                return;
            }

            mountedTree = instance;
            tree = instance;
            mountedVersion = pathsVersion;
            syncSelectionToTree();
            applyCarbonTheme();
        })();

        return () => {
            disposed = true;

            stopThemeObserver?.();
            stopThemeObserver = null;

            mountedTree?.cleanUp();
            mountedTree = null;
            tree = null;
            treesModule = null;
            mountedVersion = null;
        };
    });

    $effect(() => {
        if (!tree || !treesModule) {
            return;
        }

        if (mountedVersion === pathsVersion) {
            return;
        }

        const preparedInput = treesModule.prepareFileTreeInput(paths);

        tree.resetPaths(paths, {
            preparedInput,
        });

        mountedVersion = pathsVersion;
        syncSelectionToTree();
    });

    $effect(() => {
        syncSelectionToTree();
    });
</script>

<file-tree-container class="carbon-file-tree-mount" bind:this={treeHost}
></file-tree-container>

<style>
    .carbon-file-tree-mount {
        height: 100%;
        min-height: 0;
        overflow: hidden;
    }
</style>
