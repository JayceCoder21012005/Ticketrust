"use client";

import { createContext, use, useEffect, useState, useSyncExternalStore, type ReactNode } from "react";
import {
  getSession,
  login,
  logout,
  refreshAccessToken,
  subscribe,
  type Session,
  type SessionUser,
} from "@/lib/api/session";
import { hasSessionHint } from "@/lib/auth/session-hint";

export type AuthStatus = "loading" | "authenticated" | "anonymous";

type AuthContextValue = {
  status: AuthStatus;
  user: SessionUser | null;
  signIn: (credentials: { email: string; password: string }) => Promise<Session>;
  signOut: () => Promise<void>;
};

const AuthContext = createContext<AuthContextValue | null>(null);

/** Refresh this long before the access token expires, so requests never race its expiry. */
const REFRESH_LEAD_MS = 60_000;

const noSubscription = () => () => {};
const readHint = () => hasSessionHint(document.cookie);

/**
 * Holds the in-memory session (spec 03). On load it restores the session with one refresh call, but only
 * when the `tr_session` hint says a refresh cookie probably exists, so anonymous visitors cost no request.
 * Every refresh, scheduled or triggered by a 401, goes through one shared in-flight promise.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const session = useSyncExternalStore(subscribe, getSession, () => null);
  const hint = useSyncExternalStore(noSubscription, readHint, () => false);
  const [restored, setRestored] = useState(false);
  const restoring = hint && !session && !restored;

  useEffect(() => {
    if (!restoring) {
      return;
    }

    void refreshAccessToken().finally(() => setRestored(true));
  }, [restoring]);

  useEffect(() => {
    if (!session) {
      return;
    }

    const delay = Math.max(0, Date.parse(session.expiresAt) - Date.now() - REFRESH_LEAD_MS);
    const timer = setTimeout(() => void refreshAccessToken(), delay);
    return () => clearTimeout(timer);
  }, [session]);

  const value: AuthContextValue = {
    status: session ? "authenticated" : restoring ? "loading" : "anonymous",
    user: session?.user ?? null,
    signIn: (credentials) => login(credentials),
    signOut: () => logout(),
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
