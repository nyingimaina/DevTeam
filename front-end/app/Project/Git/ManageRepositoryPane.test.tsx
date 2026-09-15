import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import ManageRepositoryPane from "./ManageRepositoryPane";
import BrokerApi from "../../Chat/Data/BrokerApi";

jest.mock("../../Chat/Data/BrokerApi");

const mockApi = jest.mocked(BrokerApi.prototype);

describe("ManageRepositoryPane", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockApi.getGitRemoteAsync.mockResolvedValue({ url: null, credentialName: null });
    mockApi.listGitCredentialsAsync.mockResolvedValue({ names: [] });
  });

  it("loads the current remote url and credential names on mount", async () => {
    mockApi.getGitRemoteAsync.mockResolvedValue({ url: "https://example.test/repo.git", credentialName: "github-personal" });
    mockApi.listGitCredentialsAsync.mockResolvedValue({ names: ["github-personal", "gitlab-work"] });

    render(<ManageRepositoryPane api={mockApi as unknown as BrokerApi} workspacePath={"C:\\work\\proj"} onClose={jest.fn()} />);

    await waitFor(() => {
      expect(screen.getByTestId("manage-repo-remote-url")).toHaveValue("https://example.test/repo.git");
    });
    expect(mockApi.getGitRemoteAsync).toHaveBeenCalledWith("C:\\work\\proj");
    expect(screen.getByTestId("manage-repo-credential-select")).toHaveValue("github-personal");
    expect(screen.getByText("gitlab-work")).toBeInTheDocument();
  });

  it("saves a new remote url with the selected credential", async () => {
    mockApi.listGitCredentialsAsync.mockResolvedValue({ names: ["github-personal"] });
    mockApi.setGitRemoteAsync.mockResolvedValue({ url: "https://example.test/new.git", credentialName: "github-personal" });
    const user = userEvent.setup();

    render(<ManageRepositoryPane api={mockApi as unknown as BrokerApi} workspacePath={"C:\\work\\proj"} onClose={jest.fn()} />);

    await waitFor(() => expect(mockApi.listGitCredentialsAsync).toHaveBeenCalled());

    await user.clear(screen.getByTestId("manage-repo-remote-url"));
    await user.type(screen.getByTestId("manage-repo-remote-url"), "https://example.test/new.git");
    await user.selectOptions(screen.getByTestId("manage-repo-credential-select"), "github-personal");
    await user.click(screen.getByTestId("manage-repo-save-remote-btn"));

    await waitFor(() => {
      expect(mockApi.setGitRemoteAsync).toHaveBeenCalledWith("C:\\work\\proj", "https://example.test/new.git", "github-personal");
    });
  });

  it("adds a new named credential and selects it", async () => {
    mockApi.listGitCredentialsAsync.mockResolvedValue({ names: ["github-personal"] });
    mockApi.setGitCredentialAsync.mockResolvedValue({ ok: true });
    const user = userEvent.setup();

    render(<ManageRepositoryPane api={mockApi as unknown as BrokerApi} workspacePath={"C:\\work\\proj"} onClose={jest.fn()} />);

    await waitFor(() => expect(mockApi.listGitCredentialsAsync).toHaveBeenCalled());

    await user.selectOptions(screen.getByTestId("manage-repo-credential-select"), "__new__");
    await user.type(screen.getByTestId("manage-repo-new-credential-name"), "gitlab-work");
    await user.type(screen.getByTestId("manage-repo-new-credential-token"), "glpat-secret123");
    await user.click(screen.getByTestId("manage-repo-save-credential-btn"));

    await waitFor(() => {
      expect(mockApi.setGitCredentialAsync).toHaveBeenCalledWith("gitlab-work", "glpat-secret123");
    });
    await waitFor(() => {
      expect(screen.getByTestId("manage-repo-credential-select")).toHaveValue("gitlab-work");
    });
  });

  it("shows an inline error when saving the remote fails", async () => {
    mockApi.setGitRemoteAsync.mockRejectedValue(new Error("Url is required."));
    const user = userEvent.setup();

    render(<ManageRepositoryPane api={mockApi as unknown as BrokerApi} workspacePath={"C:\\work\\proj"} onClose={jest.fn()} />);
    await waitFor(() => expect(mockApi.listGitCredentialsAsync).toHaveBeenCalled());

    await user.type(screen.getByTestId("manage-repo-remote-url"), "https://example.test/repo.git");
    await user.click(screen.getByTestId("manage-repo-save-remote-btn"));

    await waitFor(() => {
      expect(screen.getByText("Url is required.")).toBeInTheDocument();
    });
  });

  it("calls onClose when the close button is clicked", async () => {
    const onClose = jest.fn();
    const user = userEvent.setup();
    render(<ManageRepositoryPane api={mockApi as unknown as BrokerApi} workspacePath={"C:\\work\\proj"} onClose={onClose} />);

    await waitFor(() => expect(mockApi.listGitCredentialsAsync).toHaveBeenCalled());
    await user.click(screen.getByTestId("manage-repo-close-btn"));

    expect(onClose).toHaveBeenCalled();
  });
});
