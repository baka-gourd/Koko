<script lang="ts">
    import { onMount } from "svelte";
    import { Button, Column, Grid, Row } from "carbon-components-svelte";
    import {
        getServerInfo,
        kokoEvents,
        kokoHubState,
        kokoServerInfo,
        pingKokoHub,
        startKokoHub,
        type KokoPingResponse,
    } from "$lib/api/koko-hub";
    import { notifyException } from "$lib/notifications";

    let pingResponse = $state<KokoPingResponse | null>(null);
    let isBusy = $state(false);

    async function refreshConnection(): Promise<void> {
        isBusy = true;

        try {
            await startKokoHub();
            await getServerInfo();
            pingResponse = await pingKokoHub();
        } catch (error) {
            notifyException(error, "Failed to refresh runtime status");
        } finally {
            isBusy = false;
        }
    }

    onMount(() => {
        void refreshConnection();
    });
</script>

<div class="home-grid">
    <Grid>
        <Row>
            <Column>
            <section class="status-panel">
                <div class="status-header">
                    <div>
                        <p class="eyebrow">Koko.Web</p>
                        <h1>Runtime status</h1>
                    </div>
                    <span class:connected={$kokoHubState === "Connected"} class="state-pill">
                        {$kokoHubState}
                    </span>
                </div>

                <div class="status-grid">
                    <div>
                        <span>Application</span>
                        <strong>{$kokoServerInfo?.appName ?? "Unknown"}</strong>
                    </div>
                    <div>
                        <span>Version</span>
                        <strong>{$kokoServerInfo?.version ?? "Unknown"}</strong>
                    </div>
                    <div>
                        <span>Environment</span>
                        <strong>{$kokoServerInfo?.environment ?? "Unknown"}</strong>
                    </div>
                    <div>
                        <span>Connection ID</span>
                        <strong>{pingResponse?.connectionId ?? "Pending"}</strong>
                    </div>
                </div>

                <div class="actions">
                    <Button size="small" disabled={isBusy} onclick={refreshConnection}>
                        Refresh
                    </Button>
                    {#if pingResponse}
                        <span class="ping-time">Last ping: {new Date(pingResponse.serverTimestampUtc).toLocaleTimeString()}</span>
                    {/if}
                </div>
            </section>

            <section class="events-panel">
                <h2>Realtime events</h2>
                {#if $kokoEvents.length === 0}
                    <p class="empty">No events received.</p>
                {:else}
                    <ul>
                        {#each $kokoEvents as event (event.id)}
                            <li>
                                <span>{event.severity}</span>
                                <strong>{event.type}</strong>
                                <p>{event.message}</p>
                            </li>
                        {/each}
                    </ul>
                {/if}
            </section>
            </Column>
        </Row>
    </Grid>
</div>

<style>
    .home-grid {
        padding-block: 2rem;
    }

    .status-panel,
    .events-panel {
        border: 1px solid var(--cds-border-subtle);
        background: var(--cds-layer);
    }

    .status-panel {
        padding: 1.5rem;
    }

    .status-header {
        display: flex;
        gap: 1rem;
        align-items: flex-start;
        justify-content: space-between;
        margin-block-end: 1.5rem;
    }

    .eyebrow {
        margin: 0 0 0.25rem;
        color: var(--cds-text-secondary);
        font-size: 0.75rem;
        line-height: 1rem;
    }

    h1,
    h2,
    p {
        margin: 0;
    }

    h1 {
        font-size: 1.75rem;
        font-weight: 400;
        line-height: 2.25rem;
    }

    h2 {
        padding: 1rem;
        border-block-end: 1px solid var(--cds-border-subtle);
        font-size: 1rem;
        font-weight: 600;
        line-height: 1.5rem;
    }

    .state-pill {
        flex: 0 0 auto;
        padding: 0.25rem 0.5rem;
        background: var(--cds-layer-accent);
        color: var(--cds-text-secondary);
        font-size: 0.75rem;
        line-height: 1rem;
    }

    .state-pill.connected {
        background: var(--cds-support-success);
        color: var(--cds-text-on-color);
    }

    .status-grid {
        display: grid;
        grid-template-columns: repeat(4, minmax(0, 1fr));
        gap: 1px;
        overflow: hidden;
        background: var(--cds-border-subtle);
    }

    .status-grid > div {
        display: flex;
        min-width: 0;
        flex-direction: column;
        gap: 0.375rem;
        padding: 1rem;
        background: var(--cds-layer);
    }

    .status-grid span,
    .ping-time,
    .empty,
    li span {
        color: var(--cds-text-secondary);
        font-size: 0.75rem;
        line-height: 1rem;
    }

    .status-grid strong {
        min-width: 0;
        overflow-wrap: anywhere;
        font-size: 0.875rem;
        font-weight: 600;
        line-height: 1.25rem;
    }

    .actions {
        display: flex;
        gap: 1rem;
        align-items: center;
        margin-block-start: 1.5rem;
    }

    .events-panel {
        margin-block-start: 1rem;
    }

    .empty {
        padding: 1rem;
    }

    ul {
        display: grid;
        margin: 0;
        padding: 0;
        list-style: none;
    }

    li {
        display: grid;
        grid-template-columns: 5rem minmax(8rem, 12rem) minmax(0, 1fr);
        gap: 1rem;
        align-items: baseline;
        padding: 0.75rem 1rem;
        border-block-end: 1px solid var(--cds-border-subtle);
    }

    li:last-child {
        border-block-end: 0;
    }

    li strong,
    li p {
        min-width: 0;
        overflow-wrap: anywhere;
        font-size: 0.875rem;
        line-height: 1.25rem;
    }

    @media (max-width: 900px) {
        .status-grid {
            grid-template-columns: repeat(2, minmax(0, 1fr));
        }

        li {
            grid-template-columns: 1fr;
            gap: 0.25rem;
        }
    }

    @media (max-width: 600px) {
        .status-header,
        .actions {
            align-items: stretch;
            flex-direction: column;
        }

        .status-grid {
            grid-template-columns: 1fr;
        }
    }
</style>
