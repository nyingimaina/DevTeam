import { useCallback, useEffect, useState } from "react";
import { buildRoute, parseRoute, Route } from "./routes";

function readRoute(): Route {
  return parseRoute(typeof window === "undefined" ? "" : window.location.hash);
}

/**
 * The current route, read from the URL hash. `active` says whether this screen's tab is the one
 * being shown: the Releases tab stays mounted while you visit Git/Settings, so it must never
 * write the address while hidden, and re-reads it the moment it becomes visible again.
 */
export function useHashRoute(active = true): [Route, (route: Route) => void] {
  const [route, setRoute] = useState<Route>(readRoute);

  useEffect(() => {
    const onChange = () => setRoute(readRoute());
    window.addEventListener("hashchange", onChange);
    return () => window.removeEventListener("hashchange", onChange);
  }, []);

  useEffect(() => {
    if (active) setRoute(readRoute());
  }, [active]);

  const navigate = useCallback((next: Route) => {
    if (!active) return;
    window.location.hash = buildRoute(next);
    setRoute(next);
  }, [active]);

  return [route, navigate];
}
