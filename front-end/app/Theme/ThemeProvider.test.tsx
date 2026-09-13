import React from "react";
import { render, screen, fireEvent, act } from "@testing-library/react";
import ThemeProvider, { useTheme, ThemeStorageKey } from "./ThemeProvider";

const mediaListeners = new Set<() => void>();
let darkMatches = false;

function installMatchMedia() {
  window.matchMedia = jest.fn().mockImplementation((query: string) => ({
    matches: darkMatches,
    media: query,
    addEventListener: (_type: string, cb: () => void) => mediaListeners.add(cb),
    removeEventListener: (_type: string, cb: () => void) => mediaListeners.delete(cb),
  })) as unknown as typeof window.matchMedia;
}

function Probe() {
  const { mode, resolvedTheme, setMode } = useTheme();
  return (
    <div>
      <span data-testid="mode">{mode}</span>
      <span data-testid="resolved">{resolvedTheme}</span>
      <button type="button" onClick={() => setMode("light")}>
        L
      </button>
      <button type="button" onClick={() => setMode("dark")}>
        D
      </button>
      <button type="button" onClick={() => setMode("system")}>
        S
      </button>
    </div>
  );
}

function currentTheme() {
  return document.documentElement.dataset.theme;
}

beforeEach(() => {
  localStorage.clear();
  darkMatches = false;
  mediaListeners.clear();
  delete document.documentElement.dataset.theme;
  installMatchMedia();
});

describe("ThemeProvider", () => {
  it("defaults to system with a light-resolved theme", () => {
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    expect(screen.getByTestId("mode")).toHaveTextContent("system");
    expect(screen.getByTestId("resolved")).toHaveTextContent("light");
    expect(currentTheme()).toBe("light");
  });

  it("restores a persisted mode and applies it", () => {
    localStorage.setItem(ThemeStorageKey, "dark");
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    expect(screen.getByTestId("mode")).toHaveTextContent("dark");
    expect(screen.getByTestId("resolved")).toHaveTextContent("dark");
    expect(currentTheme()).toBe("dark");
  });

  it("persists and applies an explicit mode", () => {
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    fireEvent.click(screen.getByText("D"));
    expect(screen.getByTestId("mode")).toHaveTextContent("dark");
    expect(screen.getByTestId("resolved")).toHaveTextContent("dark");
    expect(currentTheme()).toBe("dark");
    expect(localStorage.getItem(ThemeStorageKey)).toBe("dark");
  });

  it("resolves system mode from the OS preference", () => {
    darkMatches = true;
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    expect(screen.getByTestId("mode")).toHaveTextContent("system");
    expect(screen.getByTestId("resolved")).toHaveTextContent("dark");
    expect(currentTheme()).toBe("dark");
  });

  it("reacts to OS preference changes while in system mode", () => {
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    expect(screen.getByTestId("resolved")).toHaveTextContent("light");
    act(() => {
      darkMatches = true;
      mediaListeners.forEach((listener) => listener());
    });
    expect(screen.getByTestId("resolved")).toHaveTextContent("dark");
    expect(currentTheme()).toBe("dark");
  });

  it("ignores OS preference changes once an explicit mode is chosen", () => {
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    fireEvent.click(screen.getByText("D"));
    act(() => {
      darkMatches = false;
      mediaListeners.forEach((listener) => listener());
    });
    expect(screen.getByTestId("resolved")).toHaveTextContent("dark");
  });
});