import React from "react";
import ThemeProvider from "./Theme/ThemeProvider";
import ZestButtonDefaultsProvider from "./ZestButtonDefaults/ZestButtonDefaultsProvider";
import ZestTextboxDefaultsProvider from "./ZestTextboxDefaults/ZestTextboxDefaultsProvider";
import ZestSidekickDefaultsProvider from "./ZestSidekickDefaults/ZestSidekickDefaultsProvider";
import "./globals.css";

export const metadata = {
  title: "DevTeam",
  description: "Your agentic dev team",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <head>
        <script
          dangerouslySetInnerHTML={{
            __html: `(function(){try{var k="devteam.theme";var m=localStorage.getItem(k);if(m!=="light"&&m!=="dark"&&m!=="system"){m="system";}var r=m==="system"?(window.matchMedia&&window.matchMedia("(prefers-color-scheme: dark)").matches?"dark":"light"):m;document.documentElement.setAttribute("data-theme",r);}catch(e){document.documentElement.setAttribute("data-theme","light");}})();`,
          }}
        />
      </head>
      <body>
        <ThemeProvider>
          <ZestButtonDefaultsProvider>
            <ZestTextboxDefaultsProvider>
              <ZestSidekickDefaultsProvider>{children}</ZestSidekickDefaultsProvider>
            </ZestTextboxDefaultsProvider>
          </ZestButtonDefaultsProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}