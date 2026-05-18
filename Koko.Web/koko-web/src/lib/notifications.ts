import type NotificationQueueComponent from "carbon-components-svelte/src/Notification/NotificationQueue.svelte";
import type { NotificationData } from "carbon-components-svelte/src/Notification/NotificationQueue.svelte";

type NotificationKind = NonNullable<NotificationData["kind"]>;

type RealtimeNotificationEvent = {
    id: string;
    type: string;
    severity: string;
    message: string;
    timestampUtc: string;
};

let queue: NotificationQueueComponent | null = null;
const pending: NotificationData[] = [];

export function bindNotificationQueue(nextQueue: NotificationQueueComponent | null | undefined): void {
    if (!nextQueue || nextQueue === queue) return;

    queue = nextQueue;
    while (pending.length > 0) {
        queue.add(pending.shift()!);
    }
}

export function notifyInfo(title: string, subtitle?: string): void {
    addNotification("info", title, subtitle);
}

export function notifySuccess(title: string, subtitle?: string): void {
    addNotification("success", title, subtitle);
}

export function notifyWarning(title: string, subtitle?: string): void {
    addNotification("warning", title, subtitle);
}

export function notifyError(title: string, subtitle?: string): void {
    addNotification("error", title, subtitle, 0);
}

export function notifyException(error: unknown, title = "Operation failed"): void {
    notifyError(title, error instanceof Error ? error.message : String(error));
}

export function notifyRealtimeEvent(event: RealtimeNotificationEvent): void {
    const kind = toNotificationKind(event.severity);
    add({
        id: `event:${event.id}`,
        kind,
        title: event.type,
        subtitle: event.message,
        caption: formatCaption(event.timestampUtc),
        timeout: kind === "error" ? 0 : 6000,
        closeButtonDescription: "Close notification",
        statusIconDescription: `${kind} icon`,
    });
}

function addNotification(kind: NotificationKind, title: string, subtitle?: string, timeout = 6000): void {
    add({
        kind,
        title,
        subtitle,
        timeout,
        closeButtonDescription: "Close notification",
        statusIconDescription: `${kind} icon`,
    });
}

function add(notification: NotificationData): void {
    if (queue) {
        queue.add(notification);
        return;
    }

    pending.push(notification);
}

function toNotificationKind(severity: string): NotificationKind {
    switch (severity.toLowerCase()) {
        case "success":
            return "success";
        case "warning":
        case "warn":
            return "warning";
        case "error":
        case "fatal":
            return "error";
        default:
            return "info";
    }
}

function formatCaption(timestampUtc: string): string {
    const timestamp = new Date(timestampUtc);
    return Number.isNaN(timestamp.getTime()) ? "" : timestamp.toLocaleTimeString();
}
