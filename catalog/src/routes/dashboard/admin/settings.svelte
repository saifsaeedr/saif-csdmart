<script lang="ts">
  import { onMount } from "svelte";
  import { _ } from "@/i18n";
  import { DmartScope } from "@edraj/tsdmart";
  import {
    checkApplicationsFolders,
    createApplicationsFolders,
    ensureCriticalResources,
    checkCriticalResources,
  } from "@/lib/dmart_services";
  import { goto } from "@roxi/routify";

  $goto;

  let missingFolders = $state<string[]>([]);
  let missingWorkflow = $state(false);
  let missingReportSchema = $state(false);
  let missingWorkflowSchema = $state(false);
  let missingCriticalResources = $state<string[]>([]);

  let checking = $state(true);
  let fixing = $state(false);
  let fixed = $state(false);
  let workflowCreated = $state(false);
  let reportSchemaCreated = $state(false);
  let workflowSchemaCreated = $state(false);

  let hasIssues = $derived(
    missingFolders.length > 0 ||
      missingWorkflow ||
      missingReportSchema ||
      missingWorkflowSchema ||
      missingCriticalResources.length > 0,
  );

  async function runCheck() {
    checking = true;
    fixed = false;
    try {
      const [result, criticalResult] = await Promise.all([
        checkApplicationsFolders(DmartScope.managed),
        checkCriticalResources(),
      ]);

      if (!result.exists && result.error !== "permission_denied") {
        missingFolders = result.missing || [];
        missingWorkflow = result.missingWorkflow || false;
        missingReportSchema = result.missingReportSchema || false;
        missingWorkflowSchema = result.missingWorkflowSchema || false;
      } else {
        missingFolders = [];
        missingWorkflow = false;
        missingReportSchema = false;
        missingWorkflowSchema = false;
      }
      missingCriticalResources = criticalResult.missing || [];
    } catch (err) {
      console.error("Error checking resources:", err);
      missingFolders = [];
      missingWorkflow = false;
      missingReportSchema = false;
      missingWorkflowSchema = false;
      missingCriticalResources = [];
    } finally {
      checking = false;
    }
  }

  async function runFix() {
    fixing = true;
    try {
      const [result] = await Promise.all([
        createApplicationsFolders(DmartScope.managed),
        ensureCriticalResources(),
      ]);
      if (result.success) {
        fixed = true;
        missingFolders = [];
        missingWorkflow = false;
        missingReportSchema = false;
        missingWorkflowSchema = false;
        missingCriticalResources = [];
        workflowCreated = result.workflowCreated || false;
        reportSchemaCreated = result.reportSchemaCreated || false;
        workflowSchemaCreated = result.workflowSchemaCreated || false;
      } else {
        missingFolders = result.failed || [];
        missingWorkflow = result.workflowFailed || false;
        missingReportSchema = result.reportSchemaFailed || false;
        missingWorkflowSchema = result.workflowSchemaFailed || false;
      }
    } catch (err) {
      console.error("Error fixing resources:", err);
    } finally {
      fixing = false;
    }
  }

  onMount(runCheck);
</script>

<div class="min-h-screen bg-gray-50">
  <div class="bg-gray-50">
    <div class="container mx-auto px-4 py-8 max-w-375">
      <div class="flex items-center justify-between gap-4">
        <button
          onclick={() => $goto("/dashboard/admin")}
          class="inline-flex items-center gap-2 text-sm font-medium text-gray-600 hover:text-gray-900 transition-colors"
        >
          <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 19l-7-7 7-7"></path>
          </svg>
          {$_("admin_settings.back_to_dashboard")}
        </button>
        <button
          onclick={runCheck}
          disabled={checking || fixing}
          class="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-200 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
        >
          <svg class="w-4 h-4" class:animate-spin={checking} fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"></path>
          </svg>
          {checking ? $_("admin_settings.checking") : $_("admin_settings.recheck")}
        </button>
      </div>
      <div class="text-center mt-4">
        <h1 class="text-2xl font-bold text-gray-900 mb-2">
          {$_("admin_settings.title")}
        </h1>
        <p class="text-sm text-gray-500 max-w-3xl mx-auto">
          {$_("admin_settings.subtitle")}
        </p>
      </div>
    </div>
  </div>

  <div class="mx-auto pb-8 max-w-375 px-4">
    <div class="bg-white rounded-2xl border border-gray-100 p-6 shadow-[0_2px_8px_rgba(0,0,0,0.04)]">
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-semibold text-gray-900">
          {$_("admin_settings.critical_resources.title")}
        </h2>
        {#if !checking && !hasIssues && !fixed}
          <span class="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-emerald-50 text-emerald-700">
            <span class="w-1.5 h-1.5 rounded-full bg-emerald-500"></span>
            {$_("admin_settings.critical_resources.status_ok")}
          </span>
        {:else if !checking && hasIssues}
          <span class="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-amber-50 text-amber-700">
            <span class="w-1.5 h-1.5 rounded-full bg-amber-500"></span>
            {$_("admin_settings.critical_resources.status_issues")}
          </span>
        {/if}
      </div>
      <p class="text-sm text-gray-500 mb-6">
        {$_("admin_settings.critical_resources.description")}
      </p>

      {#if checking}
        <div class="flex items-center gap-3 text-sm text-gray-500 py-4">
          <div class="spinner spinner-sm"></div>
          {$_("admin_settings.checking_resources")}
        </div>
      {:else if hasIssues}
        <div class="bg-amber-50 border border-amber-200 rounded-xl p-4">
          <div class="flex items-start gap-3">
            <div class="shrink-0 mt-0.5">
              <svg class="w-5 h-5 text-amber-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"></path>
              </svg>
            </div>
            <div class="flex-1">
              <h3 class="text-sm font-semibold text-amber-800 mb-1">
                {$_("admin_settings.issues.heading")}
              </h3>
              <ul class="text-sm text-amber-700 space-y-1 mb-4 list-disc list-inside">
                {#if missingFolders.length > 0}
                  <li>
                    {$_("admin_settings.issues.missing_folders")}: <span class="font-medium">{missingFolders.join(", ")}</span>
                  </li>
                {/if}
                {#if missingWorkflow}
                  <li>{$_("admin_settings.issues.missing_workflow")}</li>
                {/if}
                {#if missingReportSchema}
                  <li>{$_("admin_settings.issues.missing_report_schema")}</li>
                {/if}
                {#if missingWorkflowSchema}
                  <li>{$_("admin_settings.issues.missing_workflow_schema")}</li>
                {/if}
                {#if missingCriticalResources.length > 0}
                  <li>
                    {$_("admin_settings.issues.missing_critical")}: <span class="font-medium">{missingCriticalResources.join(", ")}</span>
                  </li>
                {/if}
              </ul>
              <p class="text-xs text-amber-700 mb-4">
                {$_("admin_settings.issues.required_note")}
              </p>
              <button
                onclick={runFix}
                disabled={fixing}
                class="inline-flex items-center px-4 py-2 bg-amber-600 text-white text-sm font-medium rounded-lg hover:bg-amber-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {#if fixing}
                  <div class="spinner spinner-sm spinner-white mr-2"></div>
                  {$_("admin_settings.issues.fixing")}
                {:else}
                  <svg class="w-4 h-4 mr-2" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6"></path>
                  </svg>
                  {$_("admin_settings.issues.fix_button")}
                {/if}
              </button>
            </div>
          </div>
        </div>
      {:else if fixed}
        <div class="bg-emerald-50 border border-emerald-200 rounded-xl p-4">
          <div class="flex items-start gap-3">
            <div class="shrink-0 mt-0.5">
              <svg class="w-5 h-5 text-emerald-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z"></path>
              </svg>
            </div>
            <div>
              <h3 class="text-sm font-semibold text-emerald-800 mb-1">
                {$_("admin_settings.fixed.heading")}
              </h3>
              <p class="text-sm text-emerald-700">
                {$_("admin_settings.fixed.body")}
                {#if workflowCreated}
                  <br /><span class="font-medium">report_workflow</span> {$_("admin_settings.fixed.workflow_created")}
                {/if}
                {#if reportSchemaCreated}
                  <br /><span class="font-medium">report</span> {$_("admin_settings.fixed.report_schema_created")}
                {/if}
                {#if workflowSchemaCreated}
                  <br /><span class="font-medium">workflow</span> {$_("admin_settings.fixed.workflow_schema_created")}
                {/if}
              </p>
            </div>
          </div>
        </div>
      {:else}
        <div class="bg-emerald-50 border border-emerald-200 rounded-xl p-4">
          <div class="flex items-start gap-3">
            <div class="shrink-0 mt-0.5">
              <svg class="w-5 h-5 text-emerald-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z"></path>
              </svg>
            </div>
            <div>
              <h3 class="text-sm font-semibold text-emerald-800">
                {$_("admin_settings.healthy.heading")}
              </h3>
              <p class="text-sm text-emerald-700">
                {$_("admin_settings.healthy.body")}
              </p>
            </div>
          </div>
        </div>
      {/if}
    </div>
  </div>
</div>
