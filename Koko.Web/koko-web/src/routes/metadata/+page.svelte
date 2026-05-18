<script lang="ts">
    import { onMount } from "svelte";
    import {
        Button,
        DataTable,
        ExpandableTile,
        Pagination,
        ProgressBar,
        Search,
        Toggle,
    } from "carbon-components-svelte";
    import {
        getTapeMetadataArchivesByBarcode,
        getTapeMetadataBarcodeGroups,
        getTapeMetadataOverview,
        pruneTapeMetadata,
        reindexTapeMetadata,
        startKokoHub,
        type TapeMetadataArchive,
        type TapeMetadataBarcodeGroup,
        type TapeMetadataOverview,
    } from "$lib/api/koko-hub";
    import { notifyException } from "$lib/notifications";
    import { bytes } from "ts-humanize";
    import type { DataTableHeader } from "carbon-components-svelte/src/DataTable/DataTable.svelte";

    const archivePageSize = 5;

    type ArchiveRow = {
        id: string;
        archive: TapeMetadataArchive;
        schema: string;
        generation: string;
        status: string;
        files: string;
        logical: string;
        used: string;
        available: string;
        total: string;
        indexed: string;
    };

    type BarcodeGroupCard = TapeMetadataBarcodeGroup & {
        expanded: boolean;
    };

    type ArchivePageState = {
        rows: ArchiveRow[];
        page: number;
        totalCount: number;
        loading: boolean;
    };

    const archiveHeaders = [
        { key: "schema", value: "", sort: false, width: "7rem" },
        { key: "generation", value: "Generation" },
        { key: "status", value: "Status" },
        { key: "files", value: "Files" },
        { key: "logical", value: "Logical" },
        { key: "used", value: "Used" },
        { key: "available", value: "Available" },
        { key: "total", value: "Total" },
        { key: "indexed", value: "Indexed" },
    ] satisfies readonly DataTableHeader<ArchiveRow>[];

    let overview = $state<TapeMetadataOverview | null>(null);
    let groupCards = $state<BarcodeGroupCard[]>([]);
    let archivePages = $state<Record<string, ArchivePageState>>({});
    let search = $state("");
    let includeMissing = $state(true);
    let busy = $state(false);

    function capacityPercent(item: TapeMetadataArchive): number {
        if (!item.totalBytes || item.totalBytes <= 0 || !item.usedBytes)
            return 0;
        return Math.min(100, Math.max(0, (item.usedBytes / item.totalBytes) * 100));
    }

    function formatBytes(value?: number | null): string {
        if (value == null) return "";
        return bytes(Number(value));
    }

    function statusText(item: TapeMetadataArchive): string {
        return item.missing ? "Missing" : item.status;
    }

    function formatDate(value?: string | null): string {
        if (!value) return "";
        return new Date(value).toLocaleString();
    }

    function schemaHref(item: TapeMetadataArchive): string {
        const device = encodeURIComponent(item.archiveXxHash128);
        return `/ltfs?type=readonly&device=${device}&path=/`;
    }

    function toArchiveRow(item: TapeMetadataArchive): ArchiveRow {
        return {
            id: item.archiveXxHash128,
            archive: item,
            schema: "Schema",
            generation: item.generationNumber == null ? "-" : String(item.generationNumber),
            status: statusText(item),
            files: item.fileCount == null ? "" : String(item.fileCount),
            logical: formatBytes(item.logicalBytes),
            used: formatBytes(item.usedBytes),
            available: formatBytes(item.availableBytes),
            total: formatBytes(item.totalBytes),
            indexed: formatDate(item.indexedAtUtc),
        };
    }

    function getArchivePage(barcode: string): ArchivePageState {
        return (
            archivePages[barcode] ?? {
                rows: [],
                page: 1,
                totalCount: 0,
                loading: false,
            }
        );
    }

    function setArchivePage(barcode: string, state: ArchivePageState): void {
        archivePages = {
            ...archivePages,
            [barcode]: state,
        };
    }

    function setGroupExpanded(barcode: string, expanded: boolean): void {
        groupCards = groupCards.map((group) =>
            group.barcode === barcode ? { ...group, expanded } : group,
        );
    }

    function toggleGroup(group: BarcodeGroupCard): void {
        setGroupExpanded(group.barcode, !group.expanded);
    }

    function handleSummaryKeydown(
        event: KeyboardEvent,
        group: BarcodeGroupCard,
    ): void {
        if (event.key !== "Enter" && event.key !== " ") {
            return;
        }

        event.preventDefault();
        toggleGroup(group);
    }

    async function loadArchivePage(barcode: string, page = 1): Promise<void> {
        const current = getArchivePage(barcode);
        if (current.loading) {
            return;
        }

        setArchivePage(barcode, {
            ...current,
            page,
            loading: true,
        });

        try {
            await startKokoHub();
            const result = await getTapeMetadataArchivesByBarcode(barcode, {
                search,
                includeMissing,
                skip: (page - 1) * archivePageSize,
                take: archivePageSize,
            });

            setArchivePage(barcode, {
                rows: result.items.map(toArchiveRow),
                page,
                totalCount: result.totalCount,
                loading: false,
            });
        } catch (error) {
            setArchivePage(barcode, {
                ...current,
                page,
                loading: false,
            });
            notifyException(error, `Failed to load archives for ${barcode}`);
        }
    }

    async function refresh(): Promise<void> {
        busy = true;
        try {
            await startKokoHub();
            overview = await getTapeMetadataOverview();
            const result = await getTapeMetadataBarcodeGroups({
                search,
                includeMissing,
            });
            groupCards = result.items.map((group) => ({
                ...group,
                expanded: false,
            }));
            archivePages = {};
        } catch (error) {
            notifyException(error, "Failed to load tape metadata");
        } finally {
            busy = false;
        }
    }

    async function reindex(): Promise<void> {
        busy = true;
        try {
            await startKokoHub();
            await reindexTapeMetadata();
        } catch (error) {
            notifyException(error, "Failed to reindex tape metadata");
        } finally {
            busy = false;
        }
    }

    async function prune(): Promise<void> {
        busy = true;
        try {
            await startKokoHub();
            await pruneTapeMetadata();
            await refresh();
        } catch (error) {
            notifyException(error, "Failed to prune tape metadata");
        } finally {
            busy = false;
        }
    }

    $effect(() => {
        for (const group of groupCards) {
            const state = archivePages[group.barcode];
            if (group.expanded && !state) {
                void loadArchivePage(group.barcode);
            }
        }
    });

    onMount(() => {
        void refresh();
    });
</script>

<div class="metadata-page">
    <section class="summary-band">
        <div>
            <span>Tapes</span>
            <strong>{overview?.tapeCount ?? 0}</strong>
        </div>
        <div>
            <span>Archives</span>
            <strong>{overview?.archiveCount ?? 0}</strong>
        </div>
        <div>
            <span>Missing</span>
            <strong>{overview?.missingCount ?? 0}</strong>
        </div>
        <div>
            <span>Last indexed</span>
            <strong>{formatDate(overview?.lastIndexedAtUtc)}</strong>
        </div>
    </section>

    <section class="search-panel">
        <Search
            size="lg"
            labelText="Search metadata"
            placeholder="Search barcode or archive..."
            bind:value={search}
            on:change={refresh}
            on:clear={refresh}
        />
        <Toggle
            labelText="Missing"
            hideLabel
            labelA="Hide missing"
            labelB="Show missing"
            bind:toggled={includeMissing}
            on:change={refresh}
        />
        <div class="search-actions">
            <Button size="small" kind="secondary" disabled={busy} onclick={refresh}>
                Refresh
            </Button>
            <Button size="small" kind="tertiary" disabled={busy} onclick={reindex}>
                Reindex
            </Button>
            <Button size="small" kind="danger-tertiary" disabled={busy} onclick={prune}>
                Prune
            </Button>
        </div>
    </section>

    <section class="barcode-list" aria-label="Tape barcode groups">
        {#if groupCards.length === 0}
            <p class="empty">No barcode groups found.</p>
        {:else}
            {#each groupCards as group (group.barcode)}
                {@const latest = group.latestArchive}
                {@const page = getArchivePage(group.barcode)}
                <ExpandableTile
                    bind:expanded={group.expanded}
                    hasInteractiveContent
                    tileCollapsedIconText={`Expand ${group.barcode}`}
                    tileExpandedIconText={`Collapse ${group.barcode}`}
                >
                    <div
                        slot="above"
                        class="tile-summary"
                        role="button"
                        tabindex="0"
                        aria-expanded={group.expanded}
                        onclick={() => toggleGroup(group)}
                        onkeydown={(event) => handleSummaryKeydown(event, group)}
                    >
                        <div class="barcode-cell">
                            <strong>{group.barcode}</strong>
                            <span>{group.archiveCount} archives</span>
                        </div>
                        <div class="latest-cell">
                            <span>Latest generation</span>
                            <strong>{latest.generationNumber ?? "-"}</strong>
                        </div>
                        <div class="status-cell">
                            <span>Status</span>
                            <strong>{statusText(latest)}</strong>
                        </div>
                        <div class="capacity-cell">
                            <ProgressBar
                                class="capacity-progress"
                                value={capacityPercent(latest)}
                                max={100}
                                size="sm"
                                labelText={`${Math.round(capacityPercent(latest))}% used`}
                                helperText={`${formatBytes(latest.usedBytes)} / ${formatBytes(latest.totalBytes)}`}
                            />
                        </div>
                    </div>

                    <div slot="below" class="archive-table">
                        {#if page.loading}
                            <p class="loading">Loading archives...</p>
                        {:else}
                            <DataTable
                                sortable
                                size="short"
                                headers={archiveHeaders}
                                rows={page.rows}
                            >
                                <svelte:fragment slot="cell" let:row let:cell>
                                    {#if cell.key === "schema"}
                                        <Button
                                            size="small"
                                            kind="ghost"
                                            href={schemaHref(row.archive)}
                                            disabled={row.archive.missing || row.archive.status === "Failed"}
                                        >
                                            Schema
                                        </Button>
                                    {:else}
                                        {cell.value}
                                    {/if}
                                </svelte:fragment>
                            </DataTable>
                            <Pagination
                                page={page.page}
                                pageSize={archivePageSize}
                                pageSizes={[archivePageSize]}
                                totalItems={page.totalCount}
                                pageSizeInputDisabled
                                on:change={(event) => {
                                    void loadArchivePage(
                                        group.barcode,
                                        event.detail.page ?? page.page,
                                    );
                                }}
                            />
                        {/if}
                    </div>
                </ExpandableTile>
            {/each}
        {/if}
    </section>
</div>

<style>
    :global(.bx--content:has(.metadata-page)) {
        min-height: calc(100dvh - 3rem);
        padding: 1rem;
        background: var(--cds-background);
    }

    .metadata-page {
        display: grid;
        gap: 1rem;
        min-width: 0;
    }

    .summary-band {
        display: grid;
        grid-template-columns: repeat(4, minmax(0, 1fr));
        gap: 1px;
        border: 1px solid var(--cds-border-subtle);
        background: var(--cds-border-subtle);
    }

    .summary-band > div {
        display: grid;
        gap: 0.25rem;
        min-width: 0;
        padding: 1rem;
        background: var(--cds-layer);
    }

    .summary-band span,
    .barcode-cell span,
    .latest-cell span,
    .status-cell span,
    .empty,
    .loading {
        color: var(--cds-text-secondary);
        font-size: 0.75rem;
        line-height: 1rem;
    }

    .summary-band strong {
        min-width: 0;
        overflow-wrap: anywhere;
        font-size: 1rem;
        font-weight: 600;
        line-height: 1.5rem;
    }

    .search-panel {
        display: grid;
        grid-template-columns: minmax(18rem, 1fr) auto auto;
        gap: 1px;
        align-items: center;
        border: 1px solid var(--cds-border-subtle);
        background: var(--cds-border-subtle);
    }

    .search-panel > :global(.bx--search),
    .search-panel > :global(.bx--form-item),
    .search-actions {
        min-width: 0;
        background: var(--cds-layer);
    }

    .search-actions {
        display: flex;
        flex-wrap: wrap;
        gap: 0.5rem;
        justify-content: flex-end;
        padding: 0.5rem;
    }

    .barcode-list {
        display: grid;
        gap: 0.75rem;
        min-width: 0;
    }

    .empty,
    .loading {
        margin: 0;
        padding: 1rem;
        border: 1px solid var(--cds-border-subtle);
        background: var(--cds-layer);
    }

    .barcode-list :global(.bx--tile) {
        inline-size: 100%;
        border: 1px solid var(--cds-border-subtle);
        background: var(--cds-layer);
        text-align: start;
    }

    .barcode-list :global(.bx--tile__chevron) {
        position: relative;
        z-index: 2;
        background: var(--cds-layer);
    }

    .tile-summary {
        display: grid;
        grid-template-columns:
            minmax(12rem, 1.2fr)
            minmax(8rem, 0.6fr)
            minmax(8rem, 0.6fr)
            minmax(18rem, 1.8fr);
        gap: 1rem;
        align-items: center;
        min-width: 0;
        cursor: pointer;
    }

    .tile-summary:hover {
        background: var(--cds-layer-hover);
    }

    .tile-summary:focus-visible {
        outline: 2px solid var(--cds-focus);
        outline-offset: 2px;
    }

    .barcode-cell,
    .latest-cell,
    .status-cell {
        display: grid;
        gap: 0.125rem;
        min-width: 0;
    }

    .barcode-cell strong,
    .latest-cell strong,
    .status-cell strong {
        min-width: 0;
        overflow-wrap: anywhere;
        font-size: 0.875rem;
        font-weight: 600;
        line-height: 1.25rem;
    }

    .capacity-cell {
        min-width: 0;
    }

    .capacity-cell :global(.capacity-progress) {
        inline-size: 100%;
        max-inline-size: none;
    }

    .capacity-cell :global(.capacity-progress .bx--progress-bar__track) {
        background-color: var(--cds-background);
    }

    .archive-table {
        display: grid;
        min-width: 0;
        padding-block-start: 2rem;
    }

    .archive-table :global(.bx--data-table-container) {
        min-width: 0;
        overflow-x: auto;
    }

    .archive-table :global(.bx--data-table) {
        min-width: 66rem;
    }

    .archive-table :global(.bx--pagination) {
        border-block-start: 0;
    }

    @media (max-width: 1000px) {
        .tile-summary {
            grid-template-columns: repeat(2, minmax(0, 1fr));
        }
    }

    @media (max-width: 760px) {
        .summary-band,
        .search-panel,
        .tile-summary {
            grid-template-columns: 1fr;
        }

        .search-actions {
            justify-content: flex-start;
        }
    }
</style>
