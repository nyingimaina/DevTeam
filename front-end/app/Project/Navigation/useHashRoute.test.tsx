import React from "react";
import { act, render, screen } from "@testing-library/react";
import "@testing-library/jest-dom";
import { useHashRoute } from "./useHashRoute";
import { Route } from "./routes";

let navigate: (r: Route) => void = () => undefined;

function Probe({ active = true }: { active?: boolean }) {
  const [route, nav] = useHashRoute(active);
  navigate = nav;
  return <div data-testid="route">{JSON.stringify(route)}</div>;
}

function setHash(hash: string) {
  window.location.hash = hash;
  window.dispatchEvent(new HashChangeEvent("hashchange"));
}

describe("useHashRoute", () => {
  beforeEach(() => { window.location.hash = ""; });
  afterEach(() => { window.location.hash = ""; });

  it("starts at the route named by the address", () => {
    window.location.hash = "#/r/abc";
    render(<Probe />);
    expect(screen.getByTestId("route")).toHaveTextContent('"kind":"release"');
  });

  it("starts at the project home when the address is empty", () => {
    render(<Probe />);
    expect(screen.getByTestId("route")).toHaveTextContent('"kind":"home"');
  });

  it("follows the address when it changes (back/forward, pasted links)", () => {
    render(<Probe />);
    act(() => setHash("#/r/abc/f/def"));
    expect(screen.getByTestId("route")).toHaveTextContent('"kind":"feature"');
    act(() => setHash("#/"));
    expect(screen.getByTestId("route")).toHaveTextContent('"kind":"home"');
  });

  it("navigate() updates the address and the route", () => {
    render(<Probe />);
    act(() => navigate({ kind: "release", releaseId: "abc" }));
    expect(window.location.hash).toBe("#/r/abc");
    expect(screen.getByTestId("route")).toHaveTextContent('"releaseId":"abc"');
  });

  it("does not write the address while its tab is hidden (Git/Settings must not clobber it)", () => {
    window.location.hash = "#/r/abc";
    render(<Probe active={false} />);
    act(() => navigate({ kind: "home" }));
    expect(window.location.hash).toBe("#/r/abc");
  });

  it("re-reads the address when its tab becomes active again", () => {
    window.location.hash = "#/r/abc";
    const { rerender } = render(<Probe active={false} />);
    window.location.hash = "#/r/xyz"; // changed while hidden
    rerender(<Probe active />);
    expect(screen.getByTestId("route")).toHaveTextContent('"releaseId":"xyz"');
  });

  it("treats a garbage address as home rather than crashing", () => {
    window.location.hash = "#/r/../../x";
    render(<Probe />);
    expect(screen.getByTestId("route")).toHaveTextContent('"kind":"home"');
  });

  it("stops listening when unmounted", () => {
    const { unmount } = render(<Probe />);
    unmount();
    expect(() => act(() => setHash("#/r/abc"))).not.toThrow();
  });
});
