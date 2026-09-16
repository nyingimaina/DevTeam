import { useState } from "react";
import { render, screen, fireEvent, within } from "@testing-library/react";
import "@testing-library/jest-dom";
import PlaceholderAutocompleteInput, { placeholderTokens } from "../UI/PlaceholderAutocompleteInput";
import { PipelineEditorRoleDto } from "../../../Chat/Data/BrokerTypes";

const TOKENS = ["<F>", "<docs-root>", "<code-map/artifact.file>"];

function Harness({ multiline = false }: { multiline?: boolean }) {
  const [value, setValue] = useState("");
  return (
    <PlaceholderAutocompleteInput
      testId="prompt"
      value={value}
      onChange={setValue}
      tokens={TOKENS}
      multiline={multiline}
    />
  );
}

describe("PlaceholderAutocompleteInput", () => {
  it("renders a single-line input by default", () => {
    render(<Harness />);
    expect(screen.getByTestId("prompt").tagName).toBe("INPUT");
  });

  it("renders a textarea in multiline mode", () => {
    render(<Harness multiline />);
    expect(screen.getByTestId("prompt").tagName).toBe("TEXTAREA");
  });

  it("suggests matching tokens when typing < and a partial name", () => {
    render(<Harness />);
    const input = screen.getByTestId("prompt") as HTMLInputElement;

    fireEvent.change(input, { target: { value: "Check <doc" } });

    const suggestions = screen.getByTestId("prompt-suggestions");
    expect(within(suggestions).getByText("<docs-root>")).toBeInTheDocument();
    expect(within(suggestions).queryByText("<F>")).not.toBeInTheDocument();
  });

  it("inserts the selected token in place of the partial trigger text", () => {
    render(<Harness />);
    const input = screen.getByTestId("prompt") as HTMLInputElement;

    fireEvent.change(input, { target: { value: "Check <doc" } });
    fireEvent.mouseDown(within(screen.getByTestId("prompt-suggestions")).getByText("<docs-root>"));

    expect(input.value).toBe("Check <docs-root>");
  });

  it("stops suggesting once the trigger is closed with >", () => {
    render(<Harness />);
    const input = screen.getByTestId("prompt") as HTMLInputElement;

    fireEvent.change(input, { target: { value: "Check <docs-root> already closed <" } });

    // The second, still-open "<" at the end should still trigger suggestions.
    expect(screen.getByTestId("prompt-suggestions")).toBeInTheDocument();
  });

  it("browses every token via the Insert placeholder button without typing <", () => {
    render(<Harness multiline />);

    fireEvent.click(screen.getByTestId("prompt-browse-btn"));

    const suggestions = screen.getByTestId("prompt-suggestions");
    for (const token of TOKENS) {
      expect(within(suggestions).getByText(token)).toBeInTheDocument();
    }
  });

  it("inserting via the browse button appends at the current value's end", () => {
    render(<Harness />);
    fireEvent.click(screen.getByTestId("prompt-browse-btn"));
    fireEvent.mouseDown(within(screen.getByTestId("prompt-suggestions")).getByText("<F>"));

    expect((screen.getByTestId("prompt") as HTMLInputElement).value).toBe("<F>");
  });
});

describe("placeholderTokens", () => {
  function makeRole(overrides: Partial<PipelineEditorRoleDto> = {}): PipelineEditorRoleDto {
    return {
      name: "code-map",
      writesCode: false,
      signoff: null,
      userInputRequired: false,
      stepSummary: [],
      entryGates: [],
      exitGatePrompts: [],
      artifact: null,
      ...overrides,
    };
  }

  it("always includes <F> and every named root", () => {
    const tokens = placeholderTokens([]);
    expect(tokens).toContain("<F>");
    expect(tokens).toContain("<docs-root>");
    expect(tokens).toContain("<feature-docs-root>");
    expect(tokens).toContain("<feature-code-root-back>");
    expect(tokens).toContain("<feature-code-root-front>");
    expect(tokens).toContain("<workspace-root>");
  });

  it("includes a stage-artifact token only for roles that declare an artifact", () => {
    const roles = [
      makeRole({ name: "code-map", artifact: { root: "docs-root", fileName: "code-map.json", kind: "json" } }),
      makeRole({ name: "developer", artifact: null }),
    ];
    const tokens = placeholderTokens(roles);
    expect(tokens).toContain("<code-map/artifact.file>");
    expect(tokens).not.toContain("<developer/artifact.file>");
  });
});
