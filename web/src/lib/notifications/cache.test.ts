import { QueryClient, type InfiniteData } from "@tanstack/react-query";
import { beforeEach, describe, expect, it } from "vitest";
import { addNotification, markAllReadInCache, markReadInCache, unreadCountOf } from "./cache";
import { notificationKeys } from "./keys";
import type { NotificationItem, NotificationPage } from "./types";

const item = (n: number, read = false): NotificationItem => ({ id: `n${n}`, type: "OrderPaid", title: `Tiêu đề ${n}`, body: "Nội dung", link: null, read, createdAt: "2026-12-01T10:00:00Z" });
const data = (pages: NotificationPage[]): InfiniteData<NotificationPage, string | undefined> => ({ pages, pageParams: [undefined, ...pages.slice(1).map((_, i) => `c${i + 1}`)] });

let client: QueryClient;
const read = () => client.getQueryData<InfiniteData<NotificationPage, string | undefined>>(notificationKeys.list);

beforeEach(() => {
  client = new QueryClient();
});

describe("notification cache", () => {
  it("reads the unread count of the first page, 0 without data", () => {
    expect(unreadCountOf(undefined)).toBe(0);
    expect(unreadCountOf(data([{ items: [], nextCursor: null, unreadCount: 4 }]))).toBe(4);
  });

  it("prepends a pushed notification once and bumps the unread count", () => {
    client.setQueryData(notificationKeys.list, data([{ items: [item(1)], nextCursor: null, unreadCount: 1 }]));

    addNotification(client, item(2));
    addNotification(client, item(2));

    expect(read()!.pages[0]!.items.map((i) => i.id)).toEqual(["n2", "n1"]);
    expect(unreadCountOf(read())).toBe(2);
  });

  it("leaves an empty cache alone: the first load will include it", () => {
    addNotification(client, item(1));
    expect(read()).toBeUndefined();
  });

  it("marks one unread item read and lowers the count, but not twice", () => {
    client.setQueryData(notificationKeys.list, data([{ items: [item(2), item(1, true)], nextCursor: "c1", unreadCount: 1 }, { items: [item(0)], nextCursor: null, unreadCount: 1 }]));

    markReadInCache(client, "n2");
    markReadInCache(client, "n2");
    markReadInCache(client, "n1");

    expect(read()!.pages[0]!.items.map((i) => i.read)).toEqual([true, true]);
    expect(unreadCountOf(read())).toBe(0);
    expect(read()!.pages[1]!.items[0]!.read).toBe(false);
  });

  it("marks everything read", () => {
    client.setQueryData(notificationKeys.list, data([{ items: [item(2), item(1)], nextCursor: "c1", unreadCount: 2 }, { items: [item(0)], nextCursor: null, unreadCount: 2 }]));

    markAllReadInCache(client);

    expect(read()!.pages.flatMap((p) => p.items).every((i) => i.read)).toBe(true);
    expect(unreadCountOf(read())).toBe(0);
  });
});
