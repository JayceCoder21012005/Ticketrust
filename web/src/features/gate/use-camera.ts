"use client";

import { useCallback, useEffect, useRef, useState, type RefObject } from "react";

export type CameraStatus = "starting" | "ready" | "denied" | "unavailable";
export type Facing = "environment" | "user";

type TorchCapabilities = MediaTrackCapabilities & { torch?: boolean };

/**
 * The phone's camera for the scanner: the back camera first, a switch to the front one, and the torch where the
 * camera has one. The stream stops when the screen goes away or the camera changes.
 */
export function useCamera(videoRef: RefObject<HTMLVideoElement | null>) {
  const [facing, setFacing] = useState<Facing>("environment");
  const [status, setStatus] = useState<CameraStatus>("starting");
  const [torchSupported, setTorchSupported] = useState(false);
  const [torchOn, setTorchOn] = useState(false);
  const track = useRef<MediaStreamTrack | null>(null);

  useEffect(() => {
    let cancelled = false;
    let stream: MediaStream | undefined;

    async function start() {
      if (!navigator.mediaDevices?.getUserMedia) {
        setStatus("unavailable");
        return;
      }

      try {
        stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: facing }, width: { ideal: 1280 } }, audio: false });
        const video = videoRef.current;
        if (cancelled || !video) {
          stream.getTracks().forEach((t) => t.stop());
          return;
        }

        video.srcObject = stream;
        await video.play();
        const [videoTrack] = stream.getVideoTracks();
        track.current = videoTrack;
        setTorchSupported(Boolean((videoTrack.getCapabilities?.() as TorchCapabilities | undefined)?.torch));
        setTorchOn(false);
        setStatus("ready");
      } catch (error) {
        if (!cancelled) setStatus(error instanceof DOMException && error.name === "NotAllowedError" ? "denied" : "unavailable");
      }
    }

    void start();
    return () => {
      cancelled = true;
      stream?.getTracks().forEach((t) => t.stop());
      track.current = null;
    };
  }, [facing, videoRef]);

  const toggleTorch = useCallback(async () => {
    const next = !torchOn;
    try {
      await track.current?.applyConstraints({ advanced: [{ torch: next } as MediaTrackConstraintSet] });
      setTorchOn(next);
    } catch {
      setTorchSupported(false); // The camera said no: hide the button instead of leaving a dead one.
    }
  }, [torchOn]);

  const switchCamera = useCallback(() => setFacing((current) => (current === "environment" ? "user" : "environment")), []);

  return { status, facing, torchSupported, torchOn, toggleTorch, switchCamera };
}
