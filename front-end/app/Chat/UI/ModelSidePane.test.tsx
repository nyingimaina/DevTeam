import React from "react";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import ModelSidePane from "./ModelSidePane";

const modes = [
  { value: "build", name: "Build" },
  { value: "plan", name: "Plan" },
];

function renderPane(overrides: Partial<React.ComponentProps<typeof ModelSidePane>> = {}) {
  const props: React.ComponentProps<typeof ModelSidePane> = {
    models: [{ value: "opencode/big-pickle", name: "Big Pickle" }],
    currentModelId: "opencode/big-pickle",
    modes,
    currentModeId: "build",
    sessions: [],
    onSelectModel: () => {},
    onSelectSession: () => {},
    onNewSession: () => {},
    onDeleteSession: () => {},
    onClose: () => {},
    onSelectMode: () => {},
    ...overrides,
  };
  render(<ModelSidePane {...props} />);
}

describe("ModelSidePane", () => {
  it("renders a mode select with the current mode selected", () => {
    renderPane();
    const select = screen.getByTestId("mode-select") as HTMLSelectElement;
    expect(select).toHaveValue("build");
  });

  it("notifies the parent when the mode changes", async () => {
    const user = userEvent.setup();
    const onSelectMode = jest.fn();
    renderPane({ onSelectMode });
    await user.selectOptions(screen.getByTestId("mode-select"), "plan");
    expect(onSelectMode).toHaveBeenCalledWith("plan");
  });

  it("shows an empty state when no modes are available", () => {
    renderPane({ modes: [], currentModeId: undefined });
    expect(screen.getByText("No modes available.")).toBeInTheDocument();
  });

  it("renders the theme picker and reports changes", async () => {
    const user = userEvent.setup();
    const onThemeModeChange = jest.fn();
    renderPane({ themeMode: "light", onThemeModeChange });
    expect(screen.getByText("Theme")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Dark" }));
    expect(onThemeModeChange).toHaveBeenCalledWith("dark");
  });

  it("renders semantic buttons with an explicit type", () => {
    renderPane();
    expect(screen.getByLabelText("Close side pane")).toHaveAttribute("type", "button");
    expect(screen.getByRole("button", { name: "+ New conversation" })).toHaveAttribute("type", "button");
  });
});