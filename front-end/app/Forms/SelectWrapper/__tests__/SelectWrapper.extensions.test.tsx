import { useState } from "react";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import "@testing-library/jest-dom";
import SelectWrapper from "../UI/SelectWrapper";

interface Item {
  id: string;
  name: string;
}

const DATA: Item[] = [
  { id: "1", name: "Alpha" },
  { id: "2", name: "Beta" },
];

describe("SelectWrapper — additive extensions for rich pickers", () => {
  it("onInputValueChange fires immediately on typing (not debounced)", async () => {
    const onInputValueChange = jest.fn();
    function Harness() {
      const [selected] = useState<Item[]>([]);
      return (
        <SelectWrapper<Item>
          data={DATA}
          selectedResolver={(c) => selected.some((s) => s.id === c.id)}
          valueResolver={(i) => i.id}
          labelResolver={(i) => i.name}
          isMulti
          onSearch={() => {}}
          onInputValueChange={onInputValueChange}
          onChange={() => {}}
        />
      );
    }
    const { container } = render(<Harness />);
    const input = container.querySelector("input.react-select__input")
      ?? container.querySelector(".react-select__input input");

    fireEvent.change(input!, { target: { value: "al" } });

    // Immediate — no 500ms debounce wait
    expect(onInputValueChange).toHaveBeenCalledWith("al");
  });

  it("formatOptionLabel receives the raw item and renders custom nodes", async () => {
    function Harness() {
      const [selected] = useState<Item[]>([]);
      return (
        <SelectWrapper<Item>
          data={DATA}
          selectedResolver={(c) => selected.some((s) => s.id === c.id)}
          valueResolver={(i) => i.id}
          labelResolver={(i) => i.name}
          groupResolver={() => "Group A"}
          isMulti
          formatOptionLabel={(item) => (
            <span data-testid={`rich-${item.id}`}>RICH:{item.name}</span>
          )}
          onChange={() => {}}
        />
      );
    }
    const { container } = render(<Harness />);
    fireEvent.mouseDown(container.querySelector(".react-select__control")!);

    await waitFor(() => {
      expect(screen.getByTestId("rich-1")).toHaveTextContent("RICH:Alpha");
    });
    expect(screen.getByTestId("rich-2")).toBeInTheDocument();
  });
});
