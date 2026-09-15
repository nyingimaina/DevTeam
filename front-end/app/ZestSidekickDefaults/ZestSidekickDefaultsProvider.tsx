"use client";
import React from "react";
import { useTheme } from "../Theme/ThemeProvider";
import { ZestSidekickConfigProvider } from "jattac.libs.web.zest-sidekick-menu";

export default function ZestSidekickDefaultsProvider({ children }: { children: React.ReactNode }) {
  const { resolvedTheme } = useTheme();
  return (
    <ZestSidekickConfigProvider config={{ defaultProps: { theme: resolvedTheme } }}>
      {children}
    </ZestSidekickConfigProvider>
  );
}
