<script lang="ts">
    import {goto} from "@roxi/routify";
    import {_, locale} from "@/i18n";
    import {
        ArrowLeftOutline,
        CheckCircleSolid,
        EnvelopeSolid,
        MailBoxOutline,
        MessagesSolid,
        UserSolid,
    } from "flowbite-svelte-icons";
    import {contactUs} from "@/stores/user";

    $: isRTL = $locale === "ar" || $locale === "ku";

  let name = "";
  let email = "";
  let subject = "";
  let message = "";
  let isSubmitting = false;
  let showSuccess = false;
  let showError = false;
  let errors: Record<string, any> = {};

  function validateForm() {
    const newErrors: Record<string, any> = {};

    if (!name.trim()) {
      newErrors.name = $_("NameRequired");
    }

    if (!message.trim()) {
      newErrors.message = $_("MessageRequired");
    } else if (message.trim().length < 10) {
      newErrors.message = $_("MessageTooShort");
    } else if (message.trim().length > 1000) {
      newErrors.message = $_("MessageTooLong");
    }

    errors = newErrors;
    return Object.keys(newErrors).length === 0;
  }

  async function handleSubmit(event: any) {
    event.preventDefault();

    if (!validateForm()) {
      return;
    }

    isSubmitting = true;
    showError = false;

    try {
      await contactUs(name, email, message, subject);

      name = "";
      email = "";
      subject = "";
      message = "";
      errors = {};
      showSuccess = true;

      setTimeout(() => {
        showSuccess = false;
      }, 5000);
    } catch (error) {
      showError = true;
      setTimeout(() => {
        showError = false;
      }, 5000);
    } finally {
      isSubmitting = false;
    }
  }

  function goBack() {
    $goto("/");
  }
</script>

<div class="contact-container">
  <div class="contact-content">
    <div class="contact-header">
      <button
        aria-label={`Go back`}
        onclick={() => history.back()}
        class="btn-back"
      >
        <ArrowLeftOutline
          class="w-4 h-4 rtl:rotate-180"
        />
        {$_("Back")}
      </button>

      <div class="header-content">
        <div class="icon-wrapper">
          <MessagesSolid class="header-icon text-white w-6 h-6" />
        </div>
        <h1 class="contact-title">{$_("ContactUs")}</h1>
        <h2 class="contact-subtitle">{$_("ContactUsTitle")}</h2>
        <p class="contact-description">{$_("ContactUsDescription")}</p>
      </div>
    </div>

    {#if showSuccess}
      <div class="success-message" class:rtl={isRTL}>
        <CheckCircleSolid class="success-icon" />
        <div class="success-content">
          <h3 class="success-title">{$_("MessageSent")}</h3>
          <p class="success-description">{$_("MessageSentDescription")}</p>
        </div>
      </div>
    {/if}

    {#if showError}
      <div class="error-message" class:rtl={isRTL}>
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
          <p class="error-text">{$_("MessageError")}</p>
        </div>
      </div>
    {/if}

    <div class="form-container">
      <form onsubmit={handleSubmit} class="contact-form">
        <div class="form-group">
          <label for="name" class="form-label" class:rtl={isRTL}>
            <UserSolid class="label-icon" />
            {$_("YourName")}
          </label>
          <input
            id="name"
            type="text"
            bind:value={name}
            placeholder={$_("YourNamePlaceholder")}
            class="form-input"
            class:error={errors.name}
            class:rtl={isRTL}
            disabled={isSubmitting}
          />
          {#if errors.name}
            <p class="error-text-small" class:rtl={isRTL}>{errors.name}</p>
          {/if}
        </div>
        <div class="form-group">
          <label for="subject" class="form-label" class:rtl={isRTL}>
            <UserSolid class="label-icon" />
            {$_("YourSubject")}
          </label>
          <input
            id="subject"
            type="text"
            bind:value={subject}
            placeholder={$_("YourSubjectPlaceholder")}
            class="form-input"
            class:error={errors.subject}
            class:rtl={isRTL}
            disabled={isSubmitting}
          />
          {#if errors.subject}
            <p class="error-text-small" class:rtl={isRTL}>{errors.subject}</p>
          {/if}
        </div>
        <div class="form-group">
          <label for="email" class="form-label" class:rtl={isRTL}>
            <MailBoxOutline class="label-icon" />
            {$_("YourEmail")}
          </label>
          <input
            id="email"
            type="text"
            bind:value={email}
            placeholder={$_("YourEmailPlaceholder")}
            class="form-input"
            class:error={errors.email}
            class:rtl={isRTL}
            disabled={isSubmitting}
          />
          {#if errors.email}
            <p class="error-text-small" class:rtl={isRTL}>{errors.email}</p>
          {/if}
        </div>

        <div class="form-group">
          <label for="message" class="form-label" class:rtl={isRTL}>
            <EnvelopeSolid class="label-icon" />
            {$_("YourMessage")}
          </label>
          <textarea
            id="message"
            bind:value={message}
            placeholder={$_("YourMessagePlaceholder")}
            rows="6"
            class="form-textarea"
            class:error={errors.message}
            class:rtl={isRTL}
            disabled={isSubmitting}
          ></textarea>
          <div class="character-count" class:rtl={isRTL}>
            <span class:over-limit={message.length > 1000}>
              {message.length}/1000
            </span>
          </div>
          {#if errors.message}
            <p class="error-text-small" class:rtl={isRTL}>{errors.message}</p>
          {/if}
        </div>

        <button
          aria-label={`Send message`}
          type="submit"
          class="submit-button"
          class:loading={isSubmitting}
          class:rtl={isRTL}
          disabled={isSubmitting}
        >
          {#if isSubmitting}
            <div class="loading-spinner"></div>
            {$_("SendingMessage")}
          {:else}
            <EnvelopeSolid class="button-icon" />
            {$_("SendMessage")}
          {/if}
        </button>
      </form>
    </div>

    <div class="additional-info">
      <div class="info-card">
        <MessagesSolid class="info-icon" />
        <div class="info-content">
          <h3 class="info-title">{$_("Welcome")}</h3>
          <p class="info-description">
            {isRTL
              ? "نحن نقدر ملاحظاتك ونسعى لتحسين تجربتك معنا باستمرار."
              : "We value your feedback and strive to continuously improve your experience with us."}
          </p>
        </div>
      </div>
    </div>
  </div>
</div>

<style>
  .contact-container {
    min-height: 100vh;
    background: var(--gradient-page);
    padding: 2rem 1rem;
  }

  .contact-content {
    max-width: 560px;
    margin: 0 auto;
    animation: fadeInUp var(--duration-slow) var(--ease-out);
  }

  .contact-header {
    text-align: center;
    margin-bottom: 1.5rem;
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
    box-shadow: var(--shadow-brand);
  }

  .contact-title {
    font-size: 2rem;
    font-weight: 700;
    color: var(--color-gray-900);
    margin-bottom: 0.375rem;
    letter-spacing: -0.02em;
  }

  .contact-subtitle {
    font-size: 1.25rem;
    font-weight: 600;
    color: var(--color-gray-700);
    margin-bottom: 0.75rem;
  }

  .contact-description {
    font-size: 0.9375rem;
    color: var(--color-gray-500);
    line-height: 1.6;
  }

  .success-message, .error-message {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.875rem 1rem;
    border-radius: var(--radius-lg);
    margin-bottom: 1.5rem;
    animation: fadeInDown var(--duration-normal) var(--ease-out);
  }

  .success-message { background: #f0fdf4; border: 1px solid #bbf7d0; }
  .error-message { background: #fef2f2; border: 1px solid #fecaca; }

  .success-title {
    font-weight: 600;
    color: var(--color-success);
    margin-bottom: 0.125rem;
    font-size: 0.875rem;
  }

  .success-description, .error-text {
    color: var(--color-gray-700);
    font-size: 0.8125rem;
  }

  .form-container {
    background: white;
    border-radius: var(--radius-2xl);
    padding: 2rem;
    box-shadow: var(--shadow-lg);
    border: 1px solid rgba(255, 255, 255, 0.8);
    margin-bottom: 1.5rem;
  }

  .contact-form {
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

  .form-input, .form-textarea {
    padding: 0.6875rem 0.875rem;
    border: 1.5px solid var(--color-gray-200);
    border-radius: var(--radius-lg);
    font-size: 0.9375rem;
    transition: all var(--duration-normal) var(--ease-out);
    background: var(--color-gray-50);
    color: var(--color-gray-800);
  }

  .form-input::placeholder, .form-textarea::placeholder { color: var(--color-gray-400); }
  .form-input:hover, .form-textarea:hover { border-color: var(--color-gray-300); }

  .form-input:focus, .form-textarea:focus {
    outline: none;
    border-color: var(--color-primary-400);
    box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.1);
    background: white;
  }

  .form-input.error, .form-textarea.error {
    border-color: var(--color-error);
    box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.08);
  }

  .form-input.rtl, .form-textarea.rtl { text-align: right; }

  .form-textarea { resize: vertical; min-height: 100px; font-family: inherit; }

  .character-count { font-size: 0.6875rem; color: var(--color-gray-400); text-align: right; }
  .character-count.rtl { text-align: left; }
  .over-limit { color: var(--color-error); font-weight: 600; }

  .error-text-small { font-size: 0.75rem; color: var(--color-error); font-weight: 500; }
  .error-text-small.rtl { text-align: right; }

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

  .submit-button:active:not(:disabled) { transform: translateY(0); }
  .submit-button:disabled { opacity: 0.6; cursor: not-allowed; transform: none; }

  .loading-spinner {
    width: 1rem;
    height: 1rem;
    border: 2px solid rgba(255, 255, 255, 0.3);
    border-top: 2px solid white;
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
  }

  .additional-info { margin-top: 1.5rem; }

  .info-card {
    background: white;
    border-radius: var(--radius-xl);
    padding: 1.25rem;
    box-shadow: var(--shadow-sm);
    border: 1px solid var(--color-gray-100);
    display: flex;
    align-items: center;
    gap: 0.875rem;
  }

  .info-title { font-weight: 600; color: var(--color-gray-800); margin-bottom: 0.25rem; font-size: 0.9375rem; }
  .info-description { color: var(--color-gray-500); font-size: 0.8125rem; line-height: 1.5; }

  @media (max-width: 640px) {
    .contact-container { padding: 1rem; }
    .contact-title { font-size: 1.5rem; }
    .contact-subtitle { font-size: 1.0625rem; }
    .form-container { padding: 1.5rem; border-radius: var(--radius-xl); }
    .info-card { flex-direction: column; text-align: center; }
  }
</style>
