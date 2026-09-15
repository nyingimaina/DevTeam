import { renderHook, waitFor, act } from "@testing-library/react";
import { usePoller } from "./usePoller";

describe("usePoller", () => {
  beforeEach(() => {
    jest.useRealTimers();
  });

  it("starts polling on mount and delivers results via onResult", async () => {
    const func = jest.fn().mockResolvedValue("v1");
    const onResult = jest.fn();

    renderHook(() =>
      usePoller({
        enabled: true,
        func,
        onResult,
        pollIntervalMilliseconds: 10,
        deps: [],
      }),
    );

    await waitFor(() => expect(func).toHaveBeenCalled());
    await waitFor(() => expect(onResult).toHaveBeenCalledWith("v1", true));
  });

  it("stops polling on unmount", async () => {
    const func = jest.fn().mockResolvedValue("v1");

    const { unmount } = renderHook(() =>
      usePoller({
        enabled: true,
        func,
        pollIntervalMilliseconds: 10,
        deps: [],
      }),
    );

    await waitFor(() => expect(func).toHaveBeenCalled());
    const callsAtUnmount = func.mock.calls.length;
    unmount();

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(func.mock.calls.length).toBe(callsAtUnmount);
  });

  it("does not poll when disabled", async () => {
    const func = jest.fn().mockResolvedValue("v1");

    renderHook(() =>
      usePoller({
        enabled: false,
        func,
        pollIntervalMilliseconds: 10,
        deps: [],
      }),
    );

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(func).not.toHaveBeenCalled();
  });

  it("pollNow() forces an immediate off-schedule call", async () => {
    const func = jest.fn().mockResolvedValue("v1");

    const { result } = renderHook(() =>
      usePoller({
        enabled: true,
        func,
        pollIntervalMilliseconds: 60000,
        deps: [],
      }),
    );

    await waitFor(() => expect(func).toHaveBeenCalledTimes(1));

    act(() => {
      result.current.pollNow();
    });

    await waitFor(() => expect(func).toHaveBeenCalledTimes(2));
  });

  it("keeps retrying across many consecutive failures and resumes once func succeeds again (self-healing across an outage, since maxConsecutiveErrors is never set)", async () => {
    let shouldFail = true;
    const func = jest.fn().mockImplementation(() => {
      if (shouldFail) return Promise.reject(new Error("backend down"));
      return Promise.resolve("recovered");
    });
    const onResult = jest.fn();
    const onError = jest.fn();

    renderHook(() =>
      usePoller({
        enabled: true,
        func,
        onResult,
        onError,
        pollIntervalMilliseconds: 5,
        maxIntervalMilliseconds: 20,
        deps: [],
      }),
    );

    await waitFor(() => expect(onError.mock.calls.length).toBeGreaterThanOrEqual(5));

    shouldFail = false;

    await waitFor(() => expect(onResult).toHaveBeenCalledWith("recovered", true));
  });

  it("restarts the poller when a dependency changes", async () => {
    const func = jest.fn().mockResolvedValue("v1");

    const { rerender } = renderHook(
      ({ id }: { id: string }) =>
        usePoller({
          enabled: true,
          func: () => func(id),
          pollIntervalMilliseconds: 10,
          deps: [id],
        }),
      { initialProps: { id: "a" } },
    );

    await waitFor(() => expect(func).toHaveBeenCalledWith("a"));

    rerender({ id: "b" });

    await waitFor(() => expect(func).toHaveBeenCalledWith("b"));
  });
});
