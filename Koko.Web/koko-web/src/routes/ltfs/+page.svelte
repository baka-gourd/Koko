<script lang="ts">
    import { onMount, untrack } from "svelte";
    import { goto } from "$app/navigation";
    import { page } from "$app/state";
    import "@carbon/charts-svelte/styles.css";
    import FileTree from "$lib/components/ltfs/FileTree.svelte";
    import {
        DataTable,
        Toolbar,
        ToolbarContent,
        ToolbarSearch,
    } from "carbon-components-svelte";
    import { ChartTheme, LineChart, ScaleTypes } from "@carbon/charts-svelte";
    import type {
        ChartTabularData,
        LineChartOptions,
    } from "@carbon/charts-svelte";
    import { bytes } from "ts-humanize";
    import {
        getTapeSchemaDirectoryFiles,
        getTapeSchemaFile,
        getTapeSchemaFiles,
        startKokoHub,
        type TapeSchemaFile,
    } from "$lib/api/koko-hub";
    import { notifyError, notifyException } from "$lib/notifications";
    import {
        getLtfsTableColumns,
        ltfsTableSettings,
        type LtfsTableColumn,
    } from "$lib/ltfs-table-settings";

    const minLeftWidth = 360;
    const minRightWidth = 280;
    const handleWidth = 12;
    const speedPointCount = 24;

    type LtfsMode = "readonly" | "realtime";
    type TableRow = { id: string } & Record<string, string | number>;

    let paths = $state<readonly string[]>([]);
    let pathsVersion = $state(0);
    let treeSelectedPath = $state<string | null>(null);
    let schemaFilePaths = $state<ReadonlySet<string>>(new Set());
    let schemaDirectoryPaths = $state<ReadonlySet<string>>(new Set([""]));
    let schemaDevice = $state<string | null>(null);
    let schemaRows = $state<TapeSchemaFile[]>([]);
    let isLoadingSchema = $state(false);
    let lastMissingDeviceNotice = $state<string | null>(null);
    let requestedTreeDevice = $state<string | null>(null);
    let loadedTreeDevice = $state<string | null>(null);
    let failedTreeDevice = $state<string | null>(null);
    let loadedSelectionKey = $state<string | null>(null);
    let treeLoadRequest = 0;
    let selectionLoadRequest = 0;
    let splitPane: HTMLDivElement;
    let tablePanelHeight = $state(408);
    let tableViewportHeight = $derived(Math.max(144, tablePanelHeight - 48));
    let tableSearch = $state("");
    let leftWidth = $state<number | null>(null);
    let isResizing = $state(false);
    let chartTheme = $state(ChartTheme.WHITE);
    let speedData = $state<ChartTabularData>(createInitialSpeedData());
    let mode = $derived(toLtfsMode(page.url.searchParams.get("type")));
    let device = $derived(page.url.searchParams.get("device")?.trim() ?? "");
    let urlPath = $derived(
        normalizeLtfsPath(page.url.searchParams.get("path")),
    );
    let tableColumns = $derived(getLtfsTableColumns($ltfsTableSettings));
    let tableHeaders = $derived(
        tableColumns.map((column) => ({
            key: column.id,
            value: column.label,
        })),
    );
    let filteredSchemaRows = $derived(
        filterSchemaRows(schemaRows, tableColumns, tableSearch),
    );
    let tableRows = $derived(createTableRows(filteredSchemaRows, tableColumns));
    let currentSpeed = $derived(
        Number(speedData.at(-1)?.value ?? 0).toFixed(1),
    );
    let speedChartOptions = $derived(createSpeedChartOptions(chartTheme));
    let tableVirtualize = $derived({
        itemHeight: 24,
        containerHeight: tableViewportHeight,
        overscan: 8,
        threshold: 100,
    });

    let splitColumns = $derived(
        leftWidth === null
            ? `minmax(${minLeftWidth}px, 8fr) ${handleWidth}px minmax(${minRightWidth}px, 2fr)`
            : `${leftWidth}px ${handleWidth}px minmax(${minRightWidth}px, 1fr)`,
    );

    function toLtfsMode(value: string | null): LtfsMode {
        return value === "realtime" ? "realtime" : "readonly";
    }

    function normalizeLtfsPath(value: string | null | undefined): string {
        const trimmed = value?.trim();
        if (!trimmed || trimmed === "/") {
            return "";
        }

        return trimmed.replaceAll("\\", "/").replace(/^\/+|\/+$/g, "");
    }

    function toTreeSelectedPath(path: string): string | null {
        if (!path) {
            return null;
        }

        if (schemaDirectoryPaths.has(path) && !schemaFilePaths.has(path)) {
            return `${path}/`;
        }

        return path;
    }

    function toLtfsPathFromTreePath(path: string | null): string {
        return normalizeLtfsPath(path);
    }

    function displayLtfsPath(path: string): string {
        return path ? `/${path}` : "/";
    }

    function getDirectoryPaths(
        filePaths: readonly string[],
    ): ReadonlySet<string> {
        const directories = new Set<string>([""]);

        for (const path of filePaths) {
            const segments = path.split("/").filter(Boolean);
            for (let index = 1; index < segments.length; index += 1) {
                directories.add(segments.slice(0, index).join("/"));
            }
        }

        return directories;
    }

    function xattrValue(file: TapeSchemaFile, key: string): string {
        return (
            file.extendedAttributes.find((item) => item.key === key)?.value ??
            ""
        );
    }

    function cellValue(file: TapeSchemaFile, column: LtfsTableColumn): string {
        if (column.kind === "xattr") {
            return xattrValue(file, column.xattrKey ?? "");
        }

        switch (column.id) {
            case "name":
                return file.name;
            case "size":
                return bytes(file.length);
            case "readOnly":
                return file.readOnly ? "是" : "否";
            default:
                return "";
        }
    }

    function createTableRows(
        files: readonly TapeSchemaFile[],
        columns: readonly LtfsTableColumn[],
    ): TableRow[] {
        return files.map((file) => {
            const row: TableRow = {
                id: file.path,
            };

            for (const column of columns) {
                row[column.id] = cellValue(file, column);
            }

            return row;
        });
    }

    function filterSchemaRows(
        files: readonly TapeSchemaFile[],
        columns: readonly LtfsTableColumn[],
        search: string,
    ): readonly TapeSchemaFile[] {
        const query = search.trim().toLocaleLowerCase();
        if (!query) {
            return files;
        }

        return files.filter((file) =>
            columns.some((column) =>
                cellValue(file, column).toLocaleLowerCase().includes(query),
            ),
        );
    }

    function updatePathParam(path: string): void {
        const nextUrl = new URL(page.url);
        nextUrl.searchParams.set("path", displayLtfsPath(path));

        void goto(`${nextUrl.pathname}${nextUrl.search}${nextUrl.hash}`, {
            keepFocus: true,
            noScroll: true,
            replaceState: true,
        });
    }

    function resetSchemaState(): void {
        if (
            paths.length === 0 &&
            schemaRows.length === 0 &&
            schemaDevice === null &&
            requestedTreeDevice === null &&
            loadedTreeDevice === null &&
            failedTreeDevice === null &&
            loadedSelectionKey === null
        ) {
            return;
        }

        paths = [];
        pathsVersion += 1;
        schemaFilePaths = new Set();
        schemaDirectoryPaths = new Set([""]);
        schemaRows = [];
        schemaDevice = null;
        requestedTreeDevice = null;
        loadedTreeDevice = null;
        failedTreeDevice = null;
        loadedSelectionKey = null;
    }

    async function loadTree(deviceHash: string): Promise<void> {
        const requestId = ++treeLoadRequest;
        requestedTreeDevice = deviceHash;
        failedTreeDevice = null;
        isLoadingSchema = true;

        try {
            await startKokoHub();
            const result = await getTapeSchemaFiles(deviceHash);
            if (requestId !== treeLoadRequest) {
                return;
            }

            const nextPaths = result.items.map((file) => file.path);

            paths = nextPaths;
            pathsVersion += 1;
            schemaFilePaths = new Set(nextPaths);
            schemaDirectoryPaths = getDirectoryPaths(nextPaths);
            schemaDevice = deviceHash;
            loadedTreeDevice = deviceHash;
            loadedSelectionKey = null;
        } catch (error) {
            if (requestId !== treeLoadRequest) {
                return;
            }

            paths = [];
            pathsVersion += 1;
            schemaFilePaths = new Set();
            schemaDirectoryPaths = new Set([""]);
            schemaDevice = null;
            schemaRows = [];
            loadedTreeDevice = null;
            failedTreeDevice = deviceHash;
            loadedSelectionKey = null;
            notifyException(error, "Failed to load LTFS schema");
        } finally {
            if (requestId === treeLoadRequest) {
                requestedTreeDevice = null;
                isLoadingSchema = false;
            }
        }
    }

    async function loadSelection(
        deviceHash: string,
        path: string,
    ): Promise<void> {
        if (schemaDevice !== deviceHash) {
            return;
        }

        const requestId = ++selectionLoadRequest;
        const selectionKey = `${deviceHash}:${path}`;
        isLoadingSchema = true;

        try {
            await startKokoHub();

            if (path && schemaFilePaths.has(path)) {
                const file = await getTapeSchemaFile(deviceHash, path);
                if (requestId !== selectionLoadRequest) {
                    return;
                }

                schemaRows = file ? [file] : [];
                loadedSelectionKey = selectionKey;
                return;
            }

            const directoryPath = schemaDirectoryPaths.has(path) ? path : "";
            const result = await getTapeSchemaDirectoryFiles(
                deviceHash,
                directoryPath || null,
            );
            if (requestId !== selectionLoadRequest) {
                return;
            }

            schemaRows = result.items;
            loadedSelectionKey = selectionKey;
        } catch (error) {
            if (requestId !== selectionLoadRequest) {
                return;
            }

            schemaRows = [];
            loadedSelectionKey = null;
            notifyException(error, "Failed to load LTFS files");
        } finally {
            if (requestId === selectionLoadRequest) {
                isLoadingSchema = false;
            }
        }
    }

    function clampLeftWidth(width: number): number {
        const maxLeftWidth =
            splitPane.getBoundingClientRect().width -
            minRightWidth -
            handleWidth;

        return Math.min(Math.max(width, minLeftWidth), maxLeftWidth);
    }

    function resizeLeftPane(event: PointerEvent): void {
        if (!splitPane) {
            return;
        }

        const bounds = splitPane.getBoundingClientRect();
        leftWidth = clampLeftWidth(event.clientX - bounds.left);
    }

    function startColumnResize(event: PointerEvent): void {
        event.preventDefault();

        const target = event.currentTarget;
        if (target instanceof HTMLElement) {
            target.setPointerCapture(event.pointerId);
        }

        isResizing = true;
        resizeLeftPane(event);
    }

    function continueColumnResize(event: PointerEvent): void {
        const target = event.currentTarget;

        if (
            target instanceof HTMLElement &&
            target.hasPointerCapture(event.pointerId)
        ) {
            resizeLeftPane(event);
        }
    }

    function stopColumnResize(event: PointerEvent): void {
        const target = event.currentTarget;

        if (
            target instanceof HTMLElement &&
            target.hasPointerCapture(event.pointerId)
        ) {
            target.releasePointerCapture(event.pointerId);
        }

        isResizing = false;
    }

    function resetColumnResize(): void {
        leftWidth = null;
    }

    function getChartTheme(): ChartTheme {
        if (typeof document === "undefined") {
            return ChartTheme.WHITE;
        }

        return document.documentElement.getAttribute("theme") === "g100"
            ? ChartTheme.G100
            : ChartTheme.WHITE;
    }

    function createInitialSpeedData(): ChartTabularData {
        const now = Date.now();

        return Array.from({ length: speedPointCount }, (_, index) =>
            createSpeedPoint(
                now - (speedPointCount - index - 1) * 1_000,
                index,
            ),
        );
    }

    function createSpeedPoint(timestamp: number, index: number) {
        const base = 142 + Math.sin(index / 2) * 18;
        const variance = Math.cos(index / 3) * 7;

        return {
            group: "Speed",
            time: new Date(timestamp),
            value: Math.max(0, Math.round((base + variance) * 10) / 10),
        };
    }

    function createSpeedChartOptions(theme: ChartTheme): LineChartOptions {
        return {
            height: "100%",
            resizable: true,
            theme,
            axes: {
                left: {
                    mapsTo: "value",
                    title: "MB/s",
                },
                bottom: {
                    mapsTo: "time",
                    scaleType: ScaleTypes.TIME,
                },
            },
            data: {
                groupMapsTo: "group",
            },
            legend: {
                enabled: false,
            },
            points: {
                enabled: false,
            },
            toolbar: {
                enabled: false,
            },
        };
    }

    onMount(() => {
        let speedIndex = speedPointCount;
        chartTheme = getChartTheme();

        const themeObserver = new MutationObserver(() => {
            chartTheme = getChartTheme();
        });

        themeObserver.observe(document.documentElement, {
            attributes: true,
            attributeFilter: ["theme"],
        });

        const speedInterval = window.setInterval(() => {
            speedData = [
                ...speedData.slice(1),
                createSpeedPoint(Date.now(), speedIndex),
            ];
            speedIndex += 1;
        }, 1_000);

        return () => {
            themeObserver.disconnect();
            window.clearInterval(speedInterval);
        };
    });

    $effect(() => {
        const nextTreeSelectedPath = toTreeSelectedPath(urlPath);

        untrack(() => {
            if (treeSelectedPath !== nextTreeSelectedPath) {
                treeSelectedPath = nextTreeSelectedPath;
            }
        });
    });

    $effect(() => {
        const nextPath = toLtfsPathFromTreePath(treeSelectedPath);
        if (nextPath !== urlPath) {
            updatePathParam(nextPath);
        }
    });

    $effect(() => {
        if (mode !== "readonly") {
            resetSchemaState();
            return;
        }

        if (!device) {
            resetSchemaState();

            if (lastMissingDeviceNotice !== page.url.href) {
                notifyError(
                    "Missing LTFS device",
                    "Readonly schema browsing requires a device query parameter.",
                );
                lastMissingDeviceNotice = page.url.href;
            }
            return;
        }

        if (loadedTreeDevice === device || requestedTreeDevice === device) {
            return;
        }

        if (failedTreeDevice === device) {
            return;
        }

        void loadTree(device);
    });

    $effect(() => {
        if (mode !== "readonly" || !device || schemaDevice !== device) {
            return;
        }

        const selectionKey = `${device}:${urlPath}`;
        if (loadedSelectionKey === selectionKey) {
            return;
        }

        void loadSelection(device, urlPath);
    });

    $effect(() => {
        console.log(treeSelectedPath);
    });
</script>

<div class="ltfs-workspace">
    <div
        class="split-pane"
        style:grid-template-columns={splitColumns}
        bind:this={splitPane}>
        <div class="left-pane">
            <div class="panel table-panel" bind:clientHeight={tablePanelHeight}>
                <DataTable
                    sortable
                    virtualize={tableVirtualize}
                    headers={tableHeaders}
                    rows={tableRows}
                    size="short">
                    <Toolbar>
                        <ToolbarContent>
                            <ToolbarSearch
                                persistent
                                placeholder={isLoadingSchema
                                    ? "Loading files..."
                                    : "Search files..."}
                                bind:value={tableSearch} />
                        </ToolbarContent>
                    </Toolbar>
                </DataTable>
            </div>

            <section class="panel chart-panel">
                <div class="speed-readout">
                    <span>Current speed</span>
                    <strong>{currentSpeed} MB/s</strong>
                </div>
                <div class="chart-host">
                    <LineChart data={speedData} options={speedChartOptions} />
                </div>
            </section>
        </div>

        <button
            class="split-handle"
            class:resizing={isResizing}
            type="button"
            aria-label="Resize file tree column"
            onpointerdown={startColumnResize}
            onpointermove={continueColumnResize}
            onpointerup={stopColumnResize}
            onlostpointercapture={() => {
                isResizing = false;
            }}
            ondblclick={resetColumnResize}></button>

        <section class="panel file-tree-panel">
            <FileTree
                bind:selectedPath={treeSelectedPath}
                {paths}
                {pathsVersion}></FileTree>
        </section>
    </div>
</div>

<style>
    :global(.bx--content:has(.ltfs-workspace)) {
        overflow: hidden;
        padding: 0;
    }

    .ltfs-workspace {
        height: calc(100dvh - 3rem);
        min-height: 0;
        overflow: hidden;
        background: var(--cds-background);
    }

    .split-pane {
        display: grid;
        height: 100%;
        min-height: 0;
        overflow: hidden;
    }

    .left-pane {
        display: grid;
        grid-template-rows: minmax(0, 7fr) minmax(0, 3fr);
        min-width: 0;
        min-height: 0;
        overflow: hidden;
    }

    :global(.panel) {
        min-width: 0;
        min-height: 0;
        padding: 0;
        border-radius: 0;
    }

    .table-panel {
        display: flex;
        flex-direction: column;
        overflow: hidden;
        border-block-end: 1px solid var(--cds-border-subtle);
        overscroll-behavior: contain;
    }

    .table-panel :global(.bx--data-table-container) {
        display: flex;
        flex: 1 1 auto;
        flex-direction: column;
        min-width: 0;
        min-height: 0;
        overflow: hidden;
    }

    .table-panel :global(.bx--toolbar) {
        flex: 0 0 auto;
        min-height: 3rem;
        border-block-start: 0;
    }

    .table-panel :global(.bx--data-table-container > div:last-child) {
        flex: 1 1 auto;
        min-width: 0;
        min-height: 0;
        max-width: 100%;
        overflow-x: auto !important;
        overflow-y: auto !important;
        overscroll-behavior: contain;
    }

    .table-panel :global(.bx--data-table) {
        width: 100%;
        min-width: 48rem;
    }

    .table-panel :global(.bx--data-table th:first-child),
    .table-panel :global(.bx--data-table td:first-child) {
        min-width: 20rem;
    }

    .chart-panel {
        position: relative;
        overflow: hidden;
    }

    .speed-readout {
        position: absolute;
        z-index: 1;
        top: 0.5rem;
        right: 1rem;
        display: flex;
        gap: 0.5rem;
        align-items: baseline;
        color: var(--cds-text-secondary);
        font-size: 0.75rem;
        line-height: 1rem;
        pointer-events: none;
    }

    .speed-readout strong {
        color: var(--cds-text-primary);
        font-size: 0.875rem;
        font-weight: 600;
    }

    .chart-host {
        height: 100%;
        min-height: 0;
        overflow: hidden;
        padding: 1rem 1rem 0.25rem;
    }

    .chart-host :global(.cds--chart-holder) {
        height: 100% !important;
        min-height: 0;
        max-height: 100%;
        overflow: hidden;
    }

    .split-handle {
        position: relative;
        width: 100%;
        min-width: 0;
        padding: 0;
        border: 0;
        background: linear-gradient(
                90deg,
                transparent 0,
                transparent 5px,
                var(--cds-border-subtle) 5px,
                var(--cds-border-subtle) 7px,
                transparent 7px
            ),
            var(--cds-background);
        cursor: col-resize;
        touch-action: none;
    }

    .split-handle::before {
        position: absolute;
        inset-block: 0;
        inset-inline: 4px;
        background: transparent;
        content: "";
    }

    .split-handle::after {
        position: absolute;
        top: 50%;
        left: 50%;
        width: 2px;
        height: 3rem;
        background: var(--cds-icon-secondary);
        content: "";
        opacity: 0;
        transform: translate(-50%, -50%);
        transition: opacity 110ms cubic-bezier(0.2, 0, 0.38, 0.9);
    }

    .split-handle:hover,
    .split-handle:focus-visible,
    .split-handle.resizing {
        background: var(--cds-background-hover);
        outline: 2px solid var(--cds-focus);
        outline-offset: -2px;
    }

    .split-handle:hover::after,
    .split-handle:focus-visible::after,
    .split-handle.resizing::after {
        opacity: 1;
    }

    .file-tree-panel {
        overflow: hidden;
    }
</style>
