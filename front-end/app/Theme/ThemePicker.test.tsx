import React from "react";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import ThemePicker from "./ThemePicker";

describe("ThemePicker", () => {
  it("renders Light, System and Dark options", () => {
    render(<ThemePicker mode="system" onChange={() => {}} />);
    expect(screen.getByRole("button", { name: "Light" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "System" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Dark" })).toBeInTheDocument();
  });

  it("marks the active mode as pressed", () => {
    render(<ThemePicker mode="dark" onChange={() => {}} />);
    expect(screen.getByRole("button", { name: "Dark" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Light" })).toHaveAttribute("aria-pressed", "false");
  });

  it("notifies the parent when an option is chosen", async () => {
    const user = userEvent.setup();
    const onChange = jest.fn();
    render(<ThemePicker mode="system" onChange={onChange} />);
    await user.click(screen.getByRole("button", { name: "Dark" }));
    expect(onChange).toHaveBeenCalledWith("dark");
  });

  it("renders semantic buttons with type button", () => {
    render(<ThemePicker mode="system" onChange={() => {}} />);
    screen.getAllByRole("button").forEach((button) => {
      expect(button).toHaveAttribute("type", "button");
    });
  });
});