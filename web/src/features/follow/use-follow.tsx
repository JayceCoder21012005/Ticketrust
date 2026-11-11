import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRef } from "react";
import { useSessionPresent } from "@/lib/api/use-session-present";
import { ApiError, toFriendlyMessage } from "@/lib/errors";
import { getFollowing, setFollowing } from "@/lib/follow/api";
import { t } from "@/lib/i18n";

/** What `["follow", eventId]` holds. `closed`: the api said the event can no longer be followed (409 `event.not_followable`). */
export type FollowState = { following: boolean; closed: boolean };

export const followKey = (eventId: string) => ["follow", eventId] as const;

export type Follow = {
  /** `undefined` until the state is known. */
  following: boolean | undefined;
  toggle: () => void;
  pending: boolean;
  /** The event no longer accepts reminders: the button stays off. */
  closed: boolean;
  /** Vietnamese message for the last failure, or for a closed event; `null` when all is well. */
  message: string | null;
  /** The state itself could not be loaded (there is no button state to show). */
  loadFailed: boolean;
  reload: () => void;
};

/**
 * Follow state of one event for the signed-in user. Toggling is optimistic and rolled back if the api refuses: that is
 * safe here because no seat, stock or money is involved and both calls are idempotent. A second click while a request is
 * out is ignored (never two requests in flight).
 */
export function useFollow(eventId: string): Follow {
  const client = useQueryClient();
  const signedIn = useSessionPresent();
  const key = followKey(eventId);
  const busy = useRef(false);

  const query = useQuery({
    queryKey: key,
    queryFn: async ({ signal }): Promise<FollowState> => ({ following: await getFollowing(eventId, signal), closed: false }),
    enabled: signedIn,
    staleTime: 30_000,
  });

  const mutation = useMutation({
    mutationFn: (next: boolean) => setFollowing(eventId, next),
    onMutate: async (next) => {
      await client.cancelQueries({ queryKey: key });
      const previous = client.getQueryData<FollowState>(key);
      client.setQueryData<FollowState>(key, { following: next, closed: false });
      return { previous };
    },
    onError: (error, _next, context) => {
      const closed = error instanceof ApiError && error.code === "event.not_followable";
      client.setQueryData<FollowState>(key, { following: context?.previous?.following ?? false, closed });
    },
    onSettled: () => {
      busy.current = false;
    },
  });

  const data = query.data;
  const closed = data?.closed ?? false;

  const toggle = () => {
    if (busy.current || !data || data.closed) {
      return;
    }

    busy.current = true;
    mutation.mutate(!data.following);
  };

  return {
    following: data?.following,
    toggle,
    pending: mutation.isPending,
    closed,
    message: closed ? t("follow.notFollowable") : mutation.isError ? toFriendlyMessage(mutation.error) : null,
    loadFailed: query.isError && !data,
    reload: () => void query.refetch(),
  };
}
