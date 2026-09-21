import { buildRoute, parseRoute, Route } from "./routes";

describe("parseRoute / buildRoute", () => {
  it.each([
    ["#/", { kind: "home" }],
    ["#/r/abc", { kind: "release", releaseId: "abc" }],
    ["#/r/abc/f/def", { kind: "feature", releaseId: "abc", featureId: "def" }],
    ["#/r/abc/f/def/s/developer", { kind: "stage", releaseId: "abc", featureId: "def", stageName: "developer" }],
  ] as [string, Route][])("round-trips %s", (hash, route) => {
    expect(parseRoute(hash)).toEqual(route);
    expect(buildRoute(route)).toBe(hash);
  });

  it("round-trips real GUIDs", () => {
    const rel = "de890ad0-3fdd-42fe-a7fc-b1fc01a8b59f";
    const feat = "d92f4d93-2f5e-41a2-bdd4-5057906873fe";
    const route: Route = { kind: "feature", releaseId: rel, featureId: feat };
    expect(parseRoute(buildRoute(route))).toEqual(route);
  });

  it.each(["", "#", "/", "#/nonsense", "#/r", "#/r/", "#/r/abc/f", "#/r/abc/x/def", "#/r/abc/f/def/s"])(
    "treats %j as the project home",
    (hash) => {
      expect(parseRoute(hash)).toEqual({ kind: "home" });
    },
  );

  it("accepts a hash without the leading #", () => {
    expect(parseRoute("/r/abc")).toEqual({ kind: "release", releaseId: "abc" });
  });

  it.each([
    "#/r/../../x",
    "#/r/a%2Fb",
    "#/r/a b",
    "#/r/<script>",
    "#/r/abc/f/../x",
    "#/r/abc/f/def/s/../../x",
    "#/r/abc/f/def/s/dev eloper",
  ])("rejects unsafe input %s instead of trusting it", (hash) => {
    expect(parseRoute(hash)).toEqual({ kind: "home" });
  });

  it("ignores trailing slashes and extra whitespace", () => {
    expect(parseRoute("#/r/abc/")).toEqual({ kind: "release", releaseId: "abc" });
    expect(parseRoute("  #/r/abc/f/def/  ")).toEqual({ kind: "feature", releaseId: "abc", featureId: "def" });
  });

  it("does not throw on garbage", () => {
    expect(() => parseRoute(undefined as unknown as string)).not.toThrow();
    expect(parseRoute(undefined as unknown as string)).toEqual({ kind: "home" });
  });
});
