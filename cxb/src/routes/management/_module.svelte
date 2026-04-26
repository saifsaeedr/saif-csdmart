<!-- routify:meta reset -->
<script>
    import {goto} from '@roxi/routify';
    import {Dmart} from "@edraj/tsdmart";
    import {website} from "@/config";
    import axios from "axios";
    import Login from "@/components/Login.svelte";
    import ManagementHeader from "@/components/management/ManagementHeader.svelte";
    import {Level} from "@/utils/toast";
    import {debouncedShowToast} from "@/utils/debounce";
    import {Spinner} from "flowbite-svelte";
    import {getSpaces} from "@/lib/dmart_services.js";
    import {onMount} from "svelte";
    import {user} from "@/stores/user.js";

    $goto

    const dmartAxios = axios.create({
        baseURL: website.backend,
        withCredentials: true,
        timeout: website.backend_timeout,
    });
    let isRedirectingToLogin = false;
    dmartAxios.interceptors.response.use((request) => {
        return request;
    }, (error) => {
        if(error.code === 'ERR_NETWORK'){
            debouncedShowToast(Level.warn, "Network error.\nPlease check your connection or the server is down.");
        }
        if (
            error.response?.status === 401
            && [47, 48, 49].includes(error.response?.data?.error?.code)
            && !isRedirectingToLogin
            && localStorage.getItem("authToken")
        ) {
            isRedirectingToLogin = true;
            localStorage.removeItem("authToken");
            localStorage.removeItem("user");
            window.location.reload();
        }
        return Promise.reject(error);
    });
    Dmart.setAxiosInstance(dmartAxios);

    const storedToken = typeof localStorage !== 'undefined' && localStorage.getItem("authToken");
    if (storedToken) {
        Dmart.setToken(storedToken);
    }

    // Boot session probe: /info/me returns 200 with {authenticated: bool}
    // for both signed-in and anonymous callers (no 401 noise on cold loads).
    // Mid-session expiration is still detected by the response interceptor
    // above when a regular API call returns 401.
    const profilePromise = dmartAxios.get("info/me").then((r) => {
        const authed = r.data?.attributes?.authenticated === true;
        if (!authed) {
            // Clean up any stale local state so the Login form shows.
            if (typeof localStorage !== "undefined") {
                localStorage.removeItem("authToken");
                localStorage.removeItem("user");
            }
            user.set({signedin: false, locale: $user?.locale});
            throw new Error("not signed in");
        }
        // Authed — fire the spaces fetch (best-effort) and resolve.
        getSpaces().catch(() => {});
        return r.data;
    });
</script>

{#await profilePromise}
    <div class="flex w-svw h-svh justify-center items-center">
        <Spinner color="blue" size="16" />
    </div>
{:then _}
    {#if !$user || !$user.signedin}
        <Login />
    {:else}
        <div class="flex flex-col h-screen">
            <ManagementHeader />
            <div class="flex-grow overflow-auto">
                <slot />
            </div>
        </div>
    {/if}
{:catch error}
    <Login />
{/await}
