/** @type {import('next').NextConfig} */
const isDev = process.env.NODE_ENV === "development";

/**
 * react-markdown + the unified/remark/micromark family ship pure ESM, which jest (via next/jest)
 * refuses to load unless the packages are listed in transpilePackages. Compute the transitive
 * closure from node_modules so the list stays correct across version bumps.
 */
const fs = require("fs");
const path = require("path");
function esmTranspilePackages(entries) {
  const seen = new Set();
  const stack = [...entries];
  while (stack.length) {
    const name = stack.pop();
    if (seen.has(name)) continue;
    seen.add(name);
    const manifest = path.join("node_modules", name, "package.json");
    if (!fs.existsSync(manifest)) continue;
    const deps = JSON.parse(fs.readFileSync(manifest, "utf8")).dependencies || {};
    for (const dep of Object.keys(deps)) {
      if (!dep.startsWith("@types/") && !seen.has(dep)) stack.push(dep);
    }
  }
  return [...seen];
}

const nextConfig = {
  reactStrictMode: false,
  images: { unoptimized: true },
  transpilePackages: esmTranspilePackages(["react-markdown", "remark-gfm"]),
  ...(isDev
    ? {
        async rewrites() {
          const broker = process.env.DEVTEAM_BROKER_URL ?? "http://127.0.0.1:5202";
          return [
            { source: "/api/:path*", destination: `${broker}/api/:path*` },
            { source: "/hub/:path*", destination: `${broker}/hub/:path*` },
            { source: "/healthz", destination: `${broker}/healthz` },
          ];
        },
      }
    : { output: "export" }),
};
module.exports = nextConfig;