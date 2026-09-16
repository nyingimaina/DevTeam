import React from "react";
import { render, screen, fireEvent } from "@testing-library/react";
import "@testing-library/jest-dom";
import SettingsView from "./SettingsView";
import BrokerApi from "../../Chat/Data/BrokerApi";

jest.mock("./ProfilesView", () => ({
  __esModule: true,
  default: ({ workspacePath }: { workspacePath: string }) => (
    <div data-testid="profiles-view-mock">Profiles Mock — {workspacePath}</div>
  ),
}));

jest.mock("./PipelineView", () => ({
  __esModule: true,
  default: ({ workspacePath }: { workspacePath: string }) => (
    <div data-testid="pipeline-view-mock">Pipeline Mock — {workspacePath}</div>
  ),
}));

describe("SettingsView", () => {
  it("shows the Profiles tab by default", () => {
    render(<SettingsView api={{} as BrokerApi} workspacePath="C:/work/proj" />);

    expect(screen.getByTestId("profiles-view-mock")).toBeInTheDocument();
    expect(screen.getByText(/Profiles Mock/)).toHaveTextContent("C:/work/proj");
  });

  it("shows a Profiles tab control", () => {
    render(<SettingsView api={{} as BrokerApi} workspacePath="C:/work/proj" />);

    expect(screen.getByText("Profiles")).toBeInTheDocument();
    fireEvent.click(screen.getByText("Profiles"));
    expect(screen.getByTestId("profiles-view-mock")).toBeInTheDocument();
  });

  it("switches to the Pipeline tab", () => {
    render(<SettingsView api={{} as BrokerApi} workspacePath="C:/work/proj" />);

    expect(screen.getByText("Pipeline")).toBeInTheDocument();
    fireEvent.click(screen.getByText("Pipeline"));
    expect(screen.getByTestId("pipeline-view-mock")).toBeInTheDocument();
    expect(screen.getByText(/Pipeline Mock/)).toHaveTextContent("C:/work/proj");
  });
});
