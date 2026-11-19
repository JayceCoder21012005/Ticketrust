/** `NotificationItem` of the api; also the payload of the hub's `Notification` message. */
export type NotificationItem = {
  id: string;
  /** OrderPaid, RefundRequested, Refunded, OrderExpired, EventCancelled, SaleOpensSoon, EventStartsSoon or WaitlistOffer. */
  type: string;
  title: string;
  body: string;
  /** App-relative path (`/account/orders/{id}`, `/events/{slug}`), or null. Never trusted: see `safeLink`. */
  link: string | null;
  read: boolean;
  createdAt: string;
};

export type NotificationPage = {
  items: NotificationItem[];
  /** Opaque keyset cursor for the next (older) page; null on the last page. */
  nextCursor: string | null;
  /** Unread notifications of the whole inbox, not just this page. */
  unreadCount: number;
};
