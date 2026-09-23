import React from "react";
import { render, screen, fireEvent } from "@testing-library/react";
import "@testing-library/jest-dom";
import QuickReplyButtons from "./QuickReplyButtons";

describe("QuickReplyButtons", () => {
  it("renders one button per option", () => {
    render(<QuickReplyButtons options={["double", "decimal", "integer-only"]} onSelect={jest.fn()} disabled={false} />);
    expect(screen.getByRole("button", { name: "double" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "decimal" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "integer-only" })).toBeInTheDocument();
  });

  it("is grouped as a labelled button group", () => {
    render(<QuickReplyButtons options={["A", "B"]} onSelect={jest.fn()} disabled={false} />);
    expect(screen.getByRole("group", { name: "Quick replies" })).toBeInTheDocument();
  });

  it("calls onSelect with the clicked option's text", () => {
    const onSelect = jest.fn();
    render(<QuickReplyButtons options={["A", "B"]} onSelect={onSelect} disabled={false} />);

    fireEvent.click(screen.getByRole("button", { name: "B" }));

    expect(onSelect).toHaveBeenCalledWith("B");
  });

  it("disables every button when disabled is true (e.g. a reply is already sending)", () => {
    render(<QuickReplyButtons options={["A", "B"]} onSelect={jest.fn()} disabled={true} />);
    expect(screen.getByRole("button", { name: "A" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "B" })).toBeDisabled();
  });
});
