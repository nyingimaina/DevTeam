import React from "react";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import PathBrowser from "./PathBrowser";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { FileSystemStatDto } from "../../Chat/Data/BrokerTypes";

const HOME = "C:\\Users\\nying";
const CODE_DIR = "C:\\Users\\nying\\code";

const homeEntries = [
  { name: "code", fullPath: CODE_DIR, kind: "directory", sizeBytes: null },
  { name: "notes.txt", fullPath: "C:\\Users\\nying\\notes.txt", kind: "file", sizeBytes: 42 },
];

const codeEntries = [
  { name: "DevTeam", fullPath: "C:\\Users\\nying\\code\\DevTeam", kind: "directory", sizeBytes: null },
];

function statFor(path: string, isGit = false): FileSystemStatDto {
  const segments = path.split(/[\\/]+/).filter(Boolean);
  return {
    name: segments[segments.length - 1] ?? path,
    kind: "directory",
    exists: true,
    isGitRepository: isGit,
  };
}

function createApi() {
  return {
    listFileSystemRootsAsync: jest.fn().mockResolvedValue([
      { path: HOME, displayName: "Home" },
      { path: "D:\\", displayName: "Drive (D:\\)" },
    ]),
    listDirectoryAsync: jest.fn().mockImplementation((path: string) =>
      Promise.resolve(path === CODE_DIR ? codeEntries : homeEntries),
    ),
    getFileSystemStatAsync: jest
      .fn()
      .mockImplementation((path: string) => Promise.resolve(statFor(path, path === CODE_DIR))),
    createDirectoryAsync: jest.fn(),
  } as unknown as BrokerApi;
}

async function renderMounted(api = createApi(), props: Partial<React.ComponentProps<typeof PathBrowser>> = {}) {
  const utils = render(<PathBrowser api={api} onSelect={() => {}} {...props} />);
  await screen.findByRole("button", { name: "Home" });
  return { api, ...utils };
}

describe("PathBrowser", () => {
  it("renders quick roots and the home folder contents on mount", async () => {
    const { api } = await renderMounted();

    expect(screen.getByRole("button", { name: "Home" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Drive (D:\\)" })).toBeInTheDocument();
    expect(api.listFileSystemRootsAsync).toHaveBeenCalledTimes(1);

    expect(await screen.findByRole("button", { name: "Open folder code" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Select file notes.txt" })).toBeInTheDocument();
    expect(screen.getByLabelText("Path")).toHaveValue(HOME);
  });

  it("navigates into a folder when its row is clicked", async () => {
    const { api } = await renderMounted();

    await userEvent.click(await screen.findByRole("button", { name: "Open folder code" }));

    expect(api.getFileSystemStatAsync).toHaveBeenCalledWith(CODE_DIR);
    expect(api.listDirectoryAsync).toHaveBeenCalledWith(CODE_DIR);
    expect(await screen.findByRole("button", { name: "Open folder DevTeam" })).toBeInTheDocument();
    expect(screen.getByLabelText("Path")).toHaveValue(CODE_DIR);
    expect(screen.getByText("git")).toBeInTheDocument();
  });

  it("returns to an ancestor from the breadcrumb", async () => {
    await renderMounted();

    await userEvent.click(await screen.findByRole("button", { name: "Open folder code" }));
    await userEvent.click(await screen.findByRole("button", { name: "Go to Home" }));

    expect(await screen.findByRole("button", { name: "Open folder code" })).toBeInTheDocument();
  });

  it("creates a new folder and refreshes the listing", async () => {
    const api = createApi();
    api.createDirectoryAsync = jest
      .fn()
      .mockResolvedValue(statFor("C:\\Users\\nying\\newproject"));

    await renderMounted(api);

    await userEvent.click(screen.getByRole("button", { name: "New folder" }));
    await userEvent.type(screen.getByLabelText("New folder name"), "newproject");
    await userEvent.click(screen.getByRole("button", { name: "Create" }));

    expect(api.createDirectoryAsync).toHaveBeenCalledWith("C:\\Users\\nying\\newproject");
    expect(api.listDirectoryAsync).toHaveBeenCalledWith(HOME);
  });

  it("reports the current folder in pickDirectory mode", async () => {
    const onSelect = jest.fn();
    const api = createApi();
    await renderMounted(api, { onSelect });

    await userEvent.click(await screen.findByRole("button", { name: "Open folder code" }));
    await userEvent.click(await screen.findByRole("button", { name: "Choose this folder" }));

    expect(onSelect).toHaveBeenCalledWith(CODE_DIR, "directory");
    expect(screen.getByTestId("pathbrowser-selection")).toBeInTheDocument();
  });

  it("selects a file in pickFile mode", async () => {
    const onSelect = jest.fn();
    const api = createApi();
    await renderMounted(api, { mode: "pickFile", onSelect });

    await userEvent.click(await screen.findByRole("button", { name: "Select file notes.txt" }));

    expect(onSelect).toHaveBeenCalledWith("C:\\Users\\nying\\notes.txt", "file");
  });

  it("filters entries as the user types", async () => {
    await renderMounted();

    await userEvent.type(screen.getByLabelText("Filter entries"), "notes");

    expect(screen.getByRole("button", { name: "Select file notes.txt" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Open folder code" })).not.toBeInTheDocument();
  });

  it("surfaces an error when the listing fails", async () => {
    const api = createApi();
    api.listDirectoryAsync = jest.fn().mockRejectedValue(new Error("Access denied"));

    render(<PathBrowser api={api} onSelect={() => {}} />);

    expect(await screen.findByRole("alert")).toHaveTextContent("Access denied");
  });
});