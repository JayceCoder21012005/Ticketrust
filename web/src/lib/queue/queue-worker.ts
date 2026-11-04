import { fetchQueueStatus } from "./fetch-status";
import { createPoller } from "./poller";
import type { WorkerIn, WorkerOut } from "./worker-protocol";

/**
 * Polls the waiting room from a Web Worker: browsers throttle main-thread timers in background tabs, which would
 * delay admission by minutes; worker timers are not throttled the same way (spec 06). It has no access to the page's
 * in-memory session, so the page hands it the access token and a fresh one after every 401.
 */
type WorkerScope = {
  postMessage: (message: WorkerOut) => void;
  onmessage: ((event: MessageEvent<WorkerIn>) => void) | null;
};

const scope = self as unknown as WorkerScope;
let eventId = "";

const poller = createPoller({
  poll: (token) => fetchQueueStatus(eventId, token),
  events: {
    onStatus: (status) => scope.postMessage({ type: "status", status }),
    onUnauthorized: () => scope.postMessage({ type: "unauthorized" }),
    onNotJoined: () => scope.postMessage({ type: "notJoined" }),
    onError: (failures) => scope.postMessage({ type: "error", failures }),
  },
});

scope.onmessage = ({ data }) => {
  switch (data.type) {
    case "start":
      eventId = data.eventId;
      poller.start(data.token);
      return;
    case "token":
      poller.setToken(data.token);
      return;
    case "pollNow":
      poller.pollNow();
      return;
    case "stop":
      poller.stop();
      return;
  }
};
