"use client";

import { createContext, use, useEffect, useState, useSyncExternalStore, type ReactNode } from "react";
import {
  getSession,
  isSessionUnreachable,
  login,
  logout,
  refreshAccessToken,
  subscribe,
  type Session,
  type SessionUser,
} from "@/lib/api/session";
import { hasSessionHint } from "@/lib/auth/session-hint";
import { forgetOfflineTicketsLater } from "@/lib/tickets/forget-offline";
import { readOwnerMarker } from "@/lib/tickets/owner-marker";

export type AuthStatus = "loading" | "authenticated" | "anonymous";

type AuthContextValue = {
  status: AuthStatus;
  user: SessionUser | null;
  /**
   * The session hint says "signed in" but the session could not be restored for lack of a network (status stays
   * `anonymous`). Only the offline ticket wallet acts on it; every other guarded page still goes to login.
   */
  offline: boolean;
  signIn: (credentials: { email: string; password: string; captchaToken?: string | null }) => Promise<Session>;
  signOut: () => Promise<void>;
};

const AuthContext = createContext<AuthContextValue | null>(null);

/** Refresh this long before the access token expires, so requests never race its expiry. */
const REFRESH_LEAD_MS = 60_000;

const noSubscription = () => () => {};
const readHint = () => hasSessionHint(document.cookie);
const onClient = () => true;
const onServer = () => false;

/**
 * Holds the in-memory session (spec 03). On load it restores the session with one refresh call, but only
 * when the `tr_session` hint says a refresh cookie probably exists, so anonymous visitors cost no request.
 * Every refresh, scheduled or triggered by a 401, goes through one shared in-flight promise.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const session = useSyncExternalStore(subscribe, getSession, () => null);
  const unreachable = useSyncExternalStore(subscribe, isSessionUnreachable, () => false);
  const hint = useSyncExternalStore(noSubscription, readHint, () => false);
  // The server render and hydration can't read the cookie: until the client can, the session is unknown, not absent.
  const hydrated = useSyncExternalStore(noSubscription, onClient, onServer);
  const [restored, setRestored] = useState(false);
  const restoring = hint && !session && !restored;

  useEffect(() => {
    if (!restoring) {
      return;
    }

    void refreshAccessToken().finally(() => setRestored(true));
  }, [restoring]);

  // Every session start (the login page included, where no query cache is watched): if the offline wallet on this
  // device belongs to someone else, it is wiped before anything can show it. Only then is the cleanup loaded.
  const userId = session?.user.id ?? null;
  useEffect(() => {
    if (!userId) return;
    const owner = readOwnerMarker();
    if (owner !== null && owner !== userId) void forgetOfflineTicketsLater();
  }, [userId]);

  useEffect(() => {
    if (!session) {
      return;
    }

    const delay = Math.max(0, Date.parse(session.expiresAt) - Date.now() - REFRESH_LEAD_MS);
    const timer = setTimeout(() => void refreshAccessToken(), delay);
    return () => clearTimeout(timer);
  }, [session]);

  const value: AuthContextValue = {
    status: session ? "authenticated" : !hydrated || restoring ? "loading" : "anonymous",
    user: session?.user ?? null,
    offline: !session && hint && unreachable,
    signIn: (credentials) => login(credentials),
    // Logout wipes the offline wallet (cached keys, ticket details, the service worker and its caches).
    signOut: () => logout().finally(() => forgetOfflineTicketsLater()),
  };

  return <AuthContext value={value}>{children}</AuthContext>;
}

export function useAuth(): AuthContextValue {
  const context = use(AuthContext);
  if (!context) {
    throw new Error("useAuth must be used inside <AuthProvider>.");
  }

  return context;
}
