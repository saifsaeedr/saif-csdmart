<script lang="ts">
  import SearchBar from "./SearchBar.svelte";
  import { onDestroy } from "svelte";
  import { newNotificationType } from "@/stores/newNotificationType";
  import { _, locale, switchLocale } from "@/i18n";
  import { roles, signout, user } from "@/stores/user";
  import { goto } from "@roxi/routify";
  import { getWebSocketService } from "@/lib/services/websocket";
  import { wsConnected } from "@/stores/websocket";
  import { isPublicRoute } from "@/lib/constants";
  import { website } from "@/config";

  $goto;

  $effect(() => {
    const path = window.location.pathname;
    if (!$user?.signedin && path !== "/login" && !isPublicRoute(path)) {
      $goto("/login");
    }
  });

  let isMenuOpen = $state(false);
  let isRTL = $derived($locale === "ar" || $locale === "ku");

  let removeListener: (() => void) | null = null;

  // Register WS listener reactively when connection becomes available
  $effect(() => {
    if ($wsConnected && $user.signedin) {
      const ws = getWebSocketService();
      if (ws && !removeListener) {
        removeListener = ws.addMessageListener(handleRealtimeMessage);
      }
    }
  });

  function handleRealtimeMessage(data: any) {
    // csdmart plugin broadcasts arrive as type "notification_subscription"
    // with action_type in the message payload
    if (data.type === "notification_subscription" && data.message?.action_type) {
      const action = data.message.action_type;
      if (action === "create") {
        $newNotificationType = "create_event";
      } else if (action === "update" || action === "progress_ticket") {
        $newNotificationType = "progress";
      }
    }
  }

  onDestroy(() => {
    removeListener?.();
  });

  function renderNotificationIconColor() {
    switch ($newNotificationType) {
      case "create_comment":
        return "text-blue-500";
      case "create_reaction":
        return "text-red-500";
      case "progress":
        return "text-amber-500";
      default:
        return "text-gray-500";
    }
  }

  function handleLogin() {
    $goto("/login");
  }

  async function handleLogout() {
    await signout();
    $goto("/login");
    isMenuOpen = false;
  }

  function toggleMenu() {
    isMenuOpen = !isMenuOpen;
  }

  function closeMenu() {
    isMenuOpen = false;
  }

  function handleMenuItemClick(href: string) {
    $goto(href);
    closeMenu();
  }

  $effect(() => {
    renderNotificationIconColor();
  });

  $effect(() => {
    function handleClickOutside(event: MouseEvent) {
      const target = event.target as Element;
      if (isMenuOpen && !target.closest(".menu-container")) {
        closeMenu();
      }
    }

    if (isMenuOpen) {
      document.addEventListener("click", handleClickOutside);
      return () => document.removeEventListener("click", handleClickOutside);
    }
  });

  function getInitials(u: any) {
    if (!u) return "?";
    let name = u.localized_displayname || u.shortname || "";
    if (!name) return "?";
    let parts = name.split(" ");
    if (parts.length >= 2) {
      return (parts[0].charAt(0) + parts[1].charAt(0)).toUpperCase();
    }
    return name.substring(0, 2).toUpperCase();
  }
</script>

<header
  class={`sticky top-0 z-40 w-full ${$user.signedin ? "pb-2 max-w-[1500px] mx-auto px-4" : "bg-white/80 border-b border-gray-200 backdrop-blur-md"}`}
>
  <div class={$user.signedin ? "" : "container mx-auto sm:px-6"}>
    <div
      class={$user.signedin
        ? "w-full h-[56px] bg-white/80 backdrop-blur-xl flex items-center px-5 justify-between rounded-2xl border border-white/40 shadow-[0_2px_24px_0_rgba(0,0,0,0.06)] transition-all duration-300"
        : "flex h-16 items-center justify-between"}
    >
      <!-- Logo/Brand -->
      <div
        class={$user.signedin
          ? "flex items-center pl-2 flex-1 min-w-0"
          : "flex items-center w-3/6"}
      >
        <a
          href="/"
          class={`flex items-center justify-start group ${$user.signedin ? "space-x-2" : "space-x-3"}`}
        >
          <svg
            class="me-2"
            width="32"
            height="32"
            viewBox="8 6 32 32"
            fill="none"
            xmlns="http://www.w3.org/2000/svg"
          >
            <g filter="url(#filter0_d_30_2327)">
              <path
                d="M8 16C8 10.4772 12.4772 6 18 6H30C35.5228 6 40 10.4772 40 16V28C40 33.5228 35.5228 38 30 38H18C12.4772 38 8 33.5228 8 28V16Z"
                fill="url(#paint0_linear_30_2327)"
              />
              <path
                d="M18.6667 23.3333C18.5406 23.3338 18.4169 23.2984 18.31 23.2313C18.2032 23.1643 18.1176 23.0682 18.0631 22.9544C18.0087 22.8406 17.9876 22.7137 18.0024 22.5884C18.0172 22.4632 18.0673 22.3446 18.1467 22.2467L24.7467 15.4467C24.7963 15.3895 24.8637 15.3509 24.9381 15.3372C25.0124 15.3234 25.0892 15.3353 25.1559 15.371C25.2226 15.4067 25.2751 15.4639 25.305 15.5334C25.3348 15.6029 25.3401 15.6804 25.3201 15.7533L24.0401 19.7667C24.0023 19.8677 23.9897 19.9764 24.0031 20.0833C24.0166 20.1903 24.0558 20.2925 24.1175 20.381C24.1791 20.4695 24.2613 20.5417 24.3569 20.5914C24.4526 20.6412 24.5589 20.667 24.6667 20.6667H29.3334C29.4596 20.6662 29.5833 20.7016 29.6901 20.7687C29.797 20.8358 29.8826 20.9318 29.937 21.0456C29.9915 21.1594 30.0125 21.2863 29.9977 21.4116C29.9829 21.5369 29.9329 21.6554 29.8534 21.7533L23.2534 28.5533C23.2039 28.6105 23.1364 28.6491 23.0621 28.6629C22.9877 28.6766 22.9109 28.6647 22.8443 28.629C22.7776 28.5933 22.725 28.5361 22.6952 28.4666C22.6654 28.3971 22.66 28.3196 22.6801 28.2467L23.9601 24.2333C23.9978 24.1323 24.0105 24.0237 23.997 23.9167C23.9835 23.8097 23.9443 23.7076 23.8827 23.6191C23.8211 23.5306 23.7389 23.4583 23.6432 23.4086C23.5476 23.3588 23.4412 23.333 23.3334 23.3333H18.6667Z"
                stroke="white"
                stroke-width="1.33333"
                stroke-linecap="round"
                stroke-linejoin="round"
              />
            </g>
            <defs>
              <filter
                id="filter0_d_30_2327"
                x="0"
                y="0"
                width="48"
                height="48"
                filterUnits="userSpaceOnUse"
                color-interpolation-filters="sRGB"
              >
                <feFlood flood-opacity="0" result="BackgroundImageFix" />
                <feColorMatrix
                  in="SourceAlpha"
                  type="matrix"
                  values="0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"
                  result="hardAlpha"
                />
                <feOffset dy="2" />
                <feGaussianBlur stdDeviation="4" />
                <feComposite in2="hardAlpha" operator="out" />
                <feColorMatrix
                  type="matrix"
                  values="0 0 0 0 0.388235 0 0 0 0 0.4 0 0 0 0 0.945098 0 0 0 0.2 0"
                />
                <feBlend
                  mode="normal"
                  in2="BackgroundImageFix"
                  result="effect1_dropShadow_30_2327"
                />
                <feBlend
                  mode="normal"
                  in="SourceGraphic"
                  in2="effect1_dropShadow_30_2327"
                  result="shape"
                />
              </filter>
              <linearGradient
                id="paint0_linear_30_2327"
                x1="8"
                y1="6"
                x2="40"
                y2="38"
                gradientUnits="userSpaceOnUse"
              >
                <stop stop-color="#6366F1" />
                <stop offset="1" stop-color="#8B5CF6" />
              </linearGradient>
            </defs>
          </svg>

          <span
            class={`font-inter font-bold text-[17px] leading-[25.5px] tracking-[-0.43px] text-[#1A1A2E] ${$user.signedin ? "text-[1.1rem] group-hover:text-indigo-600" : "text-xl group-hover:text-blue-600"}`}
            >{$_("Spaces")}</span
          >
        </a>
        {#if $user.signedin}
          <div class="flex-1 ms-2 min-w-0">
            <SearchBar />
          </div>
        {/if}
      </div>

      <!-- Navigation Items -->
      <div
        class={`flex items-center ${$user.signedin ? "space-x-1 sm:space-x-2" : "space-x-3"}`}
      >
        {#if $user.signedin}
          <button
            aria-label={$newNotificationType ? $_("notifications") + " (new)" : $_("notifications")}
            onclick={() => handleMenuItemClick("/notifications")}
            class="p-2 rounded-full hover:bg-gray-100 text-gray-400 hover:text-gray-700 transition-colors relative focus:outline-none"
          >
            <svg
              class="w-5 h-5 {renderNotificationIconColor()}"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
              aria-hidden="true"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9"
              />
            </svg>
            <span class="sr-only" aria-live="polite">
              {$newNotificationType ? $_("notifications") + ": new" : ""}
            </span>
            {#if $newNotificationType}
              <span
                class="absolute top-2 right-2 h-2 w-2 rounded-full bg-red-500 border border-white animate-pulse"
                aria-hidden="true"
              ></span>
            {/if}
          </button>

          <!-- Menu Dropdown -->
          <div class="relative menu-container flex items-center">
            <button
              onclick={toggleMenu}
              class="p-2 rounded-full hover:bg-gray-100 text-gray-400 hover:text-gray-700 transition-colors relative menu-trigger focus:outline-none"
              aria-label={$_("menu") || "Menu"}
              title={$_("menu") || "Menu"}
              aria-expanded={isMenuOpen}
              aria-controls="dashboard-main-menu"
              aria-haspopup="menu"
            >
              <svg
                class="w-5 h-5"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
                aria-hidden="true"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="1.5"
                  d="M4 6h16M4 12h16M4 18h16"
                />
              </svg>
            </button>

            <!-- Avatar Trigger (also opens profile maybe? or just menu?) -->
            <button
              aria-label={$_("my_profile") || "Profile Menu"}
              onclick={toggleMenu}
              class="avatar-btn"
              aria-expanded={isMenuOpen}
              aria-controls="dashboard-main-menu"
              aria-haspopup="menu"
            >
              {getInitials($user)}
            </button>

            {#if isMenuOpen}
              <div
                id="dashboard-main-menu"
                role="menu"
                class="dropdown-menu {isRTL
                  ? 'dropdown-menu-rtl'
                  : 'dropdown-menu-ltr'}"
              >
                <div class="dropdown-scroll">
                  {#if $user.signedin && $roles.includes("super_admin")}
                    <div class="menu-section">
                      <div class="menu-section-title">{$_("admin")}</div>
                      <button
                        aria-label={`Admin Dashboard`}
                        onclick={() => handleMenuItemClick("/dashboard/admin")}
                        class="menu-item"
                      >
                        <svg
                          class="menu-icon"
                          fill="none"
                          stroke="currentColor"
                          viewBox="0 0 24 24"
                        >
                          <path
                            stroke-linecap="round"
                            stroke-linejoin="round"
                            stroke-width="2"
                            d="M3 13h8V3H3v10zm0 8h8v-6H3v6zm10 0h8v-10h-8v10zm0-18v6h8V3h-8z"
                          />
                        </svg>
                        <span>{$_("dashboard")}</span>
                      </button>
                      <button
                        aria-label={`Contact Messages`}
                        onclick={() =>
                          handleMenuItemClick(
                            "/dashboard/admin/contact-messages",
                          )}
                        class="menu-item"
                      >
                        <svg
                          class="menu-icon"
                          fill="none"
                          stroke="currentColor"
                          viewBox="0 0 24 24"
                        >
                          <path
                            stroke-linecap="round"
                            stroke-linejoin="round"
                            stroke-width="2"
                            d="M8 12h.01M12 12h.01M16 12h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z"
                          />
                        </svg>
                        <span>{$_("contact_messages")}</span>
                      </button>
                      <button
                        aria-label={`Reports`}
                        onclick={() =>
                          handleMenuItemClick("/dashboard/reports")}
                        class="menu-item"
                      >
                        <svg
                          class="menu-icon"
                          xmlns="http://www.w3.org/2000/svg"
                          fill="none"
                          stroke="currentColor"
                          viewBox="0 0 24 24"
                        >
                          <path
                            stroke-linecap="round"
                            stroke-linejoin="round"
                            stroke-width="2"
                            d="M9 12h6m-6 4h6M9 8h1m3.5-6H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8.5L14.5 2z"
                          />
                        </svg>

                        <span>{$_("reports._val")}</span>
                      </button>
                      <button
                        aria-label={`Manage Permissions`}
                        onclick={() =>
                          handleMenuItemClick("/dashboard/permissions")}
                        class="menu-item"
                      >
                        <svg
                          class="menu-icon"
                          fill="none"
                          stroke="currentColor"
                          viewBox="0 0 24 24"
                        >
                          <path
                            stroke-linecap="round"
                            stroke-linejoin="round"
                            stroke-width="2"
                            d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"
                          />
                          <path
                            stroke-linecap="round"
                            stroke-linejoin="round"
                            stroke-width="2"
                            d="M9 12l2 2 4-4"
                          />
                        </svg>
                        <span>{$_("permission")}</span>
                      </button>
                      <button
                        aria-label={`Manage Roles`}
                        onclick={() => handleMenuItemClick("/dashboard/roles")}
                        class="menu-item"
                      >
                        <svg
                          class="menu-icon"
                          fill="none"
                          stroke="currentColor"
                          viewBox="0 0 24 24"
                        >
                          <path
                            stroke-linecap="round"
                            stroke-linejoin="round"
                            stroke-width="2"
                            d="M5.121 17.804A9 9 0 1112 21v-1a7 7 0 100-14v1m0 4a3 3 0 013 3 3 3 0 01-3 3 3 3 0 01-3-3 3 3 0 013-3z"
                          />
                        </svg>

                        <span>{$_("roles")}</span>
                      </button>
                      <button
                        aria-label={`Manage Users`}
                        onclick={() =>
                          handleMenuItemClick("/dashboard/admin/users")}
                        class="menu-item"
                      >
                        <svg
                          class="menu-icon"
                          fill="none"
                          stroke="currentColor"
                          viewBox="0 0 24 24"
                        >
                          <path
                            stroke-linecap="round"
                            stroke-linejoin="round"
                            stroke-width="2"
                            d="M15 17h5v-1a4 4 0 00-4-4h-1M9 17H4v-1a4 4 0 014-4h1m3-4a3 3 0 11-6 0 3 3 0 016 0zm6 0a3 3 0 11-6 0 3 3 0 016 0z"
                          />
                        </svg>

                        <span>{$_("Users")}</span>
                      </button>
                      <button
                        aria-label={`Manage Configurations`}
                        onclick={() =>
                          handleMenuItemClick("/dashboard/admin/configs")}
                        class="menu-item"
                      >
                        <svg
                          class="menu-icon"
                          fill="none"
                          stroke="currentColor"
                          viewBox="0 0 24 24"
                        >
                          <path
                            stroke-linecap="round"
                            stroke-linejoin="round"
                            stroke-width="2"
                            d="M11.983 13.983a2 2 0 100-4 2 2 0 000 4zM19.4 15a1.65 1.65 0 01.33 1.82l-.58 1a1.65 1.65 0 01-1.51.88h-1.12a6.66 6.66 0 01-1.3.76l-.17 1.12a1.65 1.65 0 01-.88 1.51l-1 .58a1.65 1.65 0 01-1.82-.33l-.8-.8a6.66 6.66 0 01-.76-1.3H7.4a1.65 1.65 0 01-1.51-.88l-.58-1a1.65 1.65 0 01.33-1.82l.8-.8a6.66 6.66 0 010-1.52l-.8-.8a1.65 1.65 0 01-.33-1.82l.58-1a1.65 1.65 0 011.51-.88h1.12c.23-.46.49-.89.76-1.3l-.17-1.12a1.65 1.65 0 01.88-1.51l1-.58a1.65 1.65 0 011.82.33l.8.8c.51-.13 1.03-.24 1.52-.24s1.01.11 1.52.24l.8-.8a1.65 1.65 0 011.82-.33l1 .58a1.65 1.65 0 01.88 1.51l-.17 1.12c.46.23.89.49 1.3.76h1.12a1.65 1.65 0 011.51.88l.58 1a1.65 1.65 0 01-.33 1.82l-.8.8c.13.51.24 1.03.24 1.52s-.11 1.01-.24 1.52l.8.8z"
                          />
                        </svg>

                        <span>{$_("DefaultRole")}</span>
                      </button>
                      <button
                        aria-label={`Manage Templates`}
                        onclick={() =>
                          handleMenuItemClick("/dashboard/templates")}
                        class="menu-item"
                      >
                        <svg
                          class="menu-icon"
                          fill="none"
                          stroke="currentColor"
                          viewBox="0 0 24 24"
                        >
                          <path
                            stroke-linecap="round"
                            stroke-linejoin="round"
                            stroke-width="2"
                            d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8l6 6v4a2 2 0 01-2 2h-2M8 16v2a2 2 0 002 2h6a2 2 0 002-2v-2H8z"
                          />
                        </svg>

                        <span>{$_("templates._val")}</span>
                      </button>
                    </div>
                    <div class="menu-divider"></div>
                  {/if}

                  <!-- Main Navigation -->
                  <div class="menu-section">
                    <button
                      aria-label={$_("dashboard") || "Dashboard"}
                      onclick={() => handleMenuItemClick("/dashboard")}
                      class="menu-item"
                    >
                      <svg
                        class="menu-icon"
                        fill="none"
                        stroke="currentColor"
                        viewBox="0 0 24 24"
                      >
                        <path
                          stroke-linecap="round"
                          stroke-linejoin="round"
                          stroke-width="2"
                          d="M3 13h8V3H3v10zm0 8h8v-6H3v6zm10 0h8v-10h-8v10zm0-18v6h8V3h-8z"
                        />
                      </svg>
                      <span>{$_("dashboard") || "Dashboard"}</span>
                    </button>
                    {#if website.enable_chat}
                      <button
                        aria-label={`Chat & Messaging`}
                        onclick={() => handleMenuItemClick("/messaging")}
                        class="menu-item"
                      >
                        <svg
                          class="menu-icon"
                          fill="none"
                          stroke="currentColor"
                          viewBox="0 0 24 24"
                        >
                          <path
                            stroke-linecap="round"
                            stroke-linejoin="round"
                            stroke-width="2"
                            d="M7 8h10M7 12h6m-9 8l2-4h10a4 4 0 004-4V6a4 4 0 00-4-4H6a4 4 0 00-4 4v10a4 4 0 004 4z"
                          />
                        </svg>

                        <span>{$_("chat")}</span>
                      </button>
                    {/if}
                    {#if website.enable_surveys}
                      <button
                        aria-label={`Surveys`}
                        onclick={() => handleMenuItemClick("/surveys")}
                        class="menu-item"
                      >
                        <svg
                          xmlns="http://www.w3.org/2000/svg"
                          viewBox="0 0 24 24"
                          width="24"
                          height="24"
                          fill="none"
                          stroke="currentColor"
                          stroke-width="1.6"
                          stroke-linecap="round"
                          stroke-linejoin="round"
                          class="menu-icon"
                        >
                          <rect
                            x="4"
                            y="3"
                            width="16"
                            height="18"
                            rx="2"
                            ry="2"
                          />
                          <line x1="8" y1="8" x2="16" y2="8" />
                          <line x1="8" y1="12" x2="16" y2="12" />
                          <line x1="8" y1="16" x2="13" y2="16" />
                          <polyline points="6 16 7.5 17.5 10 15" />
                        </svg>

                        <span>{$_("surveys._val")}</span>
                      </button>
                    {/if}

                    {#if website.enable_notifications}
                      <button
                        aria-label={`Notifications`}
                        onclick={() => handleMenuItemClick("/notifications")}
                        class="menu-item"
                      >
                        <svg
                          class="menu-icon {renderNotificationIconColor()}"
                          fill="none"
                          stroke="currentColor"
                          viewBox="0 0 24 24"
                        >
                          <path
                            stroke-linecap="round"
                            stroke-linejoin="round"
                            stroke-width="2"
                            d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9"
                          />
                        </svg>
                        <span>{$_("notifications")}</span>
                        {#if $newNotificationType}
                          <span class="notification-badge"></span>
                        {/if}
                      </button>
                    {/if}
                    <button
                      aria-label={`My Profile`}
                      onclick={() => handleMenuItemClick("/me")}
                      class="menu-item"
                    >
                      <svg
                        class="menu-icon"
                        fill="none"
                        stroke="currentColor"
                        viewBox="0 0 24 24"
                      >
                        <path
                          stroke-linecap="round"
                          stroke-linejoin="round"
                          stroke-width="2"
                          d="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z"
                        />
                      </svg>
                      <span>{$_("my_profile")}</span>
                    </button>
                  </div>

                </div>

                <!-- Pinned bottom: language + logout -->
                <div class="dropdown-footer">
                  <div class="menu-item language-item">
                    <svg
                      class="menu-icon"
                      fill="none"
                      stroke="currentColor"
                      viewBox="0 0 24 24"
                    >
                      <path
                        stroke-linecap="round"
                        stroke-linejoin="round"
                        stroke-width="2"
                        d="M3 5h12M9 3v2m1.048 9.5A18.022 18.022 0 016.412 9m6.088 9h7M11 21l5-10 5 10M12.751 5C11.783 10.77 8.07 15.61 3 18.129"
                      />
                    </svg>
                    <label for="language-select" class="sr-only">
                      {$_("language_select")}
                    </label>
                    <select
                      id="language-select"
                      aria-label={$_("language_select")}
                      bind:value={$locale}
                      onchange={(e) =>
                        switchLocale((e.target as HTMLSelectElement).value)}
                      class="language-select-dropdown"
                    >
                      <option value="en">{$_("english")}</option>
                      <option value="ar">{$_("arabic")}</option>
                      <option value="ku">{$_("kurdish")}</option>
                    </select>
                  </div>
                  <button
                    aria-label={`Logout`}
                    onclick={handleLogout}
                    class="menu-item logout-item"
                  >
                    <svg
                      class="menu-icon"
                      fill="none"
                      stroke="currentColor"
                      viewBox="0 0 24 24"
                    >
                      <path
                        stroke-linecap="round"
                        stroke-linejoin="round"
                        stroke-width="2"
                        d="M17 16l4-4m0 0l-4-4m4 4H7m6 4v1a3 3 0 01-3 3H6a3 3 0 01-3-3V7a3 3 0 013-3h4a3 3 0 013 3v1"
                      />
                    </svg>
                    <span>{$_("sign_out")}</span>
                  </button>
                </div>
              </div>
            {/if}
          </div>

        {:else}
          <div class="flex items-center space-x-3">
            <div class="relative">
              <label for="language-select-guest" class="sr-only">
                {$_("language_select")}
              </label>
              <select
                id="language-select-guest"
                aria-label={$_("language_select")}
                bind:value={$locale}
                onchange={(e) =>
                  switchLocale((e.target as HTMLSelectElement).value)}
                class="language-select"
              >
                <option value="en">EN</option>
                <option value="ar">AR</option>
                <option value="ku">KU</option>
              </select>
            </div>

            <button onclick={handleLogin} class="login-btn">
              {$_("Login")}
            </button>
          </div>
        {/if}
      </div>
    </div>
  </div>
</header>

<style>
  .nav-icon-btn {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 2.5rem;
    height: 2.5rem;
    border-radius: var(--radius-lg);
    background: var(--color-gray-50);
    border: 1px solid var(--color-gray-200);
    transition: all var(--duration-normal) var(--ease-out);
    position: relative;
    cursor: pointer;
    text-decoration: none;
  }

  .nav-icon-btn:hover {
    background: white;
    border-color: var(--color-gray-300);
    box-shadow: var(--shadow-md);
    transform: translateY(-1px);
  }

  .nav-icon-btn:active {
    transform: translateY(0);
    box-shadow: var(--shadow-sm);
  }

  .menu-trigger[aria-expanded="true"] {
    background: var(--color-primary-50);
    border-color: var(--color-primary-200);
  }

  .nav-icon {
    width: 1.125rem;
    height: 1.125rem;
    color: var(--color-gray-500);
    transition: color var(--duration-fast) ease;
  }

  .nav-icon-btn:hover .nav-icon {
    color: var(--color-gray-700);
  }

  .dropdown-menu {
    position: absolute;
    top: calc(100% + 0.625rem);
    z-index: 50;
    min-width: 15rem;
    background: rgba(255, 255, 255, 0.92);
    backdrop-filter: blur(16px);
    border: 1px solid var(--color-gray-100);
    border-radius: var(--radius-xl);
    box-shadow: var(--shadow-xl), 0 0 0 1px rgba(0,0,0,0.03);
    animation: dropdown-enter 0.25s var(--ease-out);
    overflow: hidden;
    overscroll-behavior: contain;
  }

  @keyframes dropdown-enter {
    from { opacity: 0; transform: translateY(-6px) scale(0.97); }
    to { opacity: 1; transform: translateY(0) scale(1); }
  }

  .dropdown-scroll {
    padding: 0.5rem;
    max-height: 60vh;
    overflow-y: auto;
  }

  .dropdown-footer {
    padding: 0.5rem;
    border-top: 1px solid var(--color-gray-100);
  }

  .menu-section {
    margin-bottom: 0.25rem;
  }

  .menu-section:last-child {
    margin-bottom: 0;
  }

  .menu-section-title {
    font-size: 0.6875rem;
    font-weight: 600;
    color: var(--color-gray-400);
    text-transform: uppercase;
    letter-spacing: 0.06em;
    padding: 0.5rem 0.75rem 0.25rem;
  }

  .menu-item {
    display: flex;
    align-items: center;
    width: 100%;
    padding: 0.5rem 0.75rem;
    border: none;
    background: none;
    border-radius: var(--radius-md);
    cursor: pointer;
    transition: background var(--duration-fast) ease,
                color var(--duration-fast) ease;
    text-align: left;
    color: var(--color-gray-700);
    font-size: 0.8125rem;
    font-weight: 500;
    position: relative;
    gap: 0.625rem;
  }

  .menu-item:hover {
    background: var(--color-gray-50);
    color: var(--color-gray-900);
  }

  .menu-item:active {
    background: var(--color-gray-100);
  }

  .menu-icon {
    width: 1.125rem;
    height: 1.125rem;
    flex-shrink: 0;
    color: var(--color-gray-400);
    transition: color var(--duration-fast) ease;
  }

  .menu-item:hover .menu-icon {
    color: var(--color-primary-500);
  }

  .menu-item span {
    flex: 1;
  }

  .logout-item:hover {
    background: #fef2f2;
    color: var(--color-error);
  }

  .logout-item:hover .menu-icon {
    color: var(--color-error);
  }

  .language-item {
    padding: 0.375rem 0.75rem;
  }

  .language-select-dropdown {
    appearance: none;
    background: transparent;
    border: none;
    color: var(--color-gray-700);
    font-weight: 500;
    font-size: 0.8125rem;
    cursor: pointer;
    outline: none;
    flex: 1;
  }

  .menu-divider {
    height: 1px;
    background: var(--color-gray-100);
    margin: 0.375rem 0.5rem;
  }

  .notification-badge {
    width: 0.4375rem;
    height: 0.4375rem;
    background: var(--color-error);
    border-radius: var(--radius-full);
    margin-left: auto;
    animation: pulse-soft 2s ease-in-out infinite;
  }

  .login-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    padding: 0.5rem 1.25rem;
    background: var(--gradient-brand);
    color: white;
    font-weight: 600;
    font-size: 0.8125rem;
    border: none;
    border-radius: var(--radius-lg);
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    box-shadow: var(--shadow-brand);
  }

  .login-btn:hover {
    background: var(--gradient-brand-hover);
    transform: translateY(-1px);
    box-shadow: var(--shadow-brand-lg);
  }

  .login-btn:active {
    transform: translateY(0);
  }

  .language-select {
    appearance: none;
    background: var(--color-gray-50);
    border: 1px solid var(--color-gray-200);
    border-radius: var(--radius-md);
    padding: 0.375rem 1.75rem 0.375rem 0.625rem;
    color: var(--color-gray-600);
    font-weight: 500;
    font-size: 0.8125rem;
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    background-image: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' fill='none' viewBox='0 0 20 20'%3e%3cpath stroke='%239ca3af' stroke-linecap='round' stroke-linejoin='round' stroke-width='1.5' d='m6 8 4 4 4-4'/%3e%3c/svg%3e");
    background-position: right 0.375rem center;
    background-repeat: no-repeat;
    background-size: 0.875rem 0.875rem;
    min-width: 3.75rem;
  }

  .language-select:hover {
    background-color: white;
    border-color: var(--color-gray-300);
  }

  .language-select:focus {
    outline: none;
    box-shadow: 0 0 0 2px var(--color-primary-200);
    border-color: var(--color-primary-400);
  }

  @keyframes pulse-soft {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.5; }
  }

  .nav-icon-btn:focus {
    outline: none;
    box-shadow: 0 0 0 2px white, 0 0 0 4px var(--color-primary-300);
  }

  .login-btn:focus {
    outline: none;
    box-shadow: 0 0 0 2px white, 0 0 0 4px var(--color-primary-300);
  }

  .menu-item:focus {
    outline: none;
    background: var(--color-primary-50);
    color: var(--color-primary-700);
  }

  .avatar-btn {
    margin-left: 0.25rem;
    display: flex;
    align-items: center;
    justify-content: center;
    width: 2.125rem;
    height: 2.125rem;
    border-radius: var(--radius-full);
    background: var(--gradient-brand);
    color: white;
    font-weight: 600;
    font-size: 0.6875rem;
    border: 2px solid transparent;
    cursor: pointer;
    box-shadow: var(--shadow-xs);
    transition: all var(--duration-normal) var(--ease-out);
  }

  .avatar-btn:hover {
    box-shadow: 0 0 0 3px var(--color-primary-100);
    transform: scale(1.05);
  }

  .dropdown-menu-ltr { right: 0; }
  .dropdown-menu-rtl { left: 0; }

  @media (max-width: 640px) {
    .nav-icon-btn { width: 2.25rem; height: 2.25rem; }
    .nav-icon { width: 1rem; height: 1rem; }
    .login-btn { padding: 0.5rem 1rem; font-size: 0.75rem; }
    .language-select { padding: 0.375rem 1.5rem 0.375rem 0.5rem; font-size: 0.75rem; min-width: 3.25rem; }
    .dropdown-menu { min-width: 13rem; }
    .dropdown-menu-ltr { right: -0.5rem; }
    .dropdown-menu-rtl { left: -0.5rem; }
  }
</style>
