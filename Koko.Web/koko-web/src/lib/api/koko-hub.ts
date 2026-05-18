import { browser } from "$app/environment";
import * as signalR from "@microsoft/signalr";
import { get, writable } from "svelte/store";
import { notifyRealtimeEvent } from "$lib/notifications";

const kokoHubUrl = normalizeKokoHubUrl(import.meta.env.VITE_PUBLIC_KOKO_HUB_URL);

export interface KokoServerInfo {
    appName: string;
    version: string;
    environment: string;
    startedAtUtc: string;
}

export interface KokoPingRequest {
    clientTimestampUtc?: string;
}

export interface KokoPingResponse {
    clientTimestampUtc?: string;
    serverTimestampUtc: string;
    connectionId?: string | null;
}

export interface KokoRealtimeEvent {
    id: string;
    type: string;
    severity: string;
    message: string;
    timestampUtc: string;
    operationId?: string | null;
    progress?: number | null;
}

export interface TapeMetadataOverview {
    tapeCount: number;
    archiveCount: number;
    missingCount: number;
    lastIndexedAtUtc?: string | null;
}

export interface TapeMetadataQuery {
    search?: string | null;
    barcode?: string | null;
    includeMissing?: boolean;
    skip?: number;
    take?: number;
}

export interface TapeMetadataBarcodeGroupQuery {
    search?: string | null;
    includeMissing?: boolean;
}

export interface TapeMetadataBarcodeGroup {
    barcode: string;
    archiveCount: number;
    latestArchive: TapeMetadataArchive;
}

export interface TapeMetadataBarcodeGroupResult {
    totalCount: number;
    items: TapeMetadataBarcodeGroup[];
}

export interface TapeMetadataArchive {
    archiveXxHash128: string;
    barcode: string;
    archivePath: string;
    relativePath: string;
    archiveName: string;
    archiveSizeBytes: number;
    archiveLastWriteTimeUtc: string;
    indexedAtUtc: string;
    missing: boolean;
    status: string;
    error?: string | null;
    volumeUuid?: string | null;
    generationNumber?: number | null;
    ltfsUpdateTime?: string | null;
    locationPartition?: string | null;
    locationStartBlock?: number | null;
    fileCount?: number | null;
    directoryCount?: number | null;
    logicalBytes?: number | null;
    totalBytes?: number | null;
    usedBytes?: number | null;
    availableBytes?: number | null;
}

export interface TapeMetadataQueryResult {
    totalCount: number;
    items: TapeMetadataArchive[];
}

export interface TapeMetadataPruneResult {
    deletedRecords: number;
}

export interface TapeSchemaFileList {
    archiveXxHash128: string;
    volumeUuid: string;
    generationNumber: number;
    totalCount: number;
    items: TapeSchemaFile[];
}

export interface TapeSchemaFile {
    path: string;
    directoryPath: string;
    name: string;
    length: number;
    readOnly: boolean;
    openForWrite: boolean;
    creationTime: string;
    changeTime: string;
    modifyTime: string;
    accessTime: string;
    backupTime: string;
    fileUid: number;
    symlink?: string | null;
    extendedAttributes: TapeSchemaExtendedAttribute[];
    extents: TapeSchemaExtent[];
}

export interface TapeSchemaExtendedAttribute {
    key: string;
    value: string;
}

export interface TapeSchemaExtent {
    fileOffset: number;
    partition: string;
    startBlock: number;
    byteOffset: number;
    byteCount: number;
}

let connection: signalR.HubConnection | null = null;

export const kokoHubState = writable<signalR.HubConnectionState>(
    signalR.HubConnectionState.Disconnected,
);
export const kokoServerInfo = writable<KokoServerInfo | null>(null);
export const kokoEvents = writable<KokoRealtimeEvent[]>([]);

function updateState(hub: signalR.HubConnection): void {
    kokoHubState.set(hub.state);
}

function normalizeKokoHubUrl(value?: string): string {
    const trimmed = value?.trim();
    if (!trimmed) return "/hubs/koko";

    try {
        const url = new URL(trimmed);
        if (!url.pathname || url.pathname === "/") {
            url.pathname = "/hubs/koko";
        }

        return url.toString();
    } catch {
        return trimmed === "/" ? "/hubs/koko" : trimmed;
    }
}

export function getKokoHubConnection(): signalR.HubConnection {
    if (!browser) {
        throw new Error("Koko SignalR hub is only available in the browser.");
    }

    if (connection) {
        return connection;
    }

    const hub = new signalR.HubConnectionBuilder()
        .withUrl(kokoHubUrl)
        .withAutomaticReconnect()
        .configureLogging(signalR.LogLevel.Information)
        .build();

    hub.on("ReceiveEvent", (event: KokoRealtimeEvent) => {
        kokoEvents.update((events) => [event, ...events].slice(0, 50));
        notifyRealtimeEvent(event);
    });

    hub.onreconnecting(() => updateState(hub));
    hub.onreconnected(() => updateState(hub));
    hub.onclose(() => updateState(hub));

    connection = hub;
    updateState(hub);
    return hub;
}

export async function startKokoHub(): Promise<void> {
    const hub = getKokoHubConnection();

    if (hub.state !== signalR.HubConnectionState.Disconnected) {
        updateState(hub);
        return;
    }

    await hub.start();
    updateState(hub);
}

export async function stopKokoHub(): Promise<void> {
    const hub = getKokoHubConnection();

    if (hub.state === signalR.HubConnectionState.Disconnected) {
        updateState(hub);
        return;
    }

    await hub.stop();
    updateState(hub);
}

export async function getServerInfo(): Promise<KokoServerInfo> {
    const info = await getKokoHubConnection().invoke<KokoServerInfo>("GetServerInfo");
    kokoServerInfo.set(info);
    return info;
}

export async function pingKokoHub(): Promise<KokoPingResponse> {
    return getKokoHubConnection().invoke<KokoPingResponse>("Ping", {
        clientTimestampUtc: new Date().toISOString(),
    } satisfies KokoPingRequest);
}

export async function getTapeMetadataOverview(): Promise<TapeMetadataOverview> {
    return getKokoHubConnection().invoke<TapeMetadataOverview>("GetTapeMetadataOverview");
}

export async function queryTapeMetadata(query: TapeMetadataQuery = {}): Promise<TapeMetadataQueryResult> {
    return getKokoHubConnection().invoke<TapeMetadataQueryResult>("QueryTapeMetadata", query);
}

export async function getTapeMetadataBarcodeGroups(
    query: TapeMetadataBarcodeGroupQuery = {},
): Promise<TapeMetadataBarcodeGroupResult> {
    return getKokoHubConnection().invoke<TapeMetadataBarcodeGroupResult>(
        "GetTapeMetadataBarcodeGroups",
        query,
    );
}

export async function getTapeMetadataArchivesByBarcode(
    barcode: string,
    query: TapeMetadataQuery = {},
): Promise<TapeMetadataQueryResult> {
    return getKokoHubConnection().invoke<TapeMetadataQueryResult>(
        "GetTapeMetadataArchivesByBarcode",
        barcode,
        query,
    );
}

export async function reindexTapeMetadata(): Promise<void> {
    await getKokoHubConnection().invoke("ReindexTapeMetadata");
}

export async function pruneTapeMetadata(): Promise<TapeMetadataPruneResult> {
    return getKokoHubConnection().invoke<TapeMetadataPruneResult>("PruneTapeMetadata");
}

export async function getTapeSchemaFiles(archiveXxHash128: string): Promise<TapeSchemaFileList> {
    return getKokoHubConnection().invoke<TapeSchemaFileList>("GetTapeSchemaFiles", archiveXxHash128);
}

export async function getTapeSchemaDirectoryFiles(
    archiveXxHash128: string,
    directoryPath: string | null = null,
): Promise<TapeSchemaFileList> {
    return getKokoHubConnection().invoke<TapeSchemaFileList>(
        "GetTapeSchemaDirectoryFiles",
        archiveXxHash128,
        directoryPath,
    );
}

export async function getTapeSchemaFile(
    archiveXxHash128: string,
    filePath: string,
): Promise<TapeSchemaFile | null> {
    return getKokoHubConnection().invoke<TapeSchemaFile | null>(
        "GetTapeSchemaFile",
        archiveXxHash128,
        filePath,
    );
}

export function isKokoHubConnected(): boolean {
    return get(kokoHubState) === signalR.HubConnectionState.Connected;
}
