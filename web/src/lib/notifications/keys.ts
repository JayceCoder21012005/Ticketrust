/** TanStack Query keys of the inbox. Everything lives under `["notifications"]`. */
export const notificationKeys = {
  all: ["notifications"] as const,
  /** The infinite (keyset) query behind both the bell's popover and the list page. */
  list: ["notifications", "list"] as const,
};

/** Notifications per request: the popover shows the first page, the list page loads one more per "Tải thêm". */
export const PAGE_SIZE = 10;
