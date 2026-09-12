"use client";
import React from "react";
import { useTheme } from "../Theme/ThemeProvider";
import { ZestButtonConfigProvider } from "jattac.libs.web.zest-button";

export default function ZestButtonDefaultsProvider({ children }: { children: React.ReactNode }) {
  const { resolvedTheme } = useTheme();
  return (
    <ZestButtonConfigProvider config={{ defaultProps: { theme: resolvedTheme } }}>
      {children}
    </ZestButtonConfigProvider>
  );
}