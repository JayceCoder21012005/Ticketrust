"use client";

import { useRouter } from "next/navigation";
import Link from "next/link";
import type { MouseEvent } from "react";
import { formatDateTime } from "@/lib/format";
import { t } from "@/lib/i18n";
import { safeLink } from "@/lib/notifications/link";
import type { NotificationItem } from "@/lib/notifications/types";
import { useMarkRead } from "@/lib/notifications/use-notifications";
import { cn } from "@/lib/utils";

/**
 * What happens when a notification is chosen: it is marked read (optimistic, only if unread) and, for a plain click on one
 * with an app-relative link, the app navigates there. A link that is not an app path (see `safeLink`) is ignored, the
 * notification is still marked read.
 */
export function useOpenNotification(afterOpen?: () => void) {
  const router = useRouter();
  const { mutate: markRead } = useMarkRead();

  return (item: NotificationItem, navigate: boolean) => {
    if (!item.read) markRead(item.id);
    const href = safeLink(item.link);
    if (navigate && href) router.push(href);
    afterOpen?.();
  };
}

/** Ctrl/Cmd/Shift-click and middle-click keep their browser meaning (new tab or window): nothing for the router to do. */
const isPlainClick = (event: MouseEvent) => event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey;

const rowClass = "flex w-full items-start gap-3 px-4 py-3 text-left outline-none transition-colors hover:bg-muted focus-visible:bg-muted focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring";

function RowContent({ item }: { item: NotificationItem }) {
  return (
    <>
      <span aria-hidden className={cn("mt-1.5 size-2 shrink-0 rounded-full", item.read ? "bg-transparent" : "bg-primary")} />
      <span className="flex min-w-0 flex-1 flex-col gap-0.5">
        {item.read ? null : <span className="sr-only">{t("notifications.unread")}</span>}
        <span className={cn("text-sm", item.read ? "font-medium" : "font-semibold")}>{item.title}</span>
        <span className="line-clamp-2 text-sm text-muted-foreground">{item.body}</span>
        <time dateTime={item.createdAt} className="text-xs text-muted-foreground">
          {formatDateTime(item.createdAt)}
        </time>
      </span>
    </>
  );
}

/** One notification: a link when it has a safe target, else a button that only marks it read (nothing at all once read). */
function NotificationRow({ item, onOpen }: { item: NotificationItem; onOpen: (item: NotificationItem, navigate: boolean) => void }) {
  const href = safeLink(item.link);

  return (
    <li className="border-b last:border-b-0">
      {href ? (
        <Link
          href={href}
          prefetch={false}
          className={rowClass}
          onClick={(event) => {
            const plain = isPlainClick(event);
            if (plain) event.preventDefault();
            onOpen(item, plain);
          }}
        >
          <RowContent item={item} />
        </Link>
      ) : item.read ? (
        <div className={cn(rowClass, "hover:bg-transparent")}>
          <RowContent item={item} />
        </div>
      ) : (
        <button type="button" className={rowClass} onClick={() => onOpen(item, false)}>
          <RowContent item={item} />
        </button>
      )}
    </li>
  );
}

/** The notifications as given (newest first). Shared by the bell's popover and the list page. */
export function NotificationList({ items, onOpen, label, className }: { items: NotificationItem[]; onOpen: (item: NotificationItem, navigate: boolean) => void; label?: string; className?: string }) {
  return (
    <ul aria-label={label} className={className}>
      {items.map((item) => (
        <NotificationRow key={item.id} item={item} onOpen={onOpen} />
      ))}
    </ul>
  );
}
