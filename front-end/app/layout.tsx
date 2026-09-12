import React from "react";
import ThemeProvider from "./Theme/ThemeProvider";
import ZestButtonDefaultsProvider from "./ZestButtonDefaults/ZestButtonDefaultsProvider";
import ZestTextboxDefaultsProvider from "./ZestTextboxDefaults/ZestTextboxDefaultsProvider";
import "./globals.css";

export const metadata = {
  title: "DevTeam",
  description: "Your agentic dev team",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" data-theme="light">
      <body>
        <ThemeProvider>
          <ZestButtonDefaultsProvider>
            <ZestTextboxDefaultsProvider>{children}</ZestTextboxDefaultsProvider>
          </ZestButtonDefaultsProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}