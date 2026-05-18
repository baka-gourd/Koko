<script lang="ts">
    import { Button } from "carbon-components-svelte";
    import {
        addLtfsXattrColumn,
        getLtfsTableColumns,
        ltfsTableSettings,
        moveLtfsColumn,
        removeLtfsColumn,
        resetLtfsTableSettings,
        type LtfsColumnId,
    } from "$lib/ltfs-table-settings";

    let newXattrKey = $state("");
    let columns = $derived(getLtfsTableColumns($ltfsTableSettings));

    function addColumn(): void {
        const next = addLtfsXattrColumn($ltfsTableSettings, newXattrKey);
        ltfsTableSettings.set(next);
        newXattrKey = "";
    }

    function moveColumn(id: LtfsColumnId, direction: -1 | 1): void {
        ltfsTableSettings.set(moveLtfsColumn($ltfsTableSettings, id, direction));
    }

    function removeColumn(id: LtfsColumnId): void {
        ltfsTableSettings.set(removeLtfsColumn($ltfsTableSettings, id));
    }

    function resetColumns(): void {
        ltfsTableSettings.set(resetLtfsTableSettings());
        newXattrKey = "";
    }
</script>

<div class="settings-page">
    <section class="settings-panel">
        <div class="panel-header">
            <div>
                <p>LTFS</p>
                <h1>表格列</h1>
            </div>
            <Button size="small" kind="tertiary" onclick={resetColumns}>
                重置
            </Button>
        </div>

        <form
            class="add-column"
            onsubmit={(event) => {
                event.preventDefault();
                addColumn();
            }}
        >
            <label for="ltfs-xattr-key">扩展属性 key</label>
            <input
                id="ltfs-xattr-key"
                type="text"
                bind:value={newXattrKey}
                placeholder="ltfs.hash.sha1sum"
            />
            <Button size="small" type="submit" disabled={!newXattrKey.trim()}>
                添加列
            </Button>
        </form>

        <div class="column-list">
            {#each columns as column, index (column.id)}
                <article>
                    <div>
                        <strong>{column.label}</strong>
                        <span>{column.kind === "base" ? "基础列" : "扩展属性"}</span>
                    </div>
                    <div class="column-actions">
                        <Button
                            size="small"
                            kind="ghost"
                            disabled={index === 0}
                            onclick={() => moveColumn(column.id, -1)}
                        >
                            上移
                        </Button>
                        <Button
                            size="small"
                            kind="ghost"
                            disabled={index === columns.length - 1}
                            onclick={() => moveColumn(column.id, 1)}
                        >
                            下移
                        </Button>
                        {#if column.kind === "xattr"}
                            <Button
                                size="small"
                                kind="danger-ghost"
                                onclick={() => removeColumn(column.id)}
                            >
                                删除
                            </Button>
                        {/if}
                    </div>
                </article>
            {/each}
        </div>
    </section>
</div>

<style>
    :global(.bx--content:has(.settings-page)) {
        min-height: calc(100dvh - 3rem);
        padding: 1rem;
        background: var(--cds-background);
    }

    .settings-page {
        max-width: 56rem;
    }

    .settings-panel {
        border: 1px solid var(--cds-border-subtle);
        background: var(--cds-layer);
    }

    .panel-header {
        display: flex;
        gap: 1rem;
        align-items: flex-start;
        justify-content: space-between;
        padding: 1rem;
        border-block-end: 1px solid var(--cds-border-subtle);
    }

    h1,
    p {
        margin: 0;
    }

    h1 {
        font-size: 1.25rem;
        font-weight: 400;
        line-height: 1.75rem;
    }

    p,
    label,
    span {
        color: var(--cds-text-secondary);
        font-size: 0.75rem;
        line-height: 1rem;
    }

    .add-column {
        display: grid;
        grid-template-columns: minmax(10rem, 1fr) minmax(16rem, 2fr) auto;
        gap: 1px;
        align-items: end;
        padding: 1px;
        background: var(--cds-border-subtle);
    }

    .add-column > * {
        min-width: 0;
    }

    label {
        align-self: stretch;
        padding: 0.875rem 1rem;
        background: var(--cds-layer);
    }

    input {
        height: 2.5rem;
        min-width: 0;
        padding-inline: 1rem;
        border: 0;
        border-block-end: 1px solid var(--cds-border-strong);
        background: var(--cds-field);
        color: var(--cds-text-primary);
        font: inherit;
    }

    input:focus {
        outline: 2px solid var(--cds-focus);
        outline-offset: -2px;
    }

    .column-list {
        display: grid;
        gap: 1px;
        padding: 1px;
        background: var(--cds-border-subtle);
    }

    article {
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        gap: 1rem;
        align-items: center;
        min-width: 0;
        padding: 0.75rem 1rem;
        background: var(--cds-layer);
    }

    article > div:first-child {
        display: grid;
        gap: 0.125rem;
        min-width: 0;
    }

    strong {
        min-width: 0;
        overflow-wrap: anywhere;
        font-size: 0.875rem;
        font-weight: 600;
        line-height: 1.25rem;
    }

    .column-actions {
        display: flex;
        flex-wrap: wrap;
        gap: 0.25rem;
        justify-content: flex-end;
    }

    @media (max-width: 700px) {
        .add-column,
        article {
            grid-template-columns: 1fr;
        }

        .column-actions {
            justify-content: flex-start;
        }
    }
</style>
