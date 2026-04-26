<script lang="ts">
  import DashboardHeader from "@/components/DashboardHeader.svelte";
  import { signout, user } from "@/stores/user";
  import { onMount } from "svelte";
  import { Dmart } from "@edraj/tsdmart";
  import { website } from "@/config";
  import axios from "axios";
  import { get } from "svelte/store";
  import { initGlobalWebSocket } from "@/stores/websocket";
  import { isPublicRoute } from "@/lib/constants";
  import { withBasePrefix } from "@/lib/basePath";

  function redirectTo(path: string) {
    const target = withBasePrefix(path);
    if (window.location.pathname !== target) {
      window.location.href = target;
    }
  }

  const dmartAxios = axios.create({
    baseURL: website.backend,
    withCredentials: true,
    timeout: 30000,
  });

  // Add request interceptor to inject auth token
  dmartAxios.interceptors.request.use(
    (config) => {
      const token = localStorage.getItem("authToken");
      if (token) {
        config.headers.Authorization = `Bearer ${token}`;
      }
      return config;
    },
    (error) => Promise.reject(error),
  );

  dmartAxios.interceptors.response.use(
    (res) => res,
    async (error) => {
      if (error.code === "ERR_NETWORK") {
        console.warn("Network error: Check connection or server.");
      }
      
      const errorCode = error.response?.data?.error?.code;
      if (error.response?.status === 401 && [47, 48, 49].includes(errorCode)) {
        const currentPath = window.location.pathname;
        console.log({D: !isPublicRoute(currentPath)}, currentPath)
        if (!isPublicRoute(currentPath)) {
          console.log(`401 Unauthorized (code ${errorCode}) - redirecting to login`);
          redirectTo("/login");
        }
        await signout();
      }
      
      return Promise.reject(error);
    },
  );

  Dmart.setAxiosInstance(dmartAxios as any);

  onMount(async () => {
    const currentPath = window.location.pathname;

    if (isPublicRoute(currentPath)) {
      return;
    }

    // /info/me is anonymous-allowed and returns 200 with
    // {authenticated: bool} regardless of auth state, so we don't pollute
    // the console with a 401 on cold loads. Mid-session expiration is
    // still detected by the response interceptor above on real API calls.
    try {
      const r = await dmartAxios.get("info/me");
      const authed = r.data?.attributes?.authenticated === true;

      if (!authed) {
        await signout();
        redirectTo("/login");
        return;
      }

      // Connect global WebSocket for real-time notifications and chat
      const token = localStorage.getItem("authToken");
      const shortname = get(user).shortname;
      if (token) {
        initGlobalWebSocket(token, shortname);
      }

      if (currentPath === "/" || currentPath === "/login") {
        redirectTo("/dashboard/admin");
      }
    } catch (error: any) {
      // /info/me itself failed (network, server down). Treat as signed-out.
      console.warn("Session probe failed:", error?.message ?? error);
      await signout();
      redirectTo("/login");
    }
  });
</script>

<div class="app-shell">
  <DashboardHeader />
  <main class="app-main">
    <slot />
  </main>
</div>

<style>
  .app-shell {
    display: flex;
    flex-direction: column;
    min-height: 100vh;
    background: var(--gradient-page);
  }

  .app-main {
    flex: 1;
    animation: fadeIn var(--duration-normal) var(--ease-out);
  }
</style>
