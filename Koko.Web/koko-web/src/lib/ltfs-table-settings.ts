import { browser } from "$app/environment";
import { writable } from "svelte/store";

export const LTFS_BASE_COLUMNS = [
    { id: "name", label: "名称" },
    { id: "size", label: "大小" },
    { id: "readOnly", label: "只读" },
] as const;

export type LtfsBaseColumnId = (typeof LTFS_BASE_COLUMNS)[number]["id"];
export type LtfsColumnId = LtfsBaseColumnId | `xattr:${string}`;

export interface LtfsTableSettings {
    xattrKeys: string[];
    columnOrder: LtfsColumnId[];
}

export interface LtfsTableColumn {
    id: LtfsColumnId;
    label: string;
    kind: "base" | "xattr";
    xattrKey?: string;
}

const storageKey = "koko.ltfs.tableSettings.v1";
const defaultColumnOrder = LTFS_BASE_COLUMNS.map((column) => column.id);

export const defaultLtfsTableSettings: LtfsTableSettings = {
    xattrKeys: [],
    columnOrder: [...defaultColumnOrder],
};

function normalizeXattrKey(value: string): string {
    return value.trim();
}

function xattrColumnId(key: string): LtfsColumnId {
    return `xattr:${key}`;
}

function parseSettings(value: unknown): LtfsTableSettings {
    if (!value || typeof value !== "object") {
        return { ...defaultLtfsTableSettings };
    }

    const candidate = value as Partial<LtfsTableSettings>;
    const xattrKeys = Array.isArray(candidate.xattrKeys)
        ? candidate.xattrKeys
              .filter((key): key is string => typeof key === "string")
              .map(normalizeXattrKey)
              .filter((key, index, keys) => key.length > 0 && keys.indexOf(key) === index)
        : [];

    const validColumnIds = new Set<LtfsColumnId>([
        ...defaultColumnOrder,
        ...xattrKeys.map(xattrColumnId),
    ]);
    const columnOrder = Array.isArray(candidate.columnOrder)
        ? candidate.columnOrder.filter(
              (id): id is LtfsColumnId =>
                  typeof id === "string" && validColumnIds.has(id as LtfsColumnId),
          )
        : [];

    return {
        xattrKeys,
        columnOrder: [
            ...columnOrder,
            ...defaultColumnOrder.filter((id) => !columnOrder.includes(id)),
            ...xattrKeys
                .map(xattrColumnId)
                .filter((id) => !columnOrder.includes(id)),
        ],
    };
}

function loadSettings(): LtfsTableSettings {
    if (!browser) {
        return { ...defaultLtfsTableSettings };
    }

    const raw = window.localStorage.getItem(storageKey);
    if (!raw) {
        return { ...defaultLtfsTableSettings };
    }

    try {
        return parseSettings(JSON.parse(raw));
    } catch {
        return { ...defaultLtfsTableSettings };
    }
}

export const ltfsTableSettings = writable<LtfsTableSettings>(loadSettings());

if (browser) {
    ltfsTableSettings.subscribe((settings) => {
        window.localStorage.setItem(storageKey, JSON.stringify(parseSettings(settings)));
    });
}

export function getLtfsTableColumns(settings: LtfsTableSettings): LtfsTableColumn[] {
    return parseSettings(settings).columnOrder.map((id) => {
        if (id.startsWith("xattr:")) {
            const key = id.slice("xattr:".length);
            return {
                id,
                label: key,
                kind: "xattr",
                xattrKey: key,
            };
        }

        const base = LTFS_BASE_COLUMNS.find((column) => column.id === id);
        return {
            id,
            label: base?.label ?? id,
            kind: "base",
        };
    });
}

export function addLtfsXattrColumn(
    settings: LtfsTableSettings,
    key: string,
): LtfsTableSettings {
    const normalized = normalizeXattrKey(key);
    if (!normalized || settings.xattrKeys.includes(normalized)) {
        return parseSettings(settings);
    }

    return parseSettings({
        xattrKeys: [...settings.xattrKeys, normalized],
        columnOrder: [...settings.columnOrder, xattrColumnId(normalized)],
    });
}

export function removeLtfsColumn(
    settings: LtfsTableSettings,
    id: LtfsColumnId,
): LtfsTableSettings {
    if (!id.startsWith("xattr:")) {
        return parseSettings(settings);
    }

    const key = id.slice("xattr:".length);
    return parseSettings({
        xattrKeys: settings.xattrKeys.filter((item) => item !== key),
        columnOrder: settings.columnOrder.filter((item) => item !== id),
    });
}

export function moveLtfsColumn(
    settings: LtfsTableSettings,
    id: LtfsColumnId,
    direction: -1 | 1,
): LtfsTableSettings {
    const normalized = parseSettings(settings);
    const index = normalized.columnOrder.indexOf(id);
    const nextIndex = index + direction;
    if (index < 0 || nextIndex < 0 || nextIndex >= normalized.columnOrder.length) {
        return normalized;
    }

    const columnOrder = [...normalized.columnOrder];
    [columnOrder[index], columnOrder[nextIndex]] = [
        columnOrder[nextIndex],
        columnOrder[index],
    ];

    return parseSettings({
        ...normalized,
        columnOrder,
    });
}

export function resetLtfsTableSettings(): LtfsTableSettings {
    return { ...defaultLtfsTableSettings };
}
