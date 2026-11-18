import type { Tone } from "./describe-scan";

/** Distinct vibration patterns, so a result can be felt without looking: one long buzz, two short, three short. */
const PATTERNS: Record<Tone, number[]> = { ok: [120], warn: [80, 60, 80], bad: [60, 50, 60, 50, 60] };

/** Distinct tones: a rising beep, a flat double beep, a low buzz. */
const BEEPS: Record<Tone, { freq: number; ms: number }[]> = {
  ok: [{ freq: 880, ms: 140 }],
  warn: [
    { freq: 520, ms: 90 },
    { freq: 520, ms: 90 },
  ],
  bad: [{ freq: 180, ms: 380 }],
};

let audio: AudioContext | null = null;

function beep(tone: Tone) {
  try {
    audio ??= new AudioContext();
    let at = audio.currentTime;
    for (const { freq, ms } of BEEPS[tone]) {
      const osc = audio.createOscillator();
      const gain = audio.createGain();
      osc.frequency.value = freq;
      osc.type = tone === "bad" ? "sawtooth" : "sine";
      gain.gain.value = 0.15;
      osc.connect(gain).connect(audio.destination);
      osc.start(at);
      osc.stop(at + ms / 1000);
      at += ms / 1000 + 0.07;
    }
  } catch {
    // No audio (blocked autoplay, unsupported): the colour and the vibration still say it.
  }
}

/** Vibration and sound for a result. Both are best effort. */
export function giveFeedback(tone: Tone) {
  try {
    navigator.vibrate?.(PATTERNS[tone]);
  } catch {
    // Not supported.
  }

  beep(tone);
}
