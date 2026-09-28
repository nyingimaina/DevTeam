import BrokerApi from "./BrokerApi";
import { clearErrors, recentErrors } from "../../UI/diagnostics";

// A cancelled run comes back as 409 with a plain-language body; the user should see exactly
// that sentence, not a technical "Broker POST ... failed with 409" wrapper.
function stubFetch(ok: boolean, status: number, body: string, headers: Record<string, string> = {}) {
  const mock = jest.fn().mockResolvedValue({
    ok,
    status,
    headers: { get: (name: string) => headers[name] ?? null },
    text: async () => body,
    json: async () => JSON.parse(body),
  });
  global.fetch = mock as unknown as typeof fetch;
  return mock;
}

describe("BrokerApi error messages", () => {
  const originalFetch = global.fetch;

  beforeEach(() => {
    window.localStorage.clear();
    clearErrors();
  });

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
  });

  it("surfaces a plain-language broker error body as-is", async () => {
    stubFetch(false, 409, "The run was cancelled.");

    await expect(new BrokerApi().runStageAsync("f1")).rejects.toThrow("The run was cancelled.");
  });

  it("falls back to a technical message when the body isn't readable", async () => {
    stubFetch(false, 500, '{"error":"boom"}');

    await expect(new BrokerApi().runStageAsync("f1")).rejects.toThrow(/failed with 500/);
  });

  it("records a failed request for the support bundle, with its request reference", async () => {
    // The reference is what lets a user say "it failed, reference abc123" and a specialist find
    // exactly that request in the logs.
    stubFetch(false, 500, "Something went wrong.", { "X-Request-Id": "abc123def456" });

    await expect(new BrokerApi().runStageAsync("f1")).rejects.toThrow();

    const [entry] = recentErrors();
    expect(entry.message).toBe("Something went wrong.");
    expect(entry.status).toBe(500);
    expect(entry.requestId).toBe("abc123def456");
    expect(entry.url).toContain("/api/features/f1/run-stage");
    expect(entry.source).toBe("api");
  });
});

describe("createReleaseAsync request body", () => {
  const originalFetch = global.fetch;

  beforeEach(() => {
    window.localStorage.clear();
    clearErrors();
  });

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
  });

  // The broker's CreateReleaseRequest binds ReleaseKey. A body key of "featureKey" binds to
  // null, so the endpoint rejects the create with 400 "ReleaseKey is required." — and the field
  // name is invisible in the UI, since a release simply never appears.
  it("posts the key as releaseKey, the name the broker binds", async () => {
    const fetchMock = stubFetch(true, 200, JSON.stringify({ id: "r1" }));

    await new BrokerApi().createReleaseAsync("negation", "C:\\work\\proj");

    const [, init] = fetchMock.mock.calls[0];
    expect(JSON.parse(init.body as string)).toEqual({
      releaseKey: "negation",
      workspacePath: "C:\\work\\proj",
    });
  });
});
