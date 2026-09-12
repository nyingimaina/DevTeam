/** @type {import('next').NextConfig} */
const nextConfig = {
  output: "export",
  reactStrictMode: false,
  images: { unoptimized: true },
  /** Dev-mode only: the export build is served by the broker on its own origin, so no rewrites needed. */
  ...(process.env.NODE_ENV === "development"
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
    : {}),
};
module.exports = nextConfig;