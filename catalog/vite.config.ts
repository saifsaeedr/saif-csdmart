import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";
import { mdsvex } from "mdsvex";
import routify from "@roxi/routify/vite-plugin";
import { svelte, vitePreprocess } from "@sveltejs/vite-plugin-svelte";
import * as path from "path";
import svelteMd from "vite-plugin-svelte-md";

const production = process.env.NODE_ENV === "production";

export default defineConfig(({ command }) => ({
  // dev: absolute base so vite serves public/ (config.json, favicon) at
  // /cat/<file>, matching <base href="/cat/"> in index.html.
  // build: relative base for portability when embedded under any path.
  base: command === "serve" ? "/cat/" : "./",
  clearScreen: false,
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "src"),
      "~": path.resolve(__dirname, "node_modules"),
    },
  },
  optimizeDeps: {
    include: ["flowbite", "@roxi/routify"],
  },
  plugins: [
    tailwindcss(),
    svelteMd(),
    routify({
      forceLogging: true,
      render: { ssg: false, ssr: false },
      routesDir: {
        default: "src/routes",
        "lang-ar": "src/routes",
      },
    }),
    svelte({
      exclude: ["node_modules/flowbite-svelte"],
      compilerOptions: { dev: !production },
      extensions: [".md", ".svelte"],
      preprocess: [
        vitePreprocess(),
        mdsvex({
          extension: "md",
          remarkPlugins: [
          ],
        }),
      ],
      onwarn: (warning, defaultHandler) => {
        // Ignore a11y_click_events_have_key_events warning from sveltestrap
        if (
          warning.code?.startsWith("a11y") || // warning.filename?.startsWith("/node_modules/svelte-jsoneditor")
          warning.filename?.startsWith("/node_modules")
        )
          return;
        if (typeof defaultHandler != "undefined") defaultHandler(warning);
      },
    }),
  ],
  build: {
    cssCodeSplit: true,
    cssMinify: "lightningcss",
    chunkSizeWarningLimit: 512,
    minify: "esbuild", // Use esbuild instead of terser (faster and built-in)
    target: "esnext",
    rollupOptions: {
      // commonJsVariableInEsm: noisy `module.exports` warning from
      //   @typewriter/delta — UMD-shaped ESM file we don't control.
      // pluginTimings: rolldown's per-build profile printout; useful when
      //   diagnosing slow builds, otherwise just chatter.
      checks: {
        commonJsVariableInEsm: false,
        pluginTimings: false,
      },
      output: {
        assetFileNames: (assetInfo) => {
          const name = assetInfo.name || "asset";
          if (name.endsWith(".css")) {
            return "assets/css/[name]-[hash][extname]";
          }
          if (name.match(/\.(woff2?|eot|ttf|otf)$/)) {
            return "assets/fonts/[name]-[hash][extname]";
          }
          return "assets/[name]-[hash][extname]";
        },
        chunkFileNames: "assets/js/[name]-[hash].js",
        entryFileNames: "assets/js/[name]-[hash].js",
        manualChunks(id) {
          if (!id.includes("node_modules")) return;
          if (id.includes("/flowbite")) return "vendor-flowbite";
          if (id.includes("/@roxi/routify/")) return "vendor-routify";
          return "vendor";
        },
      },
    },
  },
  esbuild: {
    drop: production ? ["debugger"] : [],
    pure: production ? ["console.log", "console.debug", "console.info"] : [],
  },
  css: {
    lightningcss: {
      // minify: true, // This is handled by cssMinify above
    },
  },
  // dev-only proxy: with backend="" in config.json, the SPA's tsdmart calls
  // become /cat/{managed,user,info}/... via document.baseURI. Forward those
  // to dmart on :8282 with the /cat prefix stripped (dmart only mounts the
  // API at bare paths). Keeps requests same-origin so SameSite=Lax cookies
  // work. Production embeds the SPA inside dmart so this section is ignored.
  server: {
    port: 1337,
    proxy: {
      "^/cat/(managed|user|info)(/.*)?": {
        target: "http://localhost:8282",
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/cat/, ""),
      },
    },
  },
}));
