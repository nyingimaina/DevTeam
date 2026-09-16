import React from "react";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import SpecialistsView from "./SpecialistsView";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { SpecialistRoleDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");

const mockApi = jest.mocked(BrokerApi.prototype);

function makeSpecialist(overrides: Partial<SpecialistRoleDto> = {}): SpecialistRoleDto {
  return {
    id: "s1",
    name: "database-admin",
    description: "Knows how to prepare a safe production migration.",
    primingPrompt: "You are a database administrator...",
    writesCode: false,
    ...overrides,
  };
}

async function clickRow(user: ReturnType<typeof userEvent.setup>, name: string) {
  const table = await screen.findByRole("table");
  await user.click(within(table).getByRole("row", { name: new RegExp(name) }));
}

describe("SpecialistsView", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("lists specialists with their description", async () => {
    mockApi.getSpecialistsAsync.mockResolvedValue([makeSpecialist()]);
    render(<SpecialistsView api={mockApi as unknown as BrokerApi} />);

    const table = await screen.findByRole("table");
    expect(within(table).getByText("database-admin")).toBeInTheDocument();
    expect(within(table).getByText("Knows how to prepare a safe production migration.")).toBeInTheDocument();
  });

  it("opens the editor with the specialist's fields when a row is clicked", async () => {
    mockApi.getSpecialistsAsync.mockResolvedValue([makeSpecialist()]);
    const user = userEvent.setup();
    render(<SpecialistsView api={mockApi as unknown as BrokerApi} />);

    await clickRow(user, "database-admin");

    const editor = await screen.findByTestId("specialist-editor");
    expect(within(editor).getByDisplayValue("database-admin")).toBeInTheDocument();
    expect(within(editor).getByDisplayValue("You are a database administrator...")).toBeInTheDocument();
    expect(within(editor).getByTestId("specialist-writescode-checkbox")).not.toBeChecked();
  });

  it("saves edited fields", async () => {
    const specialist = makeSpecialist();
    mockApi.getSpecialistsAsync.mockResolvedValue([specialist]);
    mockApi.updateSpecialistAsync.mockResolvedValue({ ...specialist, description: "Updated.", writesCode: true });
    const user = userEvent.setup();
    render(<SpecialistsView api={mockApi as unknown as BrokerApi} />);

    await clickRow(user, "database-admin");
    const editor = await screen.findByTestId("specialist-editor");

    const descriptionField = within(editor).getByTestId("specialist-description-input");
    await user.clear(descriptionField);
    await user.type(descriptionField, "Updated.");
    await user.click(within(editor).getByTestId("specialist-writescode-checkbox"));

    await user.click(within(editor).getByTestId("specialist-save-btn"));

    await waitFor(() => expect(mockApi.updateSpecialistAsync).toHaveBeenCalledWith(
      "s1", "database-admin", "Updated.", "You are a database administrator...", true,
    ));
  });

  it("creates a new specialist and opens it for editing", async () => {
    const created = makeSpecialist({ id: "s2", name: "network-engineer", description: "", primingPrompt: "" });
    mockApi.getSpecialistsAsync.mockResolvedValueOnce([]).mockResolvedValueOnce([created]);
    mockApi.createSpecialistAsync.mockResolvedValue(created);
    const user = userEvent.setup();
    render(<SpecialistsView api={mockApi as unknown as BrokerApi} />);

    await user.type(screen.getByTestId("new-specialist-name-input"), "network-engineer");
    await user.click(screen.getByTestId("new-specialist-create-btn"));

    await waitFor(() => expect(mockApi.createSpecialistAsync).toHaveBeenCalledWith("network-engineer", "", "", false));
    expect(await screen.findByTestId("specialist-editor")).toBeInTheDocument();
  });

  it("deletes a specialist", async () => {
    mockApi.getSpecialistsAsync.mockResolvedValueOnce([makeSpecialist()]).mockResolvedValueOnce([]);
    mockApi.deleteSpecialistAsync.mockResolvedValue({ ok: true });
    const user = userEvent.setup();
    render(<SpecialistsView api={mockApi as unknown as BrokerApi} />);

    await screen.findByText("database-admin");
    await user.click(screen.getByTestId("specialist-delete-s1"));

    await waitFor(() => expect(mockApi.deleteSpecialistAsync).toHaveBeenCalledWith("s1"));
  });
});
