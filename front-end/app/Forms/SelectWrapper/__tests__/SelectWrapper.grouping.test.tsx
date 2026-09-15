import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { useState } from "react";
import "@testing-library/jest-dom";
import SelectWrapper from "../UI/SelectWrapper";

interface Item {
  id: string;
  name: string;
  section: string;
}

const DATA: Item[] = [
  { id: "1", name: "Alpha", section: "Team A" },
  { id: "2", name: "Beta", section: "Team A" },
  { id: "3", name: "Gamma", section: "Team B" },
];

/**
 * Mirrors selection state like every real consumer of the controlled
 * SelectWrapper does — otherwise each pick snaps back to empty.
 */
function Harness(props: { onChange: (items: Item[]) => void }) {
  const [selected, setSelected] = useState<Item[]>([]);
  return (
    <SelectWrapper<Item>
      data={DATA}
      selectedResolver={(candidate) => selected.some((s) => s.id === candidate.id)}
      valueResolver={(i) => i.id}
      labelResolver={(i) => i.name}
      groupResolver={(i) => i.section}
      isMulti
      onChange={(items) => {
        setSelected(items);
        props.onChange(items);
      }}
    />
  );
}

describe("SelectWrapper — groupResolver (additive grouping support)", () => {
  it("renders group headings when groupResolver is provided", async () => {
    const { container } = render(<Harness onChange={() => {}} />);

    fireEvent.mouseDown(container.querySelector(".react-select__control")!);

    await waitFor(() => {
      expect(screen.getAllByText("Team A").length).toBeGreaterThan(0);
    });
    expect(screen.getAllByText("Team B").length).toBeGreaterThan(0);
    expect(screen.getByText("Alpha")).toBeInTheDocument();
    expect(screen.getByText("Gamma")).toBeInTheDocument();
  });

  it("emits selections across different groups", async () => {
    const onChange = jest.fn();
    const { container } = render(<Harness onChange={onChange} />);

    fireEvent.mouseDown(container.querySelector(".react-select__control")!);
    fireEvent.click(screen.getByText("Alpha"));

    fireEvent.mouseDown(container.querySelector(".react-select__control")!);
    fireEvent.click(screen.getByText("Gamma"));

    await waitFor(() => {
      expect(onChange).toHaveBeenCalled();
    });
    const lastCall = onChange.mock.calls[onChange.mock.calls.length - 1][0] as Item[];
    const ids = lastCall.map((i) => i.id).sort();
    expect(ids).toEqual(["1", "3"]);
  });

  it("behaves exactly as before (flat list) when groupResolver is omitted", async () => {
    function FlatHarness(props: { onChange: (items: Item[]) => void }) {
      return (
        <SelectWrapper<Item>
          data={DATA}
          selectedResolver={() => false}
          valueResolver={(i) => i.id}
          labelResolver={(i) => i.name}
          isMulti
          onChange={props.onChange}
        />
      );
    }
    const onChange = jest.fn();
    const { container } = render(<FlatHarness onChange={onChange} />);

    fireEvent.mouseDown(container.querySelector(".react-select__control")!);

    // No group headings rendered
    expect(container.querySelectorAll(".react-select__group-heading").length).toBe(0);
    expect(screen.getByText("Alpha")).toBeInTheDocument();

    fireEvent.click(screen.getByText("Alpha"));
    await waitFor(() => expect(onChange).toHaveBeenCalledWith([expect.objectContaining({ id: "1" })]));
  });
});
