import { api } from "@/lib/api/client";
import { ApiError, type ProblemDetails } from "@/lib/errors";

const fail = (status: number, error: unknown) => new ApiError(status, (error ?? {}) as ProblemDetails);

/** `GET /events/{id}/follow`: is the signed-in user following this event? `id` is the event's GUID, not its slug. */
export async function getFollowing(eventId: string, signal?: AbortSignal): Promise<boolean> {
  const result = await api.GET("/api/v1/events/{id}/follow", { params: { path: { id: eventId } }, signal, cache: "no-store" });
  if (result.data === undefined) {
    throw fail(result.response.status, result.error);
  }

  return result.data.following;
}

/**
 * `POST` (follow) or `DELETE` (unfollow) `/events/{id}/follow` (204). Both are idempotent. Following an event that is no
 * longer published is a 409 `event.not_followable`.
 */
export async function setFollowing(eventId: string, following: boolean): Promise<void> {
  const params = { params: { path: { id: eventId } } };
  const result = following ? await api.POST("/api/v1/events/{id}/follow", params) : await api.DELETE("/api/v1/events/{id}/follow", params);
  if (!result.response.ok) {
    throw fail(result.response.status, "error" in result ? result.error : undefined);
  }
}
