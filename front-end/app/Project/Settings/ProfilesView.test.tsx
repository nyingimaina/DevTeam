import React from "react";
import { render, screen, waitFor, within, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import ProfilesView from "./ProfilesView";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ProfileDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");

const mockApi = jest.mocked(BrokerApi.prototype);

function makeProfile(overrides: Partial<ProfileDto> = {}): ProfileDto {
  return {
    id: "p1",
    name: "Terse",
    description: "Short answers",
    isDefault: true,
    prompts: [
      { stageName: "business-analyst", promptText: "Be brief.", overridesBuiltInPrompt: false },
      { stageName: "developer", promptText: "", overridesBuiltInPrompt: false },
      { stageName: "qa", promptText: "", overridesBuiltInPrompt: false },
    ],
    ...overrides,
  };
}

async function clickRow(user: ReturnType<typeof userEvent.setup>, name: string) {
  const table = await screen.findByRole("table");
  await user.click(within(table).getByRole("row", { name: new RegExp(name) }));
}

// The stage/workspace pickers are react-select (via SelectWrapper), not native <select>s —
// open the dropdown and click the option's text, mirroring SelectWrapper's own test suite.
function pickReactSelectOption(pickerWrapper: HTMLElement, optionText: string) {
  const control = pickerWrapper.querySelector(".react-select__control");
  fireEvent.mouseDown(control!);
  fireEvent.click(within(pickerWrapper).getByText(optionText));
}

describe("ProfilesView", () => {
  function makeRole(name: string) {
    return {
      name, writesCode: false, signoff: null, userInputRequired: false,
      stepSummary: [], entryGates: [], exitGatePrompts: [], artifact: null,
    };
  }

  beforeEach(() => {
    jest.clearAllMocks();
    mockApi.getWorkspaceProfileAsync.mockResolvedValue({ profileId: null });
    mockApi.getWorkspacePipelineAsync.mockResolvedValue({
      roles: ["business-analyst", "developer", "qa"].map(makeRole),
    });
  });

  it("lists profiles with a default indicator", async () => {
    mockApi.getProfilesAsync.mockResolvedValue([makeProfile()]);
    render(<ProfilesView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    const table = await screen.findByRole("table");
    expect(within(table).getByText("Terse")).toBeInTheDocument();
    expect(within(table).getByText("Short answers")).toBeInTheDocument();
    expect(within(table).getByText("✓ Default")).toBeInTheDocument();
  });

  it("opens the editor with the profile's stage prompt when a row is clicked", async () => {
    mockApi.getProfilesAsync.mockResolvedValue([makeProfile()]);
    const user = userEvent.setup();
    render(<ProfilesView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    await clickRow(user, "Terse");

    const editor = await screen.findByTestId("profile-editor");
    expect(within(editor).getByDisplayValue("Terse")).toBeInTheDocument();
    expect(within(editor).getByDisplayValue("Be brief.")).toBeInTheDocument();
  });

  it("switches which stage's prompt is shown when a different stage is picked", async () => {
    mockApi.getProfilesAsync.mockResolvedValue([makeProfile()]);
    const user = userEvent.setup();
    render(<ProfilesView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    await clickRow(user, "Terse");
    const editor = await screen.findByTestId("profile-editor");

    pickReactSelectOption(within(editor).getByTestId("profile-stage-picker"), "developer");
    await waitFor(() => expect(within(editor).getByTestId("profile-prompt-textarea")).toHaveValue(""));
  });

  it("saves edited name, description, and prompt text", async () => {
    const profile = makeProfile();
    mockApi.getProfilesAsync.mockResolvedValue([profile]);
    mockApi.updateProfileAsync.mockResolvedValue({ ...profile, name: "Renamed" });
    const user = userEvent.setup();
    render(<ProfilesView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    await clickRow(user, "Terse");
    const editor = await screen.findByTestId("profile-editor");

    const nameField = within(editor).getByTestId("profile-name-input");
    await user.clear(nameField);
    await user.type(nameField, "Renamed");

    const promptField = within(editor).getByTestId("profile-prompt-textarea");
    await user.clear(promptField);
    await user.type(promptField, "Updated guidance.");

    await user.click(within(editor).getByTestId("profile-save-btn"));

    await waitFor(() => {
      expect(mockApi.updateProfileAsync).toHaveBeenCalledWith(
        "p1",
        "Renamed",
        "Short answers",
        expect.arrayContaining([
          { stageName: "business-analyst", promptText: "Updated guidance.", overridesBuiltInPrompt: false },
        ]),
      );
    });
  });

  it("saves the override checkbox alongside the prompt text", async () => {
    const profile = makeProfile();
    mockApi.getProfilesAsync.mockResolvedValue([profile]);
    mockApi.updateProfileAsync.mockResolvedValue(profile);
    const user = userEvent.setup();
    render(<ProfilesView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    await clickRow(user, "Terse");
    const editor = await screen.findByTestId("profile-editor");

    await user.click(within(editor).getByTestId("profile-override-checkbox"));
    await user.click(within(editor).getByTestId("profile-save-btn"));

    await waitFor(() => {
      expect(mockApi.updateProfileAsync).toHaveBeenCalledWith(
        "p1",
        "Terse",
        "Short answers",
        expect.arrayContaining([
          { stageName: "business-analyst", promptText: "Be brief.", overridesBuiltInPrompt: true },
        ]),
      );
    });
  });

  it("shows a warning when the override checkbox is checked", async () => {
    mockApi.getProfilesAsync.mockResolvedValue([makeProfile()]);
    const user = userEvent.setup();
    render(<ProfilesView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    await clickRow(user, "Terse");
    const editor = await screen.findByTestId("profile-editor");

    expect(within(editor).queryByText(/replaces the entire built-in prompt/i)).not.toBeInTheDocument();
    await user.click(within(editor).getByTestId("profile-override-checkbox"));
    expect(within(editor).getByText(/replaces the entire built-in prompt/i)).toBeInTheDocument();
  });

  it("lets a custom pipeline stage (not business-analyst/developer/qa) be picked and given a prompt", async () => {
    mockApi.getWorkspacePipelineAsync.mockResolvedValue({ roles: [makeRole("code-map")] });
    const profile = makeProfile({ prompts: [] });
    mockApi.getProfilesAsync.mockResolvedValue([profile]);
    mockApi.updateProfileAsync.mockResolvedValue(profile);
    const user = userEvent.setup();
    render(<ProfilesView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    await clickRow(user, "Terse");
    const editor = await screen.findByTestId("profile-editor");

    expect(within(editor).getByTestId("profile-stage-picker")).toHaveTextContent("code-map");

    const promptField = within(editor).getByTestId("profile-prompt-textarea");
    await user.type(promptField, "Scan the codebase.");
    await user.click(within(editor).getByTestId("profile-save-btn"));

    await waitFor(() => expect(mockApi.updateProfileAsync).toHaveBeenCalledWith(
      "p1", "Terse", "Short answers",
      [{ stageName: "code-map", promptText: "Scan the codebase.", overridesBuiltInPrompt: false }],
    ));
  });

  it("suggests known placeholders in the profile prompt field", async () => {
    mockApi.getWorkspacePipelineAsync.mockResolvedValue({
      roles: [{ ...makeRole("code-map"), artifact: { root: "docs-root", fileName: "code-map.json", kind: "json" } }],
    });
    mockApi.getProfilesAsync.mockResolvedValue([makeProfile({ prompts: [] })]);
    const user = userEvent.setup();
    render(<ProfilesView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    await clickRow(user, "Terse");
    const editor = await screen.findByTestId("profile-editor");
    const promptField = within(editor).getByTestId("profile-prompt-textarea") as HTMLTextAreaElement;

    fireEvent.change(promptField, { target: { value: "Read <code" } });
    const suggestions = within(editor).getByTestId("profile-prompt-textarea-suggestions");
    fireEvent.mouseDown(within(suggestions).getByText("<code-map/artifact.file>"));

    expect(promptField.value).toBe("Read <code-map/artifact.file>");
  });

  it("creates a new profile", async () => {
    mockApi.getProfilesAsync.mockResolvedValue([]);
    mockApi.createProfileAsync.mockResolvedValue(makeProfile({ id: "p2", name: "New One", description: null }));
    const user = userEvent.setup();
    render(<ProfilesView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    await user.type(await screen.findByTestId("new-profile-name-input"), "New One");
    await user.click(screen.getByTestId("new-profile-create-btn"));

    await waitFor(() => expect(mockApi.createProfileAsync).toHaveBeenCalledWith("New One", null));
  });

  it("deletes a profile", async () => {
    mockApi.getProfilesAsync.mockResolvedValue([makeProfile()]);
    mockApi.deleteProfileAsync.mockResolvedValue({ ok: true });
    const user = userEvent.setup();
    render(<ProfilesView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    await screen.findByRole("table");
    await user.click(screen.getByTestId("profile-delete-p1"));

    await waitFor(() => expect(mockApi.deleteProfileAsync).toHaveBeenCalledWith("p1"));
  });

  it("sets a non-default profile as default", async () => {
    const other = makeProfile({ id: "p2", name: "Other", isDefault: false });
    mockApi.getProfilesAsync.mockResolvedValue([makeProfile(), other]);
    mockApi.setDefaultProfileAsync.mockResolvedValue({ ...other, isDefault: true });
    const user = userEvent.setup();
    render(<ProfilesView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    await screen.findByRole("table");
    await user.click(screen.getByTestId("profile-set-default-p2"));

    await waitFor(() => expect(mockApi.setDefaultProfileAsync).toHaveBeenCalledWith("p2"));
  });

  it("shows and updates the active profile for this workspace", async () => {
    const profile = makeProfile();
    const other = makeProfile({ id: "p2", name: "Other", isDefault: false });
    mockApi.getProfilesAsync.mockResolvedValue([profile, other]);
    mockApi.getWorkspaceProfileAsync.mockResolvedValue({ profileId: "p1" });
    mockApi.setWorkspaceProfileAsync.mockResolvedValue({ profileId: "p2" });
    render(<ProfilesView api={mockApi as unknown as BrokerApi} workspacePath="C:/work/proj" />);

    const picker = await screen.findByTestId("workspace-profile-picker");
    await waitFor(() => expect(within(picker).getByText("Terse")).toBeInTheDocument());

    pickReactSelectOption(picker, "Other");

    await waitFor(() =>
      expect(mockApi.setWorkspaceProfileAsync).toHaveBeenCalledWith("C:/work/proj", "p2"),
    );
  });
});
