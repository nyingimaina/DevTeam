import React from "react";
import { render, screen, within } from "@testing-library/react";
import "@testing-library/jest-dom";
import RichText, { cleanAssistantBody } from "./RichText";

describe("cleanAssistantBody", () => {
  it("removes streamed tool-call echo lines", () => {
    const text = [
      "Here is the plan:",
      "tool: execute",
      "call_d3832abc01",
      "1. Add validation",
    ].join("\n");
    const cleaned = cleanAssistantBody(text);
    expect(cleaned).not.toContain("tool: execute");
    expect(cleaned).not.toContain("call_d3832abc01");
    expect(cleaned).toContain("Here is the plan:");
    expect(cleaned).toContain("1. Add validation");
  });

  it("keeps normal prose unchanged", () => {
    expect(cleanAssistantBody("**Bold** plan here?")).toBe("**Bold** plan here?");
  });

  it("returns empty string for null or blank input", () => {
    expect(cleanAssistantBody(null)).toBe("");
    expect(cleanAssistantBody(undefined)).toBe("");
    expect(cleanAssistantBody("   ")).toBe("");
  });
});

describe("RichText", () => {
  it("renders markdown headings, bold text and lists", () => {
    render(
      <RichText
        text={"## Steps\n\n1. **Add** client-side validation.\n2. Wire the submit handler."}
      />,
    );
    expect(screen.getByRole("heading", { level: 2, name: "Steps" })).toBeInTheDocument();
    expect(screen.getByText("Add", { selector: "strong" })).toBeInTheDocument();
    const items = screen.getAllByRole("listitem");
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent("Add client-side validation.");
  });

  it("renders gfm tables", () => {
    render(<RichText text={"| A | B |\n| --- | --- |\n| 1 | 2 |"} />);
    expect(screen.getByRole("table")).toBeInTheDocument();
    expect(within(screen.getByRole("table")).getByText("2")).toBeInTheDocument();
  });

  it("renders inline code and code fences", () => {
    render(<RichText text={"Use `npm test`.\n\n```sh\nnpm run build\n```"} />);
    expect(screen.getByText("npm test")).toBeInTheDocument();
    expect(screen.getByText("npm run build")).toBeInTheDocument();
  });

  it("strips tool-call echo lines before rendering", () => {
    const { container } = render(
      <RichText text={"tool: execute\ncall_abc12345xyz\nTake a look at the result."} />,
    );
    expect(screen.getByText("Take a look at the result.")).toBeInTheDocument();
    expect(container).not.toHaveTextContent("tool: execute");
    expect(container).not.toHaveTextContent("call_abc12345xyz");
  });

  it("does not render raw HTML (safe by default)", () => {
    const { container } = render(<RichText text={"<script>alert(1)</script>"} />);
    expect(container.querySelector("script")).toBeNull();
  });

  it("returns null for empty text", () => {
    const { container } = render(<RichText text="   " />);
    expect(container.firstChild).toBeNull();
  });
});