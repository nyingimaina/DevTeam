/** @type {import('next').NextConfig} */
const isDev = process.env.NODE_ENV === "development";

const nextConfig = {
  reactStrictMode: false,
  images: { unoptimized: true },
  /** Static export is only for the production build; dev needs a real server so rewrites can proxy to the broker. */
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