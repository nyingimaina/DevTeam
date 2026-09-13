"use client";
import React from "react";
import { useTheme } from "../Theme/ThemeProvider";
import { ZestTextboxConfigProvider } from "jattac.libs.web.zest-textbox";

export default function ZestTextboxDefaultsProvider({ children }: { children: React.ReactNode }) {
  const { resolvedTheme } = useTheme();
  return (
    <ZestTextboxConfigProvider
      value={{ theme: resolvedTheme, helperTextConfig: () => ({}), showProgressBar: true, animatedCounter: true }}
    >
      {children}
    </ZestTextboxConfigProvider>
  );
}