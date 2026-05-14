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

  // Gate the slot so unauthenticated users never see protected content.
  // Public routes render immediately; protected routes show the spinner
  // until either the cached signed-in hint lets us render optimistically
  // or the /info/me probe in onMount confirms a session. All redirects
  // happen from onMount so the component is mounted before we navigate.
  const initialPath =
    typeof window !== "undefined" ? window.location.pathname : "/";
  const initiallyPublic = isPublicRoute(initialPath);
  // Read once at module-script run; cross-tab login/logout changes won't
  // reflect until the next page load. Treated only as an optimistic hint —
  // the /info/me probe below is the authoritative session check.
  const cachedSignedIn = get(user).signedin === true;

  let authReady = $state(initiallyPublic || cachedSignedIn);

  onMount(async () => {
    const currentPath = window.location.pathname;

    if (isPublicRoute(currentPath)) {
      return;
    }

    // No cached session on a protected route — skip the /info/me probe
    // (it would just confirm unauthenticated) and redirect to login.
    if (!cachedSignedIn) {
      redirectTo("/login");
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
        authReady = false;
        await signout();
        redirectTo("/login");
        return;
      }

      authReady = true;

      // Connect global WebSocket for real-time notifications and chat.
      // Skipped when enable_websocket is explicitly false in config.json,
      // which keeps getWebSocketService() returning null so all WS-using
      // call sites (messaging page, sendChatMessage, etc.) become no-ops.
      const token = localStorage.getItem("authToken");
      const shortname = get(user).shortname;
      if (token && website.enable_websocket !== false) {
        initGlobalWebSocket(token, shortname);
      }

      if (currentPath === "/" || currentPath === "/login") {
        redirectTo("/dashboard/admin");
      }
    } catch (error: any) {
      // /info/me itself failed (network, server down). Treat as signed-out.
      console.warn("Session probe failed:", error?.message ?? error);
      authReady = false;
      await signout();
      redirectTo("/login");
    }
  });
</script>

<div class="app-shell">
  <DashboardHeader />
  <main class="app-main">
    {#if authReady}
      <!-- svelte-ignore slot_element_deprecated -->
      <!-- Routify drives this layout via the legacy slot mechanism;
           migrating to {@render children?.()} leaves children undefined
           and the page renders blank. Revisit when Routify supports
           Svelte 5 snippets. -->
      <slot />
    {:else}
      <div class="auth-gate" aria-busy="true" aria-live="polite">
        <div class="auth-gate-spinner" role="status" aria-label="Loading"></div>
      </div>
    {/if}
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

  .auth-gate {
    min-height: 60vh;
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .auth-gate-spinner {
    width: 28px;
    height: 28px;
    border: 3px solid rgba(99, 102, 241, 0.2);
    border-top-color: rgb(99, 102, 241);
    border-radius: 50%;
    animation: auth-spin 0.8s linear infinite;
  }

  @keyframes auth-spin {
    to { transform: rotate(360deg); }
  }
</style>
