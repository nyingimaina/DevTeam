"use client";
import React, { createContext, useContext } from "react";

export interface IThemeInfo {
  mode: "light";
  resolvedTheme: "light";
}

const ThemeContext = createContext<IThemeInfo>({ mode: "light", resolvedTheme: "light" });

export function useTheme(): IThemeInfo {
  return useContext(ThemeContext);
}

export default function ThemeProvider({ children }: { children: React.ReactNode }) {
  return <ThemeContext.Provider value={{ mode: "light", resolvedTheme: "light" }}>{children}</ThemeContext.Provider>;
}