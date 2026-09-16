import React from "react";
import { render, screen, waitFor, within, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import PipelineView from "./PipelineView";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { PipelineEditorDto, PipelineEditorRoleDto, SpecialistRoleDto } from "../../Chat/Data/BrokerTypes";

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
    artifact: null,
    ...overrides,
  };
}

function makePipeline(roles: PipelineEditorRoleDto[]): PipelineEditorDto {
  return { roles };
}

function makeSpecialist(overrides: Partial<SpecialistRoleDto> = {}): SpecialistRoleDto {
  return { id: "s1", name: "database-admin", description: "DB expert", primingPrompt: "You are a DBA.", writesCode: false, ...overrides };
}

// The role/specialist pickers are react-select (via SelectWrapper), not native <select>s — open
// the dropdown and click the option's text, mirroring SelectWrapper's own test suite (see
// ProfilesView.test.tsx for the same convention).
function pickReactSelectOption(pickerWrapper: HTMLElement, optionText: string) {
  const control = pickerWrapper.querySelector(".react-select__control");
  fireEvent.mouseDown(control!);
  fireEvent.click(within(pickerWrapper).getByText(optionText));
}

describe("PipelineView", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockApi.getSpecialistsAsync.mockResolvedValue([]);
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

  it("adds a new stage from a typed title, auto-generating its key", async () => {
    mockApi.getWorkspacePipelineAsync.mockResolvedValue(makePipeline([makeRole({ name: "developer" })]));
    const user = userEvent.setup();
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);
    await screen.findByTestId("pipeline-role-developer");

    await user.type(screen.getByTestId("pipeline-new-name-input"), "Code Mapping");
    expect(screen.getByTestId("pipeline-new-key-preview")).toHaveTextContent("code-mapping");
    await user.click(screen.getByTestId("pipeline-add-btn"));

    const added = await screen.findByTestId("pipeline-role-code-mapping");
    expect(within(added).getByTestId("pipeline-role-code-mapping-steps")).toHaveTextContent("none yet");
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

  it("adds an exit gate prompt, picks a responsible role, and includes both in the saved payload", async () => {
    const role = makeRole({ name: "qa" });
    const developer = makeRole({ name: "developer" });
    mockApi.getWorkspacePipelineAsync.mockResolvedValue(makePipeline([developer, role]));
    mockApi.saveWorkspacePipelineAsync.mockResolvedValue(makePipeline([developer, role]));
    const user = userEvent.setup();
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);
    await screen.findByTestId("pipeline-role-qa");

    await user.type(screen.getByTestId("pipeline-role-qa-exit-new-text"), "Check for accessibility issues.");
    await user.click(screen.getByTestId("pipeline-role-qa-exit-add-btn"));
    pickReactSelectOption(screen.getByTestId("pipeline-role-qa-exit-0-responsible-role"), "Developer");

    await user.click(screen.getByTestId("pipeline-save-btn"));

    await waitFor(() => expect(mockApi.saveWorkspacePipelineAsync).toHaveBeenCalledWith(
      "C:/work/proj",
      expect.arrayContaining([expect.objectContaining({
        name: "qa",
        exitGatePrompts: [{ kind: "gatePrompt", gatePromptText: "Check for accessibility issues.", responsibleRole: "developer" }],
      })]),
    ));
  });

  it("suggests known placeholders when typing < and inserts the selected token", async () => {
    const role = makeRole({ name: "qa" });
    mockApi.getWorkspacePipelineAsync.mockResolvedValue(makePipeline([role]));
    mockApi.saveWorkspacePipelineAsync.mockResolvedValue(makePipeline([role]));
    const user = userEvent.setup();
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);
    await screen.findByTestId("pipeline-role-qa");

    const input = screen.getByTestId("pipeline-role-qa-exit-new-text") as HTMLInputElement;
    fireEvent.change(input, { target: { value: "Confirm <F" } });

    const suggestions = screen.getByTestId("pipeline-role-qa-exit-new-text-suggestions");
    expect(within(suggestions).getByText("<F>")).toBeInTheDocument();
    fireEvent.mouseDown(within(suggestions).getByText("<F>"));

    expect(input.value).toBe("Confirm <F>");

    await user.click(screen.getByTestId("pipeline-role-qa-exit-add-btn"));
    await user.click(screen.getByTestId("pipeline-save-btn"));

    await waitFor(() => expect(mockApi.saveWorkspacePipelineAsync).toHaveBeenCalledWith(
      "C:/work/proj",
      [expect.objectContaining({
        exitGatePrompts: [expect.objectContaining({ gatePromptText: "Confirm <F>" })],
      })],
    ));
  });

  it("browses every known placeholder via the Insert placeholder button, for a novice who doesn't know the < convention", async () => {
    const codeMap = makeRole({ name: "code-map", writesCode: false, artifact: { root: "docs-root", fileName: "code-map.json", kind: "json" } });
    const qa = makeRole({ name: "qa" });
    mockApi.getWorkspacePipelineAsync.mockResolvedValue(makePipeline([codeMap, qa]));
    const user = userEvent.setup();
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);
    await screen.findByTestId("pipeline-role-qa");

    await user.click(screen.getByTestId("pipeline-role-qa-exit-new-text-browse-btn"));

    const suggestions = screen.getByTestId("pipeline-role-qa-exit-new-text-suggestions");
    expect(within(suggestions).getByText("<F>")).toBeInTheDocument();
    expect(within(suggestions).getByText("<docs-root>")).toBeInTheDocument();
    expect(within(suggestions).getByText("<code-map/artifact.file>")).toBeInTheDocument();

    fireEvent.mouseDown(within(suggestions).getByText("<code-map/artifact.file>"));

    expect((screen.getByTestId("pipeline-role-qa-exit-new-text") as HTMLInputElement).value).toBe("<code-map/artifact.file>");
  });

  it("picks a required specialist from the registry and includes it in the saved payload", async () => {
    const role = makeRole({ name: "developer" });
    mockApi.getWorkspacePipelineAsync.mockResolvedValue(makePipeline([role]));
    mockApi.getSpecialistsAsync.mockResolvedValue([makeSpecialist({ name: "database-admin" })]);
    mockApi.saveWorkspacePipelineAsync.mockResolvedValue(makePipeline([role]));
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);
    await screen.findByTestId("pipeline-role-developer");

    pickReactSelectOption(screen.getByTestId("pipeline-role-developer-entry-specialist-picker"), "database-admin");

    expect(screen.getByTestId("pipeline-role-developer-entry")).toHaveTextContent("specialist: database-admin");

    const user = userEvent.setup();
    await user.click(screen.getByTestId("pipeline-save-btn"));

    await waitFor(() => expect(mockApi.saveWorkspacePipelineAsync).toHaveBeenCalledWith(
      "C:/work/proj",
      [expect.objectContaining({
        entryGates: [{ kind: "requiresSpecialist", requiredSpecialist: "database-admin", responsibleRole: null }],
      })],
    ));
  });

  it("declares an artifact for a stage and requires it from another stage via pickers", async () => {
    const codeMap = makeRole({ name: "code-map", writesCode: false, stepSummary: ["agent:code-map"] });
    const businessAnalyst = makeRole({ name: "business-analyst", writesCode: false, stepSummary: ["agent:business-analyst"] });
    mockApi.getWorkspacePipelineAsync.mockResolvedValue(makePipeline([codeMap, businessAnalyst]));
    mockApi.saveWorkspacePipelineAsync.mockResolvedValue(makePipeline([codeMap, businessAnalyst]));
    const user = userEvent.setup();
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);
    await screen.findByTestId("pipeline-role-code-map");

    await user.click(screen.getByTestId("pipeline-role-code-map-artifact-toggle"));
    // "docs-root" is already the default once the toggle is on — no need to re-pick it.
    // Filename is derived — no free-text field to type into — and updates live with the
    // JSON toggle, since kind determines the extension.
    expect(screen.getByTestId("pipeline-role-code-map-artifact-filename")).toHaveTextContent("File: code-map.md");
    await user.click(screen.getByTestId("pipeline-role-code-map-artifact-json-toggle"));
    expect(screen.getByTestId("pipeline-role-code-map-artifact-filename")).toHaveTextContent("File: code-map.json");

    pickReactSelectOption(screen.getByTestId("pipeline-role-business-analyst-entry-artifact-picker"), "Code MAP");
    expect(screen.getByTestId("pipeline-role-business-analyst-entry")).toHaveTextContent("artifact from: code-map");

    await user.click(screen.getByTestId("pipeline-save-btn"));

    await waitFor(() => expect(mockApi.saveWorkspacePipelineAsync).toHaveBeenCalledWith(
      "C:/work/proj",
      expect.arrayContaining([
        expect.objectContaining({
          name: "code-map",
          artifact: { root: "docs-root", fileName: "code-map.json", kind: "json" },
        }),
        expect.objectContaining({
          name: "business-analyst",
          entryGates: [{ kind: "requiresArtifact", requiredArtifactStage: "code-map", responsibleRole: null }],
        }),
      ]),
    ));
  });

  it("shows an error message if loading the pipeline fails", async () => {
    mockApi.getWorkspacePipelineAsync.mockRejectedValue(new Error("workspace not found"));
    render(<PipelineView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    expect(await screen.findByText("workspace not found")).toBeInTheDocument();
  });
});
