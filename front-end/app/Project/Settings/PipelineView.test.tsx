import React from "react";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import PipelineView from "./PipelineView";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { PipelineEditorDto, PipelineEditorRoleDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");

const mockApi = jest.mocked(BrokerApi.prototype);

function makeRole(overrides: Partial<PipelineEditorRoleDto> = {}): PipelineEditorRoleDto {
  return {
    name: "developer",
    writesCode: true,
    signoff: "pr-created",
    userInputRequired: false,
    stepSummary: ["context_bundle", "agent:developer", "verify_code", "code_hygiene"],
    entryGates: [],
    exitGatePrompts: [],
    ...overrides,
  };
}

function makePipeline(roles: PipelineEditorRoleDto[]): PipelineEditorDto {
  return { roles };
}

describe("PipelineView", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("lists stages in order with their step summary", async () => {
    mockApi.getWorkspacePipelineAsync.mockResolvedValue(makePipeline([
      makeRole({ name: "business-analyst", writesCode: false, stepSummary: ["scaffold_specs", "agent:business-analyst"] }),
      makeRole({ name: "developer" }),
      makeRole({ name: "qa", stepSummary: ["agent:qa", "coverage_matrix"] }),
    ]));
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    await screen.findByTestId("pipeline-role-qa");
    const roles = screen.getAllByTestId(/^pipeline-role-/).filter((el) => el.tagName === "LI");
    expect(roles.map((r) => r.getAttribute("data-testid"))).toEqual([
      "pipeline-role-business-analyst",
      "pipeline-role-developer",
      "pipeline-role-qa",
    ]);
    expect(screen.getByTestId("pipeline-role-qa-steps")).toHaveTextContent("agent:qa → coverage_matrix");
  });

  it("moves a stage up and down", async () => {
    mockApi.getWorkspacePipelineAsync.mockResolvedValue(makePipeline([
      makeRole({ name: "business-analyst" }),
      makeRole({ name: "developer" }),
      makeRole({ name: "qa" }),
    ]));
    const user = userEvent.setup();
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);
    await screen.findByTestId("pipeline-role-developer");

    await user.click(screen.getByTestId("pipeline-role-qa-up"));

    await waitFor(() => expect(screen.getAllByTestId(/^pipeline-role-/).filter((el) => el.tagName === "LI")[1])
      .toHaveAttribute("data-testid", "pipeline-role-qa"));
    const rolesAfter = screen.getAllByTestId(/^pipeline-role-/).filter((el) => el.tagName === "LI");
    expect(rolesAfter.map((r) => r.getAttribute("data-testid"))).toEqual([
      "pipeline-role-business-analyst",
      "pipeline-role-qa",
      "pipeline-role-developer",
    ]);
  });

  it("removes a stage", async () => {
    mockApi.getWorkspacePipelineAsync.mockResolvedValue(makePipeline([
      makeRole({ name: "business-analyst" }),
      makeRole({ name: "developer" }),
    ]));
    const user = userEvent.setup();
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);
    await screen.findByTestId("pipeline-role-developer");

    await user.click(screen.getByTestId("pipeline-role-developer-remove"));

    await waitFor(() => expect(screen.queryByTestId("pipeline-role-developer")).not.toBeInTheDocument());
    expect(screen.getByTestId("pipeline-role-business-analyst")).toBeInTheDocument();
  });

  it("adds a new stage with a plain agent-turn step summary", async () => {
    mockApi.getWorkspacePipelineAsync.mockResolvedValue(makePipeline([makeRole({ name: "developer" })]));
    const user = userEvent.setup();
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);
    await screen.findByTestId("pipeline-role-developer");

    await user.type(screen.getByTestId("pipeline-new-name-input"), "researcher");
    await user.click(screen.getByTestId("pipeline-add-btn"));

    const added = await screen.findByTestId("pipeline-role-researcher");
    expect(within(added).getByTestId("pipeline-role-researcher-steps")).toHaveTextContent("none yet");
  });

  it("toggles writesCode and saves, round-tripping through the pipeline API", async () => {
    const role = makeRole({ name: "developer", writesCode: false });
    mockApi.getWorkspacePipelineAsync.mockResolvedValue(makePipeline([role]));
    mockApi.saveWorkspacePipelineAsync.mockResolvedValue(makePipeline([{ ...role, writesCode: true }]));
    const user = userEvent.setup();
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);
    await screen.findByTestId("pipeline-role-developer");

    await user.click(screen.getByTestId("pipeline-role-developer-writescode"));
    await user.click(screen.getByTestId("pipeline-save-btn"));

    await waitFor(() => expect(mockApi.saveWorkspacePipelineAsync).toHaveBeenCalledWith(
      "C:/work/proj",
      [expect.objectContaining({ name: "developer", writesCode: true })],
    ));
    expect(screen.getByTestId("pipeline-role-developer-writescode")).toBeChecked();
  });

  it("adds an exit gate prompt with a responsible role and includes it in the saved payload", async () => {
    const role = makeRole({ name: "qa" });
    mockApi.getWorkspacePipelineAsync.mockResolvedValue(makePipeline([role]));
    mockApi.saveWorkspacePipelineAsync.mockResolvedValue(makePipeline([role]));
    const user = userEvent.setup();
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);
    await screen.findByTestId("pipeline-role-qa");

    await user.type(screen.getByTestId("pipeline-role-qa-exit-new-text"), "Check for accessibility issues.");
    await user.click(screen.getByTestId("pipeline-role-qa-exit-add-btn"));
    await user.type(screen.getByTestId("pipeline-role-qa-exit-0-responsible-role"), "developer");

    await user.click(screen.getByTestId("pipeline-save-btn"));

    await waitFor(() => expect(mockApi.saveWorkspacePipelineAsync).toHaveBeenCalledWith(
      "C:/work/proj",
      [expect.objectContaining({
        exitGatePrompts: [{ kind: "gatePrompt", gatePromptText: "Check for accessibility issues.", responsibleRole: "developer" }],
      })],
    ));
  });

  it("adds a requires-specialist entry gate and includes it in the saved payload", async () => {
    const role = makeRole({ name: "developer" });
    mockApi.getWorkspacePipelineAsync.mockResolvedValue(makePipeline([role]));
    mockApi.saveWorkspacePipelineAsync.mockResolvedValue(makePipeline([role]));
    const user = userEvent.setup();
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);
    await screen.findByTestId("pipeline-role-developer");

    await user.type(screen.getByTestId("pipeline-role-developer-entry-specialist-name"), "database-admin");
    await user.click(screen.getByTestId("pipeline-role-developer-entry-specialist-add-btn"));

    expect(screen.getByTestId("pipeline-role-developer-entry")).toHaveTextContent("specialist: database-admin");

    await user.click(screen.getByTestId("pipeline-save-btn"));

    await waitFor(() => expect(mockApi.saveWorkspacePipelineAsync).toHaveBeenCalledWith(
      "C:/work/proj",
      [expect.objectContaining({
        entryGates: [{ kind: "requiresSpecialist", requiredSpecialist: "database-admin", responsibleRole: null }],
      })],
    ));
  });

  it("shows an error message if loading the pipeline fails", async () => {
    mockApi.getWorkspacePipelineAsync.mockRejectedValue(new Error("workspace not found"));
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    expect(await screen.findByText("workspace not found")).toBeInTheDocument();
  });
});
