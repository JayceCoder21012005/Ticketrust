"use client";

import { QueryClient, QueryClientContext, QueryClientProvider } from "@tanstack/react-query";
import { Bell, BellRing } from "lucide-react";
import { use, useEffect, useState, useSyncExternalStore, type ReactNode } from "react";
import { Button } from "@/components/ui/button";
import { getSession, refreshAccessToken, subscribe } from "@/lib/api/session";
import { useDropCacheOnUserChange } from "@/lib/api/use-drop-cache-on-user-change";
import { t } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import { RemindLink, RemindPlaceholder, type FollowSize, type FollowVariant } from "./follow-ui";
import { useFollow } from "./use-follow";

type ControlProps = { eventId: string; path: string; size: FollowSize; variant: FollowVariant; className?: string };

/**
 * Public pages have no `AppProviders` (their JS is budgeted), so a signed-in visitor there gets a query client of the
 * follow buttons' own. It is one for the whole page: the panel and the mobile bar both render a button for the same event
 * and share one request and one state through it. Dropped with the user like the app's.
 */
let publicClient: QueryClient | null = null;
const getPublicClient = () =>
  (publicClient ??= new QueryClient({ defaultOptions: { queries: { refetchOnWindowFocus: false, retry: 1 }, mutations: { retry: false } } }));

/** For tests: forget the page's client between cases. */
export function resetPublicClient() {
  publicClient?.clear();
  publicClient = null;
}

function WithQueryClient({ children }: { children: ReactNode }) {
  const inherited = use(QueryClientContext);
  return inherited ? children : <OwnClient>{children}</OwnClient>;
}

function OwnClient({ children }: { children: ReactNode }) {
  const client = getPublicClient();
  useDropCacheOnUserChange(client);
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

/**
 * The toggle behind `FollowButton` (loaded lazily). It needs a session before it asks for the state: on public pages
 * nothing restores it, so a hint with a live refresh cookie restores it here, and a stale hint ends in the login link
 * instead of a redirect (the api client would otherwise send the visitor to login on the first 401).
 */
function Control({ eventId, path, size, variant, className }: ControlProps) {
  const session = useSyncExternalStore(subscribe, getSession, () => null);
  const [restoreFailed, setRestoreFailed] = useState(false);

  useEffect(() => {
    if (!getSession()) {
      void refreshAccessToken().finally(() => setRestoreFailed(!getSession()));
    }
  }, []);

  if (!session) {
    return restoreFailed ? <RemindLink path={path} size={size} variant={variant} {...(className ? { className } : {})} /> : <RemindPlaceholder size={size} variant={variant} {...(className ? { className } : {})} />;
  }

  return <Toggle eventId={eventId} size={size} variant={variant} {...(className ? { className } : {})} />;
}

function Toggle({ eventId, size, variant, className }: Omit<ControlProps, "path">) {
  const { following, toggle, closed, message, loadFailed, reload } = useFollow(eventId);
  const loading = following === undefined;

  return (
    <div className={cn("flex flex-col gap-2", className)}>
      <Button
        type="button"
        variant={following ? "secondary" : variant}
        size={size}
        aria-busy={loading && !loadFailed ? true : undefined}
        disabled={loading || closed}
        title={loading && !loadFailed ? t("follow.loading") : undefined}
        onClick={toggle}
        className={cn(loading && !loadFailed && "animate-pulse")}
      >
        {following ? <BellRing aria-hidden /> : <Bell aria-hidden />}
        {following ? t("follow.following") : t("follow.remind")}
      </Button>
      {loadFailed ? (
        <p role="alert" className="flex flex-wrap items-center gap-x-2 text-xs text-muted-foreground">
          {t("follow.loadFailed")}
          <Button type="button" variant="link" size="xs" onClick={reload} className="h-auto p-0 text-xs">
            {t("common.retry")}
          </Button>
        </p>
      ) : message ? (
        <p role="alert" className="text-xs text-danger">
          {message}
        </p>
      ) : null}
    </div>
  );
}

export default function FollowControl(props: ControlProps) {
  return (
    <WithQueryClient>
      <Control {...props} />
    </WithQueryClient>
  );
}
