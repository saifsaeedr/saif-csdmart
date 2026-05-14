<script lang="ts">
  import { goto } from "@roxi/routify";
  import { _, locale } from "@/i18n";
  import {
    EyeSlashSolid,
    EyeSolid,
    LockSolid,
    UserSolid,
  } from "flowbite-svelte-icons";
  import { loginBy, signin } from "@/stores/user";

  $goto;
  let identifier = "";
  let password = "";
  let showPassword = false;
  let isSubmitting = false;
  let showError = false;
  let errors: { identifier?: string; password?: string } = {};
  let isError: boolean;
  $: isRTL = $locale === "ar" || $locale === "ku";

  function isEmail(input: string): boolean {
    return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(input);
  }

  async function handleSubmit(event: Event) {
    event.preventDefault();
    isError = false;
    showError = false;
    errors = {};
    isSubmitting = true;

    const trimmedIdentifier = identifier.trim();

    if (!trimmedIdentifier || !password) {
      if (!trimmedIdentifier) errors.identifier = $_("ThisFieldIsRequired");
      if (!password) errors.password = $_("ThisFieldIsRequired");
      isSubmitting = false;
      return;
    }

    try {
      if (isEmail(trimmedIdentifier)) {
        await loginBy(trimmedIdentifier, password);
      } else {
        await signin(trimmedIdentifier, password);
      }
      $goto("/catalogs");
    } catch (error) {
      isError = true;
      showError = true;
    } finally {
      isSubmitting = false;
    }
  }

  function togglePasswordVisibility() {
    showPassword = !showPassword;
  }

  function goToRegister() {
    $goto("/register");
  }

  function goBack() {
    $goto("/");
  }
</script>

<div class="login-container">
  <div class="login-content">
    <div class="login-header">
      <div class="header-content">
        <div class="icon-wrapper">
          <UserSolid class="header-icon text-white w-6 h-6" />
        </div>
        <h1 class="login-title">{$_("WelcomeBack")}</h1>
        <p class="login-description">{$_("PleaseSignInToContinue")}</p>
      </div>
    </div>

    {#if showError}
      <div class="error-message" class:rtl={isRTL} role="alert">
        <svg
          class="shrink-0 inline w-4 h-4 me-3"
          aria-hidden="true"
          xmlns="http://www.w3.org/2000/svg"
          fill="currentColor"
          viewBox="0 0 20 20"
        >
          <path
            d="M10 .5a9.5 9.5 0 1 0 9.5 9.5A9.51 9.51 0 0 0 10 .5ZM9.5 4a1.5 1.5 0 1 1 0 3 1.5 1.5 0 0 1 0-3ZM12 15H8a1 1 0 0 1 0-2h1v-3H8a1 1 0 0 1 0-2h2a1 1 0 0 1 1 1v4h1a1 1 0 0 1 0 2Z"
          />
        </svg>
        <div class="error-content">
          <p class="error-text">{$_("InvalidCredentials")}</p>
        </div>
      </div>
    {/if}

    <div class="form-container">
      <form onsubmit={handleSubmit} class="login-form">
        <div class="form-group">
          <label for="identifier" class="form-label" class:rtl={isRTL}>
            <UserSolid class="label-icon" />
            {$_("Username")} / {$_("Email")}
          </label>
          <input
            id="identifier"
            type="text"
            bind:value={identifier}
            placeholder={$_("Username") + " " + $_("or") + " " + $_("Email")}
            class="form-input"
            class:error={errors.identifier}
            class:rtl={isRTL}
            disabled={isSubmitting}
            aria-invalid={!!errors.identifier}
            aria-describedby={errors.identifier ? "identifier-error" : undefined}
          />
          {#if errors.identifier}
            <p id="identifier-error" class="error-text-small" class:rtl={isRTL} role="alert">
              {errors.identifier}
            </p>
          {/if}
        </div>

        <div class="form-group">
          <label for="password" class="form-label" class:rtl={isRTL}>
            <LockSolid class="label-icon" />
            {$_("Password")}
          </label>
          <div class="password-input-wrapper" class:rtl={isRTL}>
            <input
              id="password"
              type={showPassword ? "text" : "password"}
              bind:value={password}
              placeholder={$_("Password")}
              class="form-input password-input"
              class:error={errors.password}
              class:rtl={isRTL}
              disabled={isSubmitting}
              aria-invalid={!!errors.password}
              aria-describedby={errors.password ? "password-error" : undefined}
            />
            <button
              aria-label={$_("Password")}
              aria-pressed={showPassword}
              type="button"
              class="password-toggle"
              onclick={togglePasswordVisibility}
              class:rtl={isRTL}
            >
              {#if showPassword}
                <EyeSlashSolid class="toggle-icon" />
              {:else}
                <EyeSolid class="toggle-icon" />
              {/if}
            </button>
          </div>
          {#if errors.password}
            <p id="password-error" class="error-text-small" class:rtl={isRTL} role="alert">{errors.password}</p>
          {/if}
        </div>

        <button
          type="submit"
          class="submit-button"
          class:loading={isSubmitting}
          class:rtl={isRTL}
          disabled={isSubmitting}
          aria-label={`Sign in`}
        >
          {#if isSubmitting}
            <div class="loading-spinner"></div>
            {$_("SigningIn")}
          {:else}
            <UserSolid class="button-icon" />
            {$_("SignIn")}
          {/if}
        </button>
      </form>

      <div class="register-link" class:rtl={isRTL}>
        <span class="register-text">{$_("DontHaveAccount")}</span>
        <button
          aria-label={`Go to register`}
          class="link-button"
          onclick={goToRegister}
        >
          {$_("Register")}
        </button>
      </div>

      <div class="terms-text" class:rtl={isRTL}>
        <p>{$_("TermsAndConditions")}</p>
      </div>
    </div>
  </div>
</div>

<style>
  .login-container {
    min-height: 100vh;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--gradient-page);
    padding: 2rem 1rem;
    position: relative;
    overflow: hidden;
  }

  .login-container::before {
    content: "";
    position: absolute;
    top: -40%;
    right: -20%;
    width: 60%;
    height: 80%;
    background: radial-gradient(circle, rgba(99, 102, 241, 0.06) 0%, transparent 70%);
    pointer-events: none;
  }

  .login-container::after {
    content: "";
    position: absolute;
    bottom: -30%;
    left: -15%;
    width: 50%;
    height: 60%;
    background: radial-gradient(circle, rgba(139, 92, 246, 0.05) 0%, transparent 70%);
    pointer-events: none;
  }

  .login-content {
    max-width: 420px;
    width: 100%;
    position: relative;
    z-index: 1;
    animation: fadeInUp var(--duration-slow) var(--ease-out);
  }

  .login-header {
    text-align: center;
    margin-bottom: 2rem;
  }

  .header-content {
    margin-bottom: 1.5rem;
  }

  .icon-wrapper {
    width: 3.5rem;
    height: 3.5rem;
    background: var(--gradient-brand);
    border-radius: var(--radius-xl);
    display: flex;
    align-items: center;
    justify-content: center;
    margin: 0 auto 1.25rem auto;
    box-shadow: var(--shadow-brand), 0 0 24px rgba(99, 102, 241, 0.15);
  }

  .login-title {
    font-size: 1.75rem;
    font-weight: 700;
    color: var(--color-gray-900);
    margin-bottom: 0.5rem;
    letter-spacing: -0.02em;
  }

  .login-description {
    font-size: 0.9375rem;
    color: var(--color-gray-500);
    line-height: 1.5;
  }

  .error-message {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.875rem 1rem;
    border-radius: var(--radius-lg);
    margin-bottom: 1.5rem;
    background: #fef2f2;
    border: 1px solid #fecaca;
    animation: fadeInDown var(--duration-normal) var(--ease-out);
  }

  .error-message.rtl {
    flex-direction: row-reverse;
  }

  .error-text {
    color: var(--color-gray-700);
    font-size: 0.8125rem;
    font-weight: 500;
  }

  .form-container {
    background: white;
    border-radius: var(--radius-2xl);
    padding: 2rem;
    box-shadow: var(--shadow-lg), 0 0 0 1px rgba(0, 0, 0, 0.03);
    border: 1px solid rgba(255, 255, 255, 0.8);
    animation: fadeInUp var(--duration-slow) var(--ease-out) 0.1s both;
  }

  .login-form {
    display: flex;
    flex-direction: column;
    gap: 1.25rem;
  }

  .form-group {
    display: flex;
    flex-direction: column;
    gap: 0.375rem;
  }

  .form-label {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    font-weight: 500;
    color: var(--color-gray-700);
    font-size: 0.8125rem;
  }

  .form-label.rtl {
    flex-direction: row-reverse;
  }

  .form-input {
    padding: 0.6875rem 0.875rem;
    border: 1.5px solid var(--color-gray-200);
    border-radius: var(--radius-lg);
    font-size: 0.9375rem;
    transition: all var(--duration-normal) var(--ease-out);
    background: var(--color-gray-50);
    color: var(--color-gray-800);
  }

  .form-input::placeholder {
    color: var(--color-gray-400);
  }

  .form-input:hover {
    border-color: var(--color-gray-300);
  }

  .form-input:focus {
    outline: none;
    border-color: var(--color-primary-400);
    box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.1);
    background: white;
  }

  .form-input.error {
    border-color: var(--color-error);
    box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.08);
  }

  .form-input.rtl {
    text-align: right;
  }

  .password-input-wrapper {
    position: relative;
    display: flex;
    align-items: center;
  }

  .password-input {
    padding-right: 2.75rem;
    width: 100%;
  }

  .password-input.rtl {
    padding-right: 0.875rem;
    padding-left: 2.75rem;
  }

  .password-toggle {
    position: absolute;
    right: 0.625rem;
    background: none;
    border: none;
    cursor: pointer;
    color: var(--color-gray-400);
    padding: 0.25rem;
    border-radius: var(--radius-sm);
    transition: color var(--duration-fast) ease;
  }

  .password-toggle:hover {
    color: var(--color-gray-600);
  }

  .password-toggle.rtl {
    right: auto;
    left: 0.625rem;
  }

  .error-text-small {
    font-size: 0.75rem;
    color: var(--color-error);
    font-weight: 500;
  }

  .error-text-small.rtl {
    text-align: right;
  }

  .submit-button {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    background: var(--gradient-brand);
    color: white;
    font-weight: 600;
    padding: 0.75rem 1.5rem;
    border-radius: var(--radius-lg);
    border: none;
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    font-size: 0.9375rem;
    box-shadow: var(--shadow-brand);
    margin-top: 0.25rem;
  }

  .submit-button:hover:not(:disabled) {
    background: var(--gradient-brand-hover);
    transform: translateY(-1px);
    box-shadow: var(--shadow-brand-lg);
  }

  .submit-button:active:not(:disabled) {
    transform: translateY(0);
    box-shadow: var(--shadow-xs);
  }

  .submit-button:disabled {
    opacity: 0.6;
    cursor: not-allowed;
    transform: none;
  }

  .submit-button.rtl {
    flex-direction: row-reverse;
  }

  .loading-spinner {
    width: 1rem;
    height: 1rem;
    border: 2px solid rgba(255, 255, 255, 0.3);
    border-top: 2px solid white;
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
  }

  .register-link {
    text-align: center;
    margin-top: 1.5rem;
    padding-top: 1.25rem;
    border-top: 1px solid var(--color-gray-100);
  }

  .register-text {
    color: var(--color-gray-500);
    font-size: 0.8125rem;
  }

  .link-button {
    background: none;
    border: none;
    color: var(--color-primary-500);
    font-weight: 600;
    cursor: pointer;
    text-decoration: none;
    font-size: 0.8125rem;
    margin-left: 0.25rem;
    transition: color var(--duration-fast) ease;
  }

  .register-link.rtl .link-button {
    margin-left: 0;
    margin-right: 0.25rem;
  }

  .link-button:hover {
    color: var(--color-primary-700);
    text-decoration: underline;
  }

  .terms-text {
    text-align: center;
    margin-top: 1rem;
    font-size: 0.6875rem;
    color: var(--color-gray-400);
    line-height: 1.5;
  }

  .terms-text.rtl {
    text-align: center;
  }

  @media (max-width: 640px) {
    .login-container {
      padding: 1rem;
      align-items: flex-start;
      padding-top: 3rem;
    }

    .login-title {
      font-size: 1.5rem;
    }

    .form-container {
      padding: 1.5rem;
      border-radius: var(--radius-xl);
    }
  }
</style>
