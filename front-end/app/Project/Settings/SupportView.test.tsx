import React from "react";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import SupportView from "./SupportView";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { clearErrors, recordError } from "../../UI/diagnostics";
import { saveBlob } from "../../UI/download";

jest.mock("../../Chat/Data/BrokerApi");
jest.mock("../../UI/download", () => ({ saveBlob: jest.fn() }));

const mockApi = jest.mocked(BrokerApi.prototype);
const mockSaveBlob = jest.mocked(saveBlob);

function renderSupport() {
  return render(<SupportView api={mockApi as unknown as BrokerApi} />);
}

beforeEach(() => {
  jest.clearAllMocks();
  window.localStorage.clear();
  clearErrors();
  mockApi.getDiagnosticsSettingsAsync.mockResolvedValue({
    verboseLogging: false,
    logsDirectory: "C:\\Users\\someone\\.devteam\\logs",
  });
  mockApi.setVerboseLoggingAsync.mockResolvedValue({
    verboseLogging: true,
    logsDirectory: "C:\\Users\\someone\\.devteam\\logs",
  });
  mockApi.collectDiagnosticsAsync.mockResolvedValue(new Blob(["zip"], { type: "application/zip" }));
  mockApi.revealLogsAsync.mockResolvedValue(undefined);
  mockApi.getNotificationSettingsAsync.mockResolvedValue({
    stageComplete: true,
    needsAttention: true,
    approvalNeeded: true,
    sound: true,
  });
  mockApi.setNotificationSettingsAsync.mockImplementation(async (settings) => ({
    stageComplete: true,
    needsAttention: true,
    approvalNeeded: true,
    sound: true,
    ...settings,
  }));
});

describe("SupportView", () => {
  it("tells the user what to do, in order", async () => {
    renderSupport();

    expect(screen.getByTestId("support-view")).toHaveTextContent(/turn on detailed logging/i);
    expect(screen.getByTestId("support-view")).toHaveTextContent(/collect a file/i);
    await waitFor(() => expect(screen.getByTestId("support-logs-path")).toBeInTheDocument());
  });

  it("turns detailed logging on", async () => {
    renderSupport();
    const toggle = await screen.findByTestId("support-verbose-toggle");

    await userEvent.click(toggle);

    expect(mockApi.setVerboseLoggingAsync).toHaveBeenCalledWith(true);
    await waitFor(() => expect(screen.getByTestId("support-notice")).toHaveTextContent(/repeat the problem/i));
  });

  it("collects the diagnostics file and saves it for the user", async () => {
    renderSupport();

    await userEvent.click(await screen.findByTestId("support-collect-btn"));

    expect(mockApi.collectDiagnosticsAsync).toHaveBeenCalled();
    await waitFor(() => expect(mockSaveBlob).toHaveBeenCalled());
    const [, fileName] = mockSaveBlob.mock.calls[0];
    expect(fileName).toMatch(/^devteam-diagnostics-.*\.zip$/);
    expect(screen.getByTestId("support-notice")).toHaveTextContent(/send that file/i);
  });

  it("can open the logs folder for a user who wants to look themselves", async () => {
    renderSupport();

    await userEvent.click(await screen.findByTestId("support-open-logs-btn"));

    expect(mockApi.revealLogsAsync).toHaveBeenCalled();
  });

  it("shows how many problems were recorded, and can clear them", async () => {
    recordError({ source: "api", message: "Failed to load release", status: 500, requestId: "abc123" });

    renderSupport();

    await waitFor(() => expect(screen.getByTestId("support-recorded")).toHaveTextContent(/1 recent problem/));

    await userEvent.click(screen.getByTestId("support-clear-btn"));

    expect(screen.getByTestId("support-recorded")).toHaveTextContent(/No problems recorded/i);
    expect(screen.queryByTestId("support-clear-btn")).not.toBeInTheDocument();
  });

  it("explains a failure plainly instead of showing nothing", async () => {
    mockApi.collectDiagnosticsAsync.mockRejectedValue(new Error("Could not collect the diagnostics file (HTTP 500)."));

    renderSupport();
    await userEvent.click(await screen.findByTestId("support-collect-btn"));

    await waitFor(() => expect(screen.getByTestId("support-error")).toHaveTextContent(/HTTP 500/));
  });

  it("lets the user choose which events raise a desktop notification", async () => {
    renderSupport();

    const toggles = await screen.findByTestId("support-notifications");
    expect(toggles).toHaveTextContent(/no window of its own/i);

    await userEvent.click(within(toggles).getByTestId("support-notify-stageComplete"));

    await waitFor(() =>
      expect(mockApi.setNotificationSettingsAsync).toHaveBeenCalledWith({ stageComplete: false }),
    );
  });
});
