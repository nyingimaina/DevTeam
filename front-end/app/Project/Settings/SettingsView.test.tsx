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

jest.mock("./SpecialistsView", () => ({
  __esModule: true,
  default: () => <div data-testid="specialists-view-mock">Specialists Mock</div>,
}));

jest.mock("./SupportView", () => ({
  __esModule: true,
  default: () => <div data-testid="support-view-mock">Support Mock</div>,
}));

jest.mock("./ModelsView", () => ({
  __esModule: true,
  default: () => <div data-testid="models-view-mock">Models Mock</div>,
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

  it("switches to the Specialists tab", () => {
    render(<SettingsView api={{} as BrokerApi} workspacePath="C:/work/proj" />);

    expect(screen.getByText("Specialists")).toBeInTheDocument();
    fireEvent.click(screen.getByText("Specialists"));
    expect(screen.getByTestId("specialists-view-mock")).toBeInTheDocument();
  });

  it("switches to the Support tab", () => {
    render(<SettingsView api={{} as BrokerApi} workspacePath="C:/work/proj" />);

    expect(screen.getByText("Support")).toBeInTheDocument();
    fireEvent.click(screen.getByText("Support"));
    expect(screen.getByTestId("support-view-mock")).toBeInTheDocument();
  });

  it("switches to the Models tab", () => {
    render(<SettingsView api={{} as BrokerApi} workspacePath="C:/work/proj" />);

    expect(screen.getByText("Models")).toBeInTheDocument();
    fireEvent.click(screen.getByText("Models"));
    expect(screen.getByTestId("models-view-mock")).toBeInTheDocument();
  });
});
