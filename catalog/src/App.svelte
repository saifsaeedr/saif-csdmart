<script module lang="ts">
    import {createRouter, Router} from "@roxi/routify";
    import routes from "../.routify/routes.default";
    import {SvelteToast} from "@zerodevx/svelte-toast";
    // Derive router prefix from <base href> in index.html (strip leading/trailing slashes)
    const baseHref = document.querySelector("base")?.getAttribute("href") || "/";
    const prefix = baseHref.replace(/^\/|\/$/g, "");
</script>

<script lang="ts">
  import { setupI18n, dir, locale } from "./i18n";

  function findRoute(routers: any, paths: any): any {
    if (paths.length === 0) {
      return routers;
    }

    let [currentPath, ...remainingPaths] = paths;
    if (currentPath.endsWith(".")) {
      currentPath = currentPath.slice(0, -1);
    }

    const matchingChild = routers.children.find(
      (child: any) => child.name === `${currentPath}`
    );

    if (matchingChild) {
      return findRoute(matchingChild, remainingPaths);
    } else {
      return null;
    }
  }

  let createdRouter: any = null;

  function prepareRouter() {
    if (createdRouter === null) {
      createdRouter = createRouter({
        routes: routes,
        urlRewrite: {
          toInternal: (url) => {
            const oldURL = url;

            url = url.replaceAll("//", "/");
            url = url.replace(`/${prefix}`, "");
            url = url === "" ? "/" : url;

            if ($locale == "ar" && url.endsWith(".ar")) {
              return oldURL;
            }
            if ($locale == "ku" && url.endsWith(".ku")) {
              return oldURL;
            }

            url = url.replaceAll(".ar", "").replaceAll(".ku", "");
            if (url.endsWith("index")) {
              url = url.slice(0, -5);
            }
            if (url.endsWith("/")) {
              url = url.slice(0, -1);
            }

            const lang = $locale === "en" ? null : $locale;
            const paths = url.split("/");

            let fileName = paths[paths.length - 1];
            if (fileName === "") {
              fileName = "index";
            }

            const tryPaths = [fileName, "index"];
            if (lang) {
              tryPaths.unshift(`index.${lang}`);
            }

            for (const tryPath of tryPaths) {
              paths.push(tryPath);
              let result = findRoute(routes, paths);
              if (result !== null) {
                const surl = url.split("/");
                if (surl.length === 1) {
                  return url;
                }
                return surl.join("/") + `/${paths[paths.length - 1]}`;
              }
              paths.pop();
            }

            return url;
          },
          toExternal: (url) => {
            document.dir = $dir;
            return `/${prefix}${url}`;
          },
        },
      });
    }
    return createdRouter;
  }

  const router = prepareRouter();

  setupI18n();
</script>

<div id="routify-app">
  <SvelteToast />
  <Router {router} />
</div>
