import {defineConfig} from "vite";
import {mdsvex} from "mdsvex";
import routify from "@roxi/routify/vite-plugin";
import {svelte, vitePreprocess} from "@sveltejs/vite-plugin-svelte";
import {viteStaticCopy} from "vite-plugin-static-copy";
import plantuml from "@akebifiky/remark-simple-plantuml";
import svelteMd from "vite-plugin-svelte-md";
import tailwindcss from "@tailwindcss/vite"
import {execSync} from "node:child_process";
import type {Plugin} from "vite";

// `prismjs/components/prism-*.js` files reference a bare global `Prism`
// without importing it. Under rolldown they get bundled as side-effect
// modules whose top-level code runs before any importer body — so the
// global is undefined and the load throws. Prepending an explicit
// `import Prism from "prismjs"` turns the bare reference into a tracked
// ESM binding, which rolldown then orders correctly behind the core.
const prismAddonImportPlugin = (): Plugin => ({
  name: "cxb:prismjs-addon-import",
  enforce: "pre",
  transform(code, id) {
    if (/\/prismjs\/components\/prism-[^./]+\.js(?:[?#]|$)/.test(id) && !code.includes("import Prism")) {
      return {code: `import Prism from "prismjs";\n${code}`, map: null};
    }
  },
});

// @roxi/routify ships `console.debug(...) // ROUTIFY-DEV-ONLY` lines that
// produce the "processing scroll queue" / "scroll to top" noise in the
// browser console on every navigation. Routify's own vite-plugin has a
// stripLogs() transform meant to remove these in production, but under
// vite 8 + rolldown the hook order doesn't fire it for the scroller
// chunks. We do the same strip ourselves with a `pre` transform so it
// runs regardless of plugin order.
const routifyStripDevLogsPlugin = (): Plugin => ({
  name: "cxb:routify-strip-dev-logs",
  enforce: "pre",
  transform(code, id) {
    if (!/\/@roxi\/routify\/lib\//.test(id)) return;
    if (!/routify-dev-only/i.test(code)) return;
    return {
      code: code
        .replace(/\/\/ *routify-dev-only-start[\s\S]+?\/\/ *routify-dev-only-end/gim, "")
        .replace(/.+\/\/ *routify-dev-only.*$/gim, ""),
      map: null,
    };
  },
});

const production = process.env.NODE_ENV === "production";
const gitHash = (() => {
  try {
    return execSync("git rev-parse --short HEAD").toString().trim();
  } catch {
    return "unknown";
  }
})();

export default defineConfig(({command}) => ({
  // dev: absolute base so vite serves public/ files (config.json, favicon) at
  // /cxb/<file>, matching <base href="/cxb/"> in index.html. Without this the
  // browser fetches /cxb/config.json and gets the SPA fallback HTML.
  // build: relative base so the bundle is portable and works when mounted
  // at any path by the embedding server.
  base: command === "serve" ? "/cxb/" : "./",
  clearScreen: false,
  define: {
    'import.meta.env.VITE_GIT_HASH': JSON.stringify(gitHash),
  },
  resolve: {
    alias: {
      "@": process.cwd() + "/src",
      "~": process.cwd() + "/node_modules",
    },
  },
  plugins: [
    prismAddonImportPlugin(),
    routifyStripDevLogsPlugin(),
    tailwindcss(),
    svelteMd(),
    viteStaticCopy({
      targets: [
        {
          src: 'public/config.json',
          dest: ''
        }
      ]
    }),
    routify({
      "render.ssr": {enable: false},
      // Misleadingly named: forceLogging defaults to TRUE which means
      // "do not strip console.debug calls". Setting it false lets the
      // routify vite-plugin's stripLogs() transform remove the
      // "processing scroll queue" / "scroll to top" noise from the
      // production bundle.
      forceLogging: false,
    }),
    svelte({
      compilerOptions: {
        dev: !production,
      },
      extensions: [".md", ".svelte"],
      preprocess: [
        vitePreprocess(),
        mdsvex({
          extension: "md",
          remarkPlugins: [
            plantuml, {
              baseUrl: "https://www.plantuml.com/plantuml/svg"
            }
          ],
        }) as any
      ],
      onwarn: (warning, defaultHandler) => {
        const ignoredWarnings = [
          'non_reactive_update',
          'state_referenced_locally',
          'element_invalid_self_closing_tag',
          'event_directive_deprecated',
          'css_unused_selector'
        ];
        if (
            warning.code?.startsWith("a11y") ||
            warning.filename?.startsWith("/node_modules") ||
            ignoredWarnings.includes(warning.code)
        )
          return;
        if (typeof defaultHandler !== "undefined") defaultHandler(warning);
      },
    }),
  ],
  build: {
    chunkSizeWarningLimit: 512,
    cssMinify: 'lightningcss',
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
        manualChunks(id) {
          if (id.includes('node_modules')) {
            const pkg = id.toString().split('node_modules/')[1].split('/')[0].toString();
            // Skip packages that produce empty chunks after tree-shaking
            const skipChunks = [
              '@popperjs', 'date-fns', 'fast-deep-equal', 'fast-uri',
              'jmespath', 'json-schema-traverse', 'jsonpath-plus'
            ];
            if (skipChunks.includes(pkg)) return;
            return pkg;
          }
        },
      },
    }
  },
  // dev-only proxy: with backend="" in config.json (the default), the SPA's
  // tsdmart calls become /cxb/{managed,user,info}/... via document.baseURI
  // resolution. Forward those to dmart on :8282 with the /cxb prefix
  // stripped, since dmart only exposes the API at the bare paths. Sidesteps
  // SameSite=Lax cookie loss across ports. Production embeds the SPA inside
  // dmart so same-origin is automatic and this section is ignored.
  server: {
    port: 1337,
    proxy: {
      "^/cxb/(managed|user|info)(/.*)?": {
        target: "http://localhost:8282",
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/cxb/, ""),
      },
    },
  },
}));