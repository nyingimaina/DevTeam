import React from "react";
import { render, screen, within } from "@testing-library/react";
import "@testing-library/jest-dom";
import Breadcrumb from "./Breadcrumb";
import { crumbsFor } from "./routes";

describe("crumbsFor", () => {
  const names = { project: "calculator", release: "Release adding", feature: "subtraction", stage: "Developer" };

  it("is just the project at home", () => {
    expect(crumbsFor({ kind: "home" }, names).map((c) => c.label)).toEqual(["calculator"]);
  });

  it("adds the release, feature and stage as you go deeper", () => {
    expect(crumbsFor({ kind: "release", releaseId: "r" }, names).map((c) => c.label)).toEqual(["calculator", "Release adding"]);
    expect(crumbsFor({ kind: "feature", releaseId: "r", featureId: "f" }, names).map((c) => c.label))
      .toEqual(["calculator", "Release adding", "subtraction"]);
    expect(crumbsFor({ kind: "stage", releaseId: "r", featureId: "f", stageName: "developer" }, names).map((c) => c.label))
      .toEqual(["calculator", "Release adding", "subtraction", "Developer"]);
  });

  it("points every crumb at its own level", () => {
    const crumbs = crumbsFor({ kind: "stage", releaseId: "r", featureId: "f", stageName: "developer" }, names);
    expect(crumbs.map((c) => c.route)).toEqual([
      { kind: "home" },
      { kind: "release", releaseId: "r" },
      { kind: "feature", releaseId: "r", featureId: "f" },
      { kind: "stage", releaseId: "r", featureId: "f", stageName: "developer" },
    ]);
  });

  it("falls back to a readable label when a name isn't known yet", () => {
    const labels = crumbsFor({ kind: "feature", releaseId: "r", featureId: "f" }, { project: "calculator" }).map((c) => c.label);
    expect(labels).toEqual(["calculator", "Release", "Feature"]);
  });
});

describe("Breadcrumb", () => {
  const crumbs = crumbsFor({ kind: "feature", releaseId: "r1", featureId: "f1" }, { project: "calculator", release: "Release adding", feature: "subtraction" });

  it("is a labelled navigation landmark", () => {
    render(<Breadcrumb crumbs={crumbs} />);
    expect(screen.getByRole("navigation", { name: "Breadcrumb" })).toBeInTheDocument();
  });

  it("makes every crumb but the last a real link to its level", () => {
    render(<Breadcrumb crumbs={crumbs} />);
    const nav = screen.getByRole("navigation", { name: "Breadcrumb" });

    expect(within(nav).getByRole("link", { name: "calculator" })).toHaveAttribute("href", "#/");
    expect(within(nav).getByRole("link", { name: "Release adding" })).toHaveAttribute("href", "#/r/r1");
  });

  it("marks the current page and does not link it", () => {
    render(<Breadcrumb crumbs={crumbs} />);
    const current = screen.getByText("subtraction");

    expect(current).toHaveAttribute("aria-current", "page");
    expect(screen.queryByRole("link", { name: "subtraction" })).not.toBeInTheDocument();
  });

  it("renders a single crumb as the current page", () => {
    render(<Breadcrumb crumbs={crumbsFor({ kind: "home" }, { project: "calculator" })} />);
    expect(screen.getByText("calculator")).toHaveAttribute("aria-current", "page");
  });
});
