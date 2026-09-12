import React from "react";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import AutoGrowTextarea from "./AutoGrowTextarea";
import styles from "../Styles/AutoGrowTextarea.module.css";

describe("AutoGrowTextarea", () => {
  it("renders the controlled value and placeholder", () => {
    render(
      <AutoGrowTextarea
        value="hello"
        onChange={() => {}}
        onEnter={() => {}}
        placeholder="Ask something…"
      />,
    );
    const input = screen.getByPlaceholderText("Ask something…") as HTMLTextAreaElement;
    expect(input.value).toBe("hello");
  });

  it("notifies parent on change", async () => {
    const user = userEvent.setup();
    const onChange = jest.fn();
    render(<AutoGrowTextarea value="" onChange={onChange} onEnter={() => {}} />);
    await user.type(screen.getByRole("textbox"), "abc");
    expect(onChange).toHaveBeenCalled();
  });

  it("fires onEnter on Enter without shift", async () => {
    const user = userEvent.setup();
    const onEnter = jest.fn();
    render(<AutoGrowTextarea value="text" onChange={() => {}} onEnter={onEnter} />);
    await user.type(screen.getByRole("textbox"), "{Enter}");
    expect(onEnter).toHaveBeenCalledTimes(1);
  });

  it("does not fire onEnter on Shift+Enter", async () => {
    const user = userEvent.setup();
    const onEnter = jest.fn();
    render(<AutoGrowTextarea value="text" onChange={() => {}} onEnter={onEnter} />);
    await user.type(screen.getByRole("textbox"), "{Shift>}{Enter}{/Shift}");
    expect(onEnter).not.toHaveBeenCalled();
  });

  it("uses the pill style class", () => {
    render(<AutoGrowTextarea value="" onChange={() => {}} onEnter={() => {}} />);
    expect(screen.getByRole("textbox")).toHaveClass(styles.input);
  });
});