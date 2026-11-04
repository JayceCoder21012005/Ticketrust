import { useEffect, useRef, type RefObject } from "react";

export const MIN_SCALE = 0.5;
export const MAX_SCALE = 8;
const DRAG_THRESHOLD_PX = 4;

export type View = { x: number; y: number; k: number };

const clamp = (k: number) => Math.min(MAX_SCALE, Math.max(MIN_SCALE, k));

/** Scales by `factor` around the point (cx, cy) in the group's parent coordinates, keeping that point still. */
export function zoomAt(view: View, factor: number, cx: number, cy: number): View {
  const k = clamp(view.k * factor);
  const ratio = k / view.k;
  return { k, x: cx - (cx - view.x) * ratio, y: cy - (cy - view.y) * ratio };
}

export function pan(view: View, dx: number, dy: number): View {
  return { ...view, x: view.x + dx, y: view.y + dy };
}

/**
 * Wheel zoom, drag to pan and two-finger pinch on an SVG, applied as one `transform` on `group` straight from refs, so a
 * pan never re-renders React (spec 07: 60 fps). Never put `will-change: transform` on the group: in Chromium that made
 * every frame of a 10,000-seat map take 230–500 ms (spike S1).
 */
function paintTo(group: SVGGElement | null, v: View) {
  group?.setAttribute("transform", `translate(${v.x} ${v.y}) scale(${v.k})`);
}

/** Screen pixels → SVG user units, so a drag follows the pointer whatever the zoom of the viewBox. */
function unitsOf(element: SVGSVGElement): number {
  const width = element.viewBox.baseVal?.width ?? 0;
  return element.clientWidth > 0 && width > 0 ? width / element.clientWidth : 1;
}

function svgPoint(element: SVGSVGElement, clientX: number, clientY: number) {
  const rect = element.getBoundingClientRect();
  const box = element.viewBox.baseVal;
  const units = unitsOf(element);
  return { x: (box?.x ?? 0) + (clientX - rect.left) * units, y: (box?.y ?? 0) + (clientY - rect.top) * units };
}

/**
 * Wheel zoom, drag to pan and two-finger pinch on an SVG, applied as one `transform` on `group` straight from refs, so a
 * pan never re-renders React (spec 07: 60 fps). Never put `will-change: transform` on the group: in Chromium that made
 * every frame of a 10,000-seat map take 230–500 ms (spike S1).
 */
export function usePanZoom(svg: RefObject<SVGSVGElement | null>, group: RefObject<SVGGElement | null>, resetKey: unknown) {
  const view = useRef<View>({ x: 0, y: 0, k: 1 });
  const dragged = useRef(false);

  const zoomBy = (factor: number) => {
    const box = svg.current?.viewBox.baseVal;
    view.current = zoomAt(view.current, factor, box ? box.x + box.width / 2 : 0, box ? box.y + box.height / 2 : 0);
    paintTo(group.current, view.current);
  };

  useEffect(() => {
    view.current = { x: 0, y: 0, k: 1 };
    paintTo(group.current, view.current);
  }, [group, resetKey]);

  useEffect(() => {
    const element = svg.current;
    if (!element) return;
    const pointers = new Map<number, { x: number; y: number }>();
    let start: { x: number; y: number } | null = null;
    const paint = () => paintTo(group.current, view.current);

    const wheel = (e: WheelEvent) => {
      e.preventDefault();
      const p = svgPoint(element, e.clientX, e.clientY);
      view.current = zoomAt(view.current, e.deltaY < 0 ? 1.15 : 1 / 1.15, p.x, p.y);
      paint();
    };

    const down = (e: PointerEvent) => {
      pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
      start = { x: e.clientX, y: e.clientY };
      dragged.current = false;
    };

    const move = (e: PointerEvent) => {
      const previous = pointers.get(e.pointerId);
      if (!previous) return;
      if (pointers.size === 2) {
        const other = [...pointers.entries()].find(([id]) => id !== e.pointerId)![1];
        const before = Math.hypot(previous.x - other.x, previous.y - other.y);
        const after = Math.hypot(e.clientX - other.x, e.clientY - other.y);
        if (before > 0) {
          const mid = svgPoint(element, (e.clientX + other.x) / 2, (e.clientY + other.y) / 2);
          view.current = zoomAt(view.current, after / before, mid.x, mid.y);
        }
      } else {
        const units = unitsOf(element);
        view.current = pan(view.current, (e.clientX - previous.x) * units, (e.clientY - previous.y) * units);
      }

      pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
      if (start && Math.hypot(e.clientX - start.x, e.clientY - start.y) > DRAG_THRESHOLD_PX) dragged.current = true;
      paint();
    };

    const up = (e: PointerEvent) => {
      pointers.delete(e.pointerId);
    };

    element.addEventListener("wheel", wheel, { passive: false });
    element.addEventListener("pointerdown", down);
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
    window.addEventListener("pointercancel", up);
    return () => {
      element.removeEventListener("wheel", wheel);
      element.removeEventListener("pointerdown", down);
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
      window.removeEventListener("pointercancel", up);
    };
  }, [svg, group]);

  return {
    zoomIn: () => zoomBy(1.4),
    zoomOut: () => zoomBy(1 / 1.4),
    /** True when the last pointer gesture was a drag, so its trailing click must not toggle a seat. */
    wasDrag: () => dragged.current,
  };
}
