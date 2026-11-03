"use client";

import { useEffect, useState } from "react";
import { fetchServerTime } from "@/lib/server-time";

type Status = { kind: "loading" } | { kind: "ok"; serverNow: Date } | { kind: "error"; message: string };

/** Proves the web → `/api` rewrite → api path works end to end. */
export function ApiStatus() {
  const [status, setStatus] = useState<Status>({ kind: "loading" });

  useEffect(() => {
    const controller = new AbortController();
    fetchServerTime(controller.signal)
      .then(({ serverNow }) => setStatus({ kind: "ok", serverNow }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) {
          setStatus({ kind: "error", message: error instanceof Error ? error.message : "Unknown error" });
        }
      });
    return () => controller.abort();
  }, []);

  if (status.kind === "loading") {
    return <p role="status">Đang kết nối API…</p>;
  }

  if (status.kind === "error") {
    return <p role="alert" className="text-danger">Không kết nối được API: {status.message}</p>;
  }

  return (
    <p role="status" className="text-success">
      API OK · giờ máy chủ {status.serverNow.toLocaleString("vi-VN", { timeZone: "Asia/Ho_Chi_Minh" })}
    </p>
  );
}
