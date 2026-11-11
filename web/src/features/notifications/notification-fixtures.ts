import type { NotificationItem, NotificationPage } from "@/lib/notifications/types";

/** Test data shared by the notification component tests. */
export const notification = (n: number, extra: Partial<NotificationItem> = {}): NotificationItem => ({
  id: `0000000${n}-aaaa-bbbb-cccc-dddddddddddd`,
  type: "OrderPaid",
  title: `Thanh toán thành công ${n}`,
  body: `Đơn #0000000${n} đã được thanh toán.`,
  link: `/account/orders/0000000${n}-aaaa-bbbb-cccc-dddddddddddd`,
  read: false,
  createdAt: "2026-12-01T03:00:00Z",
  ...extra,
});

export const notificationPage = (items: NotificationItem[], unreadCount = items.filter((i) => !i.read).length, nextCursor: string | null = null): NotificationPage => ({
  items,
  nextCursor,
  unreadCount,
});

/** `count` notifications numbered from `from` upwards (single digits keep the ids readable). */
export const notifications = (count: number, from = 1) => Array.from({ length: count }, (_, i) => notification(from + i));
