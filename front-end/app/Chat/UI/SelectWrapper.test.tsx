import React from "react";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import SelectWrapper from "./SelectWrapper";

describe("SelectWrapper", () => {
  const options = [
    { value: "build", name: "Build", description: "Implement features" },
    { value: "plan", name: "Plan" },
  ];

  it("renders a label and the selected option", () => {
    render(
      <SelectWrapper options={options} value="build" onChange={() => {}} label="Mode" testId="mode" />,
    );
    const select = screen.getByTestId("mode") as HTMLSelectElement;
    expect(select).toHaveValue("build");
    expect(select.options).toHaveLength(2);
    expect(screen.getByText("Mode")).toBeInTheDocument();
  });

  it("notifies the parent when an option is selected", async () => {
    const user = userEvent.setup();
    const onChange = jest.fn();
    render(<SelectWrapper options={options} value="build" onChange={onChange} testId="mode" />);
    await user.selectOptions(screen.getByTestId("mode"), "plan");
    expect(onChange).toHaveBeenCalledWith("plan");
  });

  it("shows a disabled placeholder when nothing is selected", () => {
    render(
      <SelectWrapper
        options={options}
        value=""
        onChange={() => {}}
        placeholder="Select a mode"
        testId="mode"
      />,
    );
    const select = screen.getByTestId("mode") as HTMLSelectElement;
    expect(select).toHaveValue("");
    expect(select.options[0]).toHaveTextContent("Select a mode");
    expect(select.options[0].disabled).toBe(true);
  });

  it("is disabled when disabled is true", () => {
    render(<SelectWrapper options={options} value="build" onChange={() => {}} disabled testId="mode" />);
    expect(screen.getByTestId("mode")).toBeDisabled();
  });
});