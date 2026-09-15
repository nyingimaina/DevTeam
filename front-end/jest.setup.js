import "@testing-library/jest-dom";

global.ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

global.IntersectionObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

if (typeof window !== "undefined") {
  window.matchMedia =
    window.matchMedia ||
    (() => ({ matches: false, addEventListener() {}, removeEventListener() {} }));

  // jsdom doesn't implement the Pointer Events spec (no PointerEvent constructor, no
  // hasPointerCapture/setPointerCapture/releasePointerCapture on Element) — Radix UI's
  // interactive primitives (used internally by e.g. ZestButton's split-button dropdown)
  // rely on these, and without them userEvent.click on a Radix trigger can hang
  // indefinitely instead of failing cleanly. This is the standard, widely-documented fix.
  if (!window.PointerEvent) {
    class PointerEvent extends MouseEvent {
      constructor(type, params = {}) {
        super(type, params);
        this.pointerId = params.pointerId ?? 0;
        this.pointerType = params.pointerType ?? "mouse";
        this.isPrimary = params.isPrimary ?? true;
        this.width = params.width ?? 1;
        this.height = params.height ?? 1;
        this.pressure = params.pressure ?? 0;
      }
    }
    window.PointerEvent = PointerEvent;
  }
  // Stateful (not just no-op) — Radix's own pointer-capture logic checks hasPointerCapture
  // after calling setPointerCapture and behaves oddly (observed: userEvent.click hanging
  // indefinitely on a Radix trigger) if that check never reflects the capture it just made.
  if (!Element.prototype.hasPointerCapture) {
    const captured = new WeakMap();
    Element.prototype.setPointerCapture = function setPointerCapture(pointerId) {
      let ids = captured.get(this);
      if (!ids) { ids = new Set(); captured.set(this, ids); }
      ids.add(pointerId);
    };
    Element.prototype.releasePointerCapture = function releasePointerCapture(pointerId) {
      captured.get(this)?.delete(pointerId);
    };
    Element.prototype.hasPointerCapture = function hasPointerCapture(pointerId) {
      return captured.get(this)?.has(pointerId) ?? false;
    };
  }
  if (!Element.prototype.scrollIntoView) {
    Element.prototype.scrollIntoView = () => {};
  }
}