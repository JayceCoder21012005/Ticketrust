import { api } from "@/lib/api/client";
import type { SessionUser } from "@/lib/api/session";
import { ApiError, type ProblemDetails } from "@/lib/errors";

type ProfileUpdate = { fullName: string; phoneNumber: string | null; avatarUrl: string | null };

function orThrow<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.data === undefined) {
    throw new ApiError(result.response.status, (result.error ?? {}) as ProblemDetails);
  }

  return result.data;
}

export async function getMe(): Promise<SessionUser> {
  return orThrow(await api.GET("/api/v1/me"));
}

export async function updateMe(body: ProfileUpdate): Promise<SessionUser> {
  return orThrow(await api.PUT("/api/v1/me", { body }));
}

/** Presigned upload (spec 03): ask the api for a one-file URL, PUT the bytes there, return the public URL. */
export async function uploadAvatar(file: File): Promise<string> {
  const ticket = orThrow(await api.POST("/api/v1/me/avatar-upload-url", { body: { contentType: file.type, size: file.size } }));
  const target = new URL(ticket.uploadUrl);
  const response = await fetch(`${target.pathname}${target.search}`, {
    method: "PUT",
    headers: { "Content-Type": file.type },
    body: file,
  });

  if (!response.ok) {
    throw new ApiError(response.status, (await response.json().catch(() => ({}))) as ProblemDetails);
  }

  return ticket.publicUrl;
}
