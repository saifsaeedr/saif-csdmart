<script lang="ts">
  import { goto, params } from "@roxi/routify";
  import HtmlEditor from "@/components/editors/HtmlEditor.svelte";
  import {
    attachAttachmentsToEntity,
    createEntity,
    getEntityByShortname,
    getSpaceFolders,
    getSpaces,
    getSpaceSchema,
  } from "@/lib/dmart_services";
  import {
    getTemplateFromSchemaAttachment,
    hasTemplateAttachment,
    hasMarkdownTemplateAttachment,
    getMarkdownTemplateFromSchemaAttachment,
  } from "@/lib/dmart_services/templates";
  import {
    errorToastMessage,
    successToastMessage,
  } from "@/lib/toasts_messages";
  import {
    CheckCircleSolid,
    CloseCircleOutline,
    CloseCircleSolid,
    CloudArrowUpOutline,
    FileCheckSolid,
    FileImportSolid,
    FilePdfOutline,
    FloppyDiskSolid,
    PaperClipOutline,
    PaperPlaneSolid,
    PlayOutline,
    PlusOutline,
    TagOutline,
    TrashBinSolid,
    UploadOutline,
  } from "flowbite-svelte-icons";
  import { _, locale } from "@/i18n";
  import { derived as derivedStore } from "svelte/store";
  import { onMount } from "svelte";
  import { ResourceType, DmartScope } from "@edraj/tsdmart";
  import { roles } from "@/stores/user";
  import MarkdownEditor from "@/components/editors/MarkdownEditor.svelte";
  import DynamicSchemaBasedForms from "@/components/forms/DynamicSchemaBasedForms.svelte";
  import { marked } from "marked";
  import { mangle } from "marked-mangle";
  import { gfmHeadingId } from "marked-gfm-heading-id";

  marked.use(mangle());
  marked.use(
    gfmHeadingId({
      prefix: "my-prefix-",
    }),
  );
  // Touch both Routify helpers at root level so Svelte 5 binds the
  // routify context before any async work (onMount, $effect) reads them.
  // Without this Routify logs "Unable to access context" on navigation.
  $goto;
  $params;

  let isLoading = $state(false);
  let content = "";
  let resource_type = $state(ResourceType.content);
  let itemResourceType: any;
  let isAdmin = $state(false);
  let selectedEditorType = $state("html");
  let contentType = $state("json");

  let entryType = $state("content");
  let availableSchemas = $state<any[]>([]);
  let allowedSchemaShortnames = $state<string[]>([]);
  let selectedSchema: any = $state(null);
  let schemaBasedTemplate: any = $state(null); // Template extracted from schema attachment
  let loadingSchemas = $state(false);
  let jsonFormData: Record<string, any> = $state({});
  let templateFormData: Record<string, any> = $state({});
  let pollFormData: Record<string, any> = $state({});
  let pollSchema: any = $state(null);
  let loadingPollSchema = $state(false);
  let bodyContent: any;
  let isEmpty = false;
  let validationResult: { isValid: boolean; missingFields: any[] } = { isValid: true, missingFields: [] };

  let title = $state("");
  let shortname = $state("");
  let slug = $state("");
  let shortnameError = $state("");
  const shortnamePattern = "^[a-zA-Z\\u0621-\\u064a0-9\\u0660-\\u0669\\u064b-\\u065f_]{1,64}$";

  function validateShortnameInput(value: string): boolean {
    if (!value || value === "auto") return true;
    const pattern = new RegExp(shortnamePattern);
    return pattern.test(value);
  }

  function handleShortnameInput(event: Event) {
    const value = (event.target as HTMLInputElement).value;
    if (!validateShortnameInput(value)) {
      shortnameError = $_("validation.shortname_invalid");
    } else {
      shortnameError = "";
    }
  }

  let selectedSpace = $state("");
  let spaces = $state<any[]>([]);
  let subpathHierarchy = $state<any[]>([]);
  let currentPath = $state("");
  let loadingSpaces = $state(false);
  let loadingSubpaths = $state(false);

  const canCreateEntry = $derived(
    shortname.trim().length > 0 && !shortnameError
  );

  const filteredSchemas = $derived.by(() => {
    if (!allowedSchemaShortnames.length) return availableSchemas;
    const allowed = new Set(allowedSchemaShortnames);
    return availableSchemas.filter((s: any) => allowed.has(s.shortname));
  });

  $effect(() => {
    if (
      selectedSchema &&
      !filteredSchemas.some((s: any) => s.shortname === selectedSchema.shortname)
    ) {
      selectedSchema = null;
      schema_shortname = "";
      jsonFormData = {};
      schemaBasedTemplate = null;
      templateFormData = {};
    }
  });

  let workflow_shortname = "";
  let schema_shortname = "";
  let markdownEditorRef: any = $state(null);
  let htmlEditorRef: any = $state(null);
  let markdownContent = $state("");
  let schemas: any;
  let entity: any;

  const isRTL = derivedStore(
    locale,
    ($locale: any) => $locale === "ar" || $locale === "ku",
  );

  let rolesValue: any;
  roles.subscribe((value: any) => {
    rolesValue = value;
    isAdmin = value.includes("super_admin");
  });

  async function handleEntryTypeChange() {
    if (entryType === "structured" && selectedSpace) {
      await loadSchemasForSpace();
    }
    if (entryType === "poll") {
      selectedSpace = "poll";
      currentPath = "polls";
      resource_type = ResourceType.content;
      workflow_shortname = "";
      schema_shortname = "poll";
      await loadPollSchema();
    }
  }

  async function loadPollSchema() {
    if (pollSchema) return;

    loadingPollSchema = true;
    try {
      const response = await getSpaceSchema("management", DmartScope.managed);
      if (response?.status === "success" && response?.records) {
        const pollSchemaRecord = response.records.find(
          (record: any) => record.shortname === "poll",
        );

        if (pollSchemaRecord) {
          pollSchema = {
            shortname: "poll",
            title: pollSchemaRecord.attributes?.displayname?.en || "Poll",
            schema: pollSchemaRecord.attributes?.payload?.body,
            description: pollSchemaRecord.attributes?.description?.en || "",
          };
          pollFormData = {};
        } else {
          errorToastMessage("Poll schema not found in management space");
        }
      } else {
        errorToastMessage("Failed to load poll schema");
      }
    } catch (error) {
      errorToastMessage("Failed to load poll schema");
      console.error("Error loading poll schema:", error);
    } finally {
      loadingPollSchema = false;
    }
  }

  async function loadSchemasForSpace() {
    if (!selectedSpace) return;

    loadingSchemas = true;
    try {
      const response = await getSpaceSchema(selectedSpace, DmartScope.managed);
      if (response?.status === "success" && response?.records) {
        availableSchemas = response.records.map((record: any) => ({
          shortname: record.shortname,
          title: record.attributes?.displayname?.en || record.shortname,
          schema: record.attributes?.payload?.body,
          description: record.attributes?.description?.en || "",
          raw: record, // Keep raw record to access attachments
        }));
      } else {
        availableSchemas = [];
      }
    } catch (error) {
      errorToastMessage("Failed to load schemas");
      console.error("Error loading schemas:", error);
      availableSchemas = [];
    } finally {
      loadingSchemas = false;
    }
  }

  async function handleSpaceChange(event: any) {
    selectedSpace = event.target.value;
    if (selectedSpace) {
      await initializeSubpathHierarchy(selectedSpace);
      if (entryType === "structured") {
        await loadSchemasForSpace();
      }
    }
  }

  function handleSchemaChange(event: any) {
    const schemaShortname = event.target.value;
    schema_shortname = schemaShortname; // Assign to module-level variable
    const schemaRecord = availableSchemas.find(
      (s: any) => s.shortname === schemaShortname,
    );
    selectedSchema = schemaRecord;
    jsonFormData = {};
    
    // Check if schema has a template attachment
    // For structured entries, specifically check for markdown template attachments
    if (schemaRecord && schemaRecord.raw) {
      if (entryType === "structured") {
        // For structured entries, only use markdown template attachments
        const markdownTemplate = getMarkdownTemplateFromSchemaAttachment(schemaRecord.raw);
        if (markdownTemplate) {
          schemaBasedTemplate = markdownTemplate;
          templateFormData = {};
        } else {
          schemaBasedTemplate = null;
        }
      } else {
        // For json entries, use any template attachment
        const templateFromAttachment = getTemplateFromSchemaAttachment(schemaRecord.raw);
        if (templateFromAttachment) {
          schemaBasedTemplate = templateFromAttachment;
          templateFormData = {};
        } else {
          schemaBasedTemplate = null;
        }
      }
    } else {
      schemaBasedTemplate = null;
    }
  }

  onMount(async () => {
    await loadSpaces();
  });

  async function loadSpaces() {
    loadingSpaces = true;
    try {
      const response = await getSpaces(false, DmartScope.managed, ["management"]);

      spaces = (response?.records ?? []).map((space: any) => ({
        value: space?.shortname,
        name: space?.attributes?.displayname?.en || space?.shortname,
      }));

      if (selectedSpace) {
        await initializeSubpathHierarchy(selectedSpace);
      }
    } catch (error) {
      errorToastMessage($_("create_entry.error.load_spaces_failed"));
      console.error("Error loading spaces:", error);
    } finally {
      loadingSpaces = false;
    }
  }

  async function initializeSubpathHierarchy(spaceName: any) {
    subpathHierarchy = [];
    currentPath = "";
    await loadSubpathLevel(spaceName, "", 0);
  }

  // Split a navigated parentPath ("", "/", "/folder_a", "/folder_a/folder_b")
  // into the (subpath, shortname) addressing the folder ITSELF — used to fetch
  // the parent folder so its payload.body drives the schema/workflow/resource
  // restrictions for entries created under it. Returns null at root.
  function splitParentPath(
    parentPath: string,
  ): { subpath: string; shortname: string } | null {
    const trimmed = (parentPath || "").replace(/^\/+/, "").replace(/\/+$/, "");
    if (!trimmed) return null;
    const lastSlash = trimmed.lastIndexOf("/");
    return lastSlash < 0
      ? { subpath: "/", shortname: trimmed }
      : {
          subpath: trimmed.slice(0, lastSlash),
          shortname: trimmed.slice(lastSlash + 1),
        };
  }

  async function loadSubpathLevel(spaceName: any, parentPath: any, level: any) {
    if (!spaceName) return;

    loadingSubpaths = true;
    try {
      // Fetch the parent folder ITSELF in parallel with its children. The
      // children query (getSpaceFolders) feeds the next-level dropdown; the
      // parent retrieve drives content_schema_shortnames /
      // content_resource_types / workflow_shortnames / schema_shortname for
      // creation under this path. Reading those from records[0] (a child)
      // was the previous bug — they belong to parentPath itself.
      const parts = splitParentPath(parentPath);
      const [response, parentEntry] = await Promise.all([
        getSpaceFolders(spaceName, parentPath || "/", DmartScope.managed),
        parts
          ? getEntityByShortname(
              parts.shortname,
              spaceName,
              parts.subpath,
              ResourceType.folder,
              DmartScope.managed,
              true,
              false,
            )
          : Promise.resolve(null),
      ]);

      // The server returns records: null when the queried subpath has no
      // children — coerce to [] so the .filter/.some calls below don't blow
      // up on a freshly created (or genuinely empty) folder.
      const records: any[] = response?.records ?? [];
      const folders = records.filter(
        (item: any) => item.resource_type === "folder",
      );
      const hasNonFolderContent = records.some(
        (item: any) => item.resource_type !== "folder",
      );
      const parentBody: any = (parentEntry as any)?.payload?.body ?? null;
      itemResourceType = parentBody?.content_resource_types?.[0];

      const folderContentSchemaShortnames: string[] = Array.isArray(
        parentBody?.content_schema_shortnames,
      )
        ? parentBody.content_schema_shortnames.filter(
            (s: any) => typeof s === "string" && s.trim().length > 0,
          )
        : [];

      const levelData = {
        level,
        path: parentPath,
        folders: folders.map((folder: any) => ({
          value: folder.shortname,
          name: folder.attributes?.displayname?.en || folder.shortname,
          fullPath: parentPath
            ? `${parentPath}/${folder.shortname}`
            : folder.shortname,
        })),
        resource_type: itemResourceType,
        workflow_shortname: parentBody?.workflow_shortnames?.[0] || "",
        schema_shortname:
          (parentEntry as any)?.payload?.schema_shortname ||
          folderContentSchemaShortnames[0] ||
          "",
        content_schema_shortnames: folderContentSchemaShortnames,
        canCreateEntry:
          level > 0 || hasNonFolderContent || folders.length === 0,
        selectedFolder: "",
      };

      subpathHierarchy = [...subpathHierarchy.slice(0, level), levelData];

      updateCanCreateEntry();
    } catch (error) {
      errorToastMessage($_("create_entry.error.load_subpaths_failed"));
      console.error("Error loading subpaths:", error);
    } finally {
      loadingSubpaths = false;
    }
  }

  function updateCanCreateEntry() {
    const lastLevel = subpathHierarchy[subpathHierarchy.length - 1];

    resource_type = lastLevel.resource_type;
    workflow_shortname = subpathHierarchy[0].workflow_shortname;
    schema_shortname = lastLevel.schema_shortname;
    allowedSchemaShortnames = lastLevel.content_schema_shortnames || [];
    currentPath = lastLevel.path;
  }

  async function handleSubpathChange(level: any, folderValue: any) {
    const levelData = subpathHierarchy[level];
    if (!levelData) return;

    levelData.selectedFolder = folderValue;

    if (folderValue) {
      const selectedFolder = levelData.folders.find(
        (f: any) => f.value === folderValue,
      );
      if (selectedFolder) {
        const newPath = selectedFolder.fullPath;
        await loadSubpathLevel(selectedSpace, `/${newPath}`, level + 1);
      }
    } else {
      subpathHierarchy = subpathHierarchy.slice(0, level + 1);
      updateCanCreateEntry();
    }
  }

  let tags = $state<any[]>([]);
  let newTag = $state("");

  function addTag() {
    if (newTag.trim() !== "") {
      tags = [...tags, newTag.trim()];
      newTag = "";
    }
  }

  function removeTag(index: any) {
    tags = tags.filter((_: any, i: any) => i !== index);
  }

  type AttachmentTranslation = { en: string; ar: string; ku: string };

  type AttachmentStatus = "pending" | "uploading" | "success" | "error";

  type AttachmentEntry = {
    file: File;
    shortname: string;
    displayname: AttachmentTranslation;
    description: AttachmentTranslation;
    status: AttachmentStatus;
  };

  function emptyAttachmentTranslation(): AttachmentTranslation {
    return { en: "", ar: "", ku: "" };
  }

  function toAttachmentTranslationPayload(
    t: AttachmentTranslation,
  ): Record<string, string> {
    const out: Record<string, string> = {};
    if (t.en?.trim()) out.en = t.en.trim();
    if (t.ar?.trim()) out.ar = t.ar.trim();
    if (t.ku?.trim()) out.ku = t.ku.trim();
    return out;
  }

  let attachments = $state<AttachmentEntry[]>([]);

  const uploadingCount = $derived(
    attachments.filter((a) => a.status === "uploading").length,
  );
  const uploadedCount = $derived(
    attachments.filter((a) => a.status === "success").length,
  );
  const isUploadingAttachments = $derived(
    attachments.some((a) => a.status === "uploading"),
  );
  const showUploadBanner = $derived(
    attachments.length > 0 &&
      attachments.some((a) => a.status !== "pending"),
  );

  function handleFileChange(event: any) {
    const input = event.target;
    if (input.files) {
      const newEntries: AttachmentEntry[] = Array.from(input.files as FileList).map(
        (file) => ({
          file,
          shortname: "",
          displayname: emptyAttachmentTranslation(),
          description: emptyAttachmentTranslation(),
          status: "pending",
        }),
      );
      attachments = [...attachments, ...newEntries];
    }
  }

  function removeAttachment(index: any) {
    attachments = attachments.filter((_: any, i: any) => i !== index);
  }

  function getPreviewUrl(file: any) {
    if (
      file.type.startsWith("image/") ||
      file.type.startsWith("video/") ||
      file.type === "application/pdf"
    ) {
      return URL.createObjectURL(file);
    }
    return null;
  }

  function isContentEmpty(content: any, type: any = "html") {
    if (content === null || content === undefined) {
      return true;
    }

    if (typeof content === "string") {
      if (type === "html") {
        const trimmedRaw = content.trim();
        if (!trimmedRaw) return true;
        // typewriter-editor's empty doc often looks like `<p><br></p>`,
        // `<p></p>`, or nested whitespace-only paragraphs. Treat those as empty.
        const trivialWrapper =
          /^(<p>(\s|&nbsp;|<br\s*\/?>)*<\/p>\s*)+$/i;
        if (trivialWrapper.test(trimmedRaw)) return true;
        // Media embeds and block elements with no inline text still count as
        // real content — an image-only post is valid.
        if (
          /<(img|video|audio|iframe|table|figure|svg|source|embed|object|picture|canvas)\b/i.test(
            trimmedRaw,
          )
        ) {
          return false;
        }
        const textContent = trimmedRaw
          .replace(/<[^>]*>/g, "")
          .replace(/&nbsp;/g, " ")
          .replace(/\s+/g, " ")
          .trim();
        return textContent === "";
      } else {
        return content.trim() === "";
      }
    }

    if (typeof content === "object") {
      if (Array.isArray(content)) {
        return content.length === 0;
      }
      const keys = Object.keys(content);
      if (keys.length === 0) return true;

      return keys.every((key: any) => {
        const value = content[key];
        if (value === null || value === undefined || value === "") return true;
        if (typeof value === "string") return value.trim() === "";
        if (Array.isArray(value)) return value.length === 0;
        if (typeof value === "object") return Object.keys(value).length === 0;
        return false;
      });
    }

    return false;
  }

  function isJsonFormDataEmpty(formData: any) {
    if (!formData || Object.keys(formData).length === 0) {
      return true;
    }

    return Object.values(formData).every((value: any) => {
      if (value === null || value === undefined || value === "") return true;
      if (typeof value === "string") return value.trim() === "";
      if (Array.isArray(value)) return value.length === 0;
      if (typeof value === "object") return Object.keys(value).length === 0;
      return false;
    });
  }

  function validateRequiredFields(formData: any, schema: any) {
    if (!schema || !schema.required || !Array.isArray(schema.required)) {
      return { isValid: true, missingFields: [] };
    }

    const requiredFields = schema.required.filter(
      (field: any) => field && field.trim() !== "",
    );

    if (requiredFields.length === 0) {
      return { isValid: true, missingFields: [] };
    }

    const missingFields: any[] = [];

    for (const fieldName of requiredFields) {
      const value = formData[fieldName];

      if (value === null || value === undefined || value === "") {
        missingFields.push(fieldName);
      } else if (typeof value === "string" && value.trim() === "") {
        missingFields.push(fieldName);
      } else if (Array.isArray(value) && value.length === 0) {
        missingFields.push(fieldName);
      } else if (typeof value === "object" && Object.keys(value).length === 0) {
        missingFields.push(fieldName);
      }
    }

    return {
      isValid: missingFields.length === 0,
      missingFields,
    };
  }

  async function handlePublish(isPublish: any) {
    // Validate shortname before proceeding
    if (shortname && shortname !== "auto" && !validateShortnameInput(shortname)) {
      errorToastMessage($_("validation.shortname_invalid"));
      return;
    }

    if (entryType === "poll") {
      if (!pollSchema || !pollSchema.schema) {
        errorToastMessage("Poll schema not loaded");
        return;
      }

      const validationResult = validateRequiredFields(
        pollFormData,
        pollSchema.schema,
      );

      if (!validationResult.isValid) {
        const fieldNames = validationResult.missingFields
          .map((field: any) => pollSchema.schema.properties[field]?.title || field)
          .join(", ");

        errorToastMessage(
          $_("create_entry.error.required_fields_missing", {
            values: { fields: fieldNames },
          }),
        );
        return;
      }

      if (isJsonFormDataEmpty(pollFormData)) {
        const hasRequiredFields = pollSchema?.schema?.required?.some(
          (field: any) => field && field.trim() !== "",
        );
        if (hasRequiredFields) {
          errorToastMessage($_("create_entry.error.content_required"));
          return;
        }
        bodyContent = {};
      } else {
        bodyContent = pollFormData;
      }

      isLoading = true;
      entity = {
        displayname: title,
        body: bodyContent,
        tags: tags,
        is_active: isPublish,
        ...(isAdmin && shortname ? { shortname } : {}),
      };

      const attributes: any = {
        displayname: { en: entity.displayname || "" },
        description: { en: "", ar: "", ku: "" },
        is_active: entity.is_active !== false,
        tags: entity.tags || [],
        relationships: [],
        ...(slug.trim() ? { slug: slug.trim() } : {}),
        payload: {
          content_type: "json",
          body: entity.body,
        },
      };

      const response = await createEntity(
        "poll",
        "polls",
        ResourceType.content,
        attributes,
        entity.shortname || "auto",
      );

      const msg = isPublish
        ? $_("create_entry.success.published")
        : $_("create_entry.success.saved");

      if (response) {
        successToastMessage(msg);
        setTimeout(() => {
          goBack();
        }, 500);
      } else {
        errorToastMessage(
          isPublish
            ? $_("create_entry.error.publish_failed")
            : $_("create_entry.error.save_failed"),
        );
        isLoading = false;
      }
      return;
    }

    if (!selectedSpace) {
      errorToastMessage($_("create_entry.error.select_space"));
      return;
    }

    if (!shortname.trim()) {
      errorToastMessage($_("create_entry.error.shortname_required"));
      return;
    }

    if (entryType === "structured") {
      if (selectedSchema && selectedSchema.schema) {
        validationResult = validateRequiredFields(
          jsonFormData,
          selectedSchema.schema,
        );

        if (!validationResult.isValid) {
          const fieldNames = validationResult.missingFields
            .map(
              (field: any) =>
                selectedSchema.schema.properties[field]?.title || field,
            )
            .join(", ");

          errorToastMessage(
            $_("create_entry.error.required_fields_missing", {
              values: { fields: fieldNames },
            }),
          );
          return;
        }
      }

      // Check if schema has a template attachment - validate template fields
      if (schemaBasedTemplate && schemaBasedTemplate.schema) {
        const fields = parseTemplateFields(schemaBasedTemplate.schema);
        const requiredFields = fields.filter((f: any) => f.required);
        const missingFields = requiredFields.filter((f: any) => {
          const value = templateFormData[f.name];
          if (value == null) return true;
          if (typeof value === "string") return !value.trim();
          if (Array.isArray(value)) return value.length === 0;
          if (typeof value === "object") return Object.keys(value).length === 0;
          return false;
        });

        if (missingFields.length > 0) {
          errorToastMessage(
            $_("create_entry.error.required_fields_missing", {
              values: { fields: missingFields.map((f) => f.label).join(", ") },
            }),
          );
          return;
        }
      }

      // Required-field validation above (validateRequiredFields) has already
      // guaranteed required inputs are filled. Trust the form data from here —
      // an extra whole-object "is empty" heuristic produced false-negatives on
      // valid submissions.
      bodyContent =
        jsonFormData && Object.keys(jsonFormData).length > 0
          ? jsonFormData
          : {};

      // If schema has a template attachment, wrap body content with template data
      if (schemaBasedTemplate && schemaBasedTemplate.schema && !isEmpty) {
        bodyContent = {
          schema_data: jsonFormData,
          template: {
            name: schemaBasedTemplate.shortname,
            data: { ...templateFormData },
          },
        };
      }
    } else {
      // Pull the very latest HTML from the editor so a final keystroke that
      // hasn't yet propagated through the `change` event isn't missed.
      if (selectedEditorType === "html" && htmlEditorRef?.flush) {
        htmlEditor = htmlEditorRef.flush();
      }
      const content = getContent();
      if (isContentEmpty(content, selectedEditorType)) {
        isEmpty = true;
      }
      bodyContent = isEmpty ? undefined : content;
      contentType = selectedEditorType;
    }

    if (isEmpty) {
      errorToastMessage($_("create_entry.error.content_required"));
      return;
    }

    isLoading = true;

    // Resolve the subpath once — $params.subpath may be undefined when the
    // page is accessed via /entries/create (no [subpath] route segment).
    // Falls back to currentPath which was set via loadPrefilledData().
    const resolvedSubpath = (($params.subpath ?? currentPath) || "/").replace(
      /^\//,
      "",
    );

    entity = {
      displayname: title,
      body: bodyContent,
      tags: tags,
      is_active: isPublish,
      ...(shortname ? { shortname } : {}),
    };

    const attributes: any = {
      displayname: { en: entity.displayname || "" },
      description: { en: "", ar: "", ku: "" },
      is_active: entity.is_active !== false,
      tags: entity.tags || [],
      relationships: [],
      ...(slug.trim() ? { slug: slug.trim() } : {}),
      payload: {
        content_type: contentType || "json",
        body: entity.body,
      },
    };
    if (workflow_shortname) attributes.workflow_shortname = workflow_shortname;
    if (schema_shortname)
      attributes.payload.schema_shortname = schema_shortname;

    const response = await createEntity(
      selectedSpace,
      resolvedSubpath,
      resource_type || ResourceType.content,
      attributes,
      entity.shortname || "auto",
    );

    const msg = isPublish
      ? $_("create_entry.success.published")
      : $_("create_entry.success.saved");

    if (response) {
      successToastMessage(msg);
      for (let i = 0; i < attachments.length; i++) {
        const attachment = attachments[i];
        attachments[i] = { ...attachment, status: "uploading" };
        try {
          const r = await attachAttachmentsToEntity(
            response,
            selectedSpace,
            resolvedSubpath,
            attachment.file,
            {
              shortname: attachment.shortname,
              displayname: toAttachmentTranslationPayload(attachment.displayname),
              description: toAttachmentTranslationPayload(attachment.description),
            },
          );
          if (r === false) {
            attachments[i] = { ...attachments[i], status: "error" };
            errorToastMessage(
              $_("create_entry.error.attachment_failed", {
                values: { name: attachment.file.name },
              }),
            );
          } else {
            attachments[i] = { ...attachments[i], status: "success" };
          }
        } catch (err) {
          attachments[i] = { ...attachments[i], status: "error" };
          errorToastMessage(
            $_("create_entry.error.attachment_failed", {
              values: { name: attachment.file.name },
            }),
          );
        }
      }
      setTimeout(() => {
        goBack();
      }, 500);
    } else {
      errorToastMessage(
        isPublish
          ? $_("create_entry.error.publish_failed")
          : $_("create_entry.error.save_failed"),
      );
      isLoading = false;
    }
  }

  let htmlEditor = $state("");

  function getContent() {
    if (selectedEditorType === "html") {
      return htmlEditor;
    } else {
      return markdownContent;
    }
  }

  function generateContentFromSchemaTemplate() {
    if (!schemaBasedTemplate || !schemaBasedTemplate.schema) {
      return "";
    }

    let content = schemaBasedTemplate.schema;

    // Replace placeholders with values or empty string
    Object.keys(templateFormData).forEach((key) => {
      const placeholderPattern = new RegExp(
        `\\{\\{${key}(?::[^}]+)?\\}\\}`,
        "g",
      );
      let value = templateFormData[key];
      
      // Handle list/array type - join non-empty items
      if (Array.isArray(value)) {
        value = value.filter((item: any) => item && item.trim()).join(", ");
      }
      
      content = content.replace(placeholderPattern, value || "");
    });

    // Remove any remaining placeholders that don't have values
    content = content.replace(/\{\{[^}]+\}\}/g, "");

    return content;
  }

  function parseTemplateFields(templateContent: any) {
    if (!templateContent) return [];

    const placeholderRegex = /\{\{([^:}]+)(?::([^}]+))?\}\}/g;
    const fields = [];
    const seen = new Set();
    let match;

    while ((match = placeholderRegex.exec(templateContent)) !== null) {
      const fieldName = match[1].trim();
      const fieldType = match[2]?.trim() || "string";

      if (!seen.has(fieldName)) {
        seen.add(fieldName);

        let inputType = "text";
        let placeholder = `Enter ${fieldName.replace(/_/g, " ")}`;

        switch (fieldType.toLowerCase()) {
          case "int":
          case "integer":
            inputType = "number";
            placeholder = `Enter whole number for ${fieldName}`;
            break;
          case "float":
          case "double":
            inputType = "number";
            placeholder = `Enter decimal number for ${fieldName}`;
            break;
          case "number":
            inputType = "number";
            break;
          case "email":
            inputType = "email";
            break;
          case "url":
            inputType = "url";
            break;
          case "date":
            inputType = "date";
            break;
          case "time":
            inputType = "time";
            break;
          case "password":
            inputType = "password";
            break;
          case "bool":
          case "boolean":
            inputType = "checkbox";
            placeholder = "";
            break;
          case "list":
          case "array":
            inputType = "textarea";
            placeholder = `Enter comma-separated values for ${fieldName}`;
            break;
          case "object":
            inputType = "textarea";
            placeholder = `Enter JSON object for ${fieldName}`;
            break;
          case "list_object":
            inputType = "textarea";
            placeholder = `Enter JSON array of objects for ${fieldName}`;
            break;
          case "textarea":
          case "text":
          case "string":
          default:
            inputType =
              fieldType.toLowerCase() === "textarea" ? "textarea" : "text";
            break;
        }

        fields.push({
          name: fieldName,
          label: fieldName
            .replace(/_/g, " ")
            .replace(/\b\w/g, (l) => l.toUpperCase()),
          type: inputType,
          originalType: fieldType,
          placeholder,
          required: true,
        });
      }
    }

    return fields;
  }

  type Crumb = {
    name: string;
    route: string | null;
    params?: Record<string, string>;
  };

  let cameFromAdmin = $state(false);

  onMount(() => {
    if (typeof document !== "undefined" && document.referrer) {
      cameFromAdmin = document.referrer.includes("/dashboard/admin");
    }
  });

  const useAdminPaths = $derived(cameFromAdmin || isAdmin);

  const breadcrumbs = $derived.by<Crumb[]>(() => {
    const rootLabel = useAdminPaths
      ? $_("admin_content.breadcrumb.admin")
      : $_("post_detail.breadcrumb.catalogs");
    const rootRoute = useAdminPaths ? "/dashboard/admin" : "/catalogs";
    const spaceRoute = useAdminPaths
      ? "/dashboard/admin/[space_name]"
      : "/catalogs/[space_name]";
    const subpathRoute = useAdminPaths
      ? "/dashboard/admin/[space_name]/[subpath]"
      : "/catalogs/[space_name]/[subpath]";

    const crumbs: Crumb[] = [{ name: rootLabel, route: rootRoute }];

    if (!selectedSpace) {
      crumbs.push({ name: $_("my_entries.create_new"), route: null });
      return crumbs;
    }

    crumbs.push({
      name: selectedSpace,
      route: spaceRoute,
      params: { space_name: selectedSpace },
    });

    const rawSubpath: string = ($params.subpath ?? currentPath) || "";
    const parts: string[] = rawSubpath
      .replace(/^\//, "")
      .replace(/-/g, "/")
      .split("/")
      .filter((p: string) => p.length > 0);

    let currentUrlPath = "";
    parts.forEach((part: string, index: number) => {
      currentUrlPath += (index === 0 ? "" : "-") + part;
      crumbs.push({
        name: part,
        route: subpathRoute,
        params: { space_name: selectedSpace, subpath: currentUrlPath },
      });
    });

    crumbs.push({ name: $_("my_entries.create_new"), route: null });
    return crumbs;
  });

  const parentCrumb = $derived(
    breadcrumbs.length >= 2 ? breadcrumbs[breadcrumbs.length - 2] : null,
  );

  function navigateToBreadcrumb(crumb: Crumb) {
    if (!crumb.route) return;
    if (crumb.params) {
      $goto(crumb.route, crumb.params);
    } else {
      $goto(crumb.route);
    }
  }

  function goBack() {
    if (parentCrumb) {
      navigateToBreadcrumb(parentCrumb);
    } else {
      $goto("/entries");
    }
  }

  async function loadPrefilledData() {
    const prefilledSpace = $params.space_name || $params.spaceName;
    const prefilledSubpath = $params.subpath;

    if (prefilledSpace) {
      selectedSpace = prefilledSpace;
      // Normalise: strip leading slash if present (e.g. "/schema" → "schema")
      const normalizedSubpath = prefilledSubpath
        ? prefilledSubpath.replace(/^\//, "")
        : "";
      currentPath = normalizedSubpath;

      try {
        await loadSubpathLevel(
          prefilledSpace,
          normalizedSubpath ? `/${normalizedSubpath}` : "/",
          0,
        );
        // updateCanCreateEntry sets metadata (resource_type, schema, workflow)
        // but also overwrites selectedSubpath — restore the correct value after
        updateCanCreateEntry();
        currentPath = normalizedSubpath;
      } catch (e) {
        currentPath = normalizedSubpath;
      }
    }
  }

  onMount(async () => {
    await loadSpaces();
    await loadPrefilledData();
  });
</script>

<div class="page-container" class:rtl={$isRTL}>
  <div class="content-wrapper">
    <div class="create-header">
      <div class="create-header-inner">
        <button
          onclick={goBack}
          class="w-10 h-10 bg-indigo-50 hover:bg-indigo-100 text-indigo-600 rounded-xl flex items-center justify-center transition-colors shadow-sm"
          aria-label={$_("entry_detail.navigation.back_to_folder") || "Go back"}
          type="button"
        >
          <svg
            class="w-5 h-5"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M15 19l-7-7 7-7"
            />
          </svg>
        </button>
        <div>
          <h1 class="create-page-title">
            {$_("my_entries.create_new")}
          </h1>
          <nav
            class="flex text-sm text-gray-500 font-medium mb-1"
            aria-label="Breadcrumb"
          >
            <ol class="inline-flex items-center space-x-2">
              {#each breadcrumbs as crumb, index}
                <li class="inline-flex items-center">
                  {#if index > 0}
                    <svg
                      class="w-4 h-4 mx-1 text-gray-400"
                      fill="none"
                      stroke="currentColor"
                      viewBox="0 0 24 24"
                    >
                      <path
                        stroke-linecap="round"
                        stroke-linejoin="round"
                        stroke-width="2"
                        d="M9 5l7 7-7 7"
                      />
                    </svg>
                  {/if}
                  {#if crumb.route}
                    <button
                      onclick={() => navigateToBreadcrumb(crumb)}
                      class="create-breadcrumb-link"
                      type="button"
                    >
                      {crumb.name}
                    </button>
                  {:else}
                    <span class="create-breadcrumb-current">{crumb.name}</span>
                  {/if}
                </li>
              {/each}
            </ol>
          </nav>
        </div>
      </div>
    </div>

    {#if showUploadBanner}
      <div
        class="attachments-upload-banner"
        role="status"
        aria-live="polite"
      >
        <span class="attachments-upload-banner-spinner" aria-hidden="true"></span>
        <div class="attachments-upload-banner-text">
          <strong>
            {$_("create_entry.attachments.uploading_progress", {
              default: "Uploading {done}/{total}…",
              values: { done: uploadedCount, total: attachments.length },
            })}
          </strong>
          <div
            class="attachments-upload-banner-progress"
            aria-hidden="true"
          >
            <div
              class="attachments-upload-banner-progress-fill"
              style="width: {attachments.length
                ? Math.round((uploadedCount / attachments.length) * 100)
                : 0}%"
            ></div>
          </div>
        </div>
      </div>
    {/if}

    <div class="action-section">
      <div class="action-content">
        <div class="action-info">
          <div class="action-icon">
            <FileCheckSolid class="icon" />
          </div>
          <div class="action-text">
            <h3>{$_("create_entry.action.title")}</h3>
            <p>{$_("create_entry.action.description")}</p>
          </div>
        </div>
        <div class="action-buttons">
          <button
            aria-label={$_("create_entry.buttons.save_draft")}
            class="draft-button"
            onclick={(event) => {
              event.preventDefault();
              handlePublish(false);
            }}
            disabled={isLoading || !canCreateEntry}
          >
            <FloppyDiskSolid class="icon button-icon" />
            <span
              >{isLoading
                ? $_("create_entry.buttons.saving")
                : $_("create_entry.buttons.save_draft")}</span
            >
          </button>
          <button
            aria-label={$_("create_entry.buttons.publish_now")}
            class="publish-button"
            onclick={(event) => {
              event.preventDefault();
              handlePublish(true);
            }}
            disabled={isLoading || !canCreateEntry}
          >
            <PaperPlaneSolid class="icon button-icon" />
            <span
              >{isLoading
                ? $_("create_entry.buttons.publishing")
                : $_("create_entry.buttons.publish_now")}</span
            >
          </button>
        </div>
      </div>
    </div>

    <div class="section">
      <div class="section-header">
        <FileCheckSolid class="section-icon" />
        <h2>Entry Details</h2>
      </div>
      <div class="section-content details-content">
        <div class="form-row">
          <span class="form-row-label">Type</span>
          <div class="entry-type-selector compact">
            <label class="entry-type-option">
              <input
                type="radio"
                bind:group={entryType}
                value="content"
                onchange={handleEntryTypeChange}
              />
              <span class="entry-type-label">
                <strong>{$_("create_entry.entry_type.content_title")}</strong>
                <small
                  >{$_("create_entry.entry_type.content_description")}</small
                >
              </span>
            </label>
            <label class="entry-type-option">
              <input
                type="radio"
                bind:group={entryType}
                value="structured"
                onchange={handleEntryTypeChange}
              />
              <span class="entry-type-label">
                <strong>Structured Entry</strong>
                <small>Form data merged with markdown template preview</small>
              </span>
            </label>
          </div>
        </div>

        <div class="form-row">
          <label for="title-input" class="form-row-label">
            {$_("create_entry.title.section_title")}
          </label>
          <input
            type="text"
            id="title-input"
            bind:value={title}
            class="form-row-input"
            placeholder={$_("create_entry.title.placeholder")}
            aria-label={$_("create_entry.title.placeholder")}
          />
        </div>

        <div class="form-row">
          <label for="shortname-input" class="form-row-label">
            {$_("create_entry.shortname.section_title")}
            <span class="required-indicator">*</span>
          </label>
          <div class="form-row-control">
            <div
              class="shortname-input-group"
              class:input-error={shortnameError}
            >
              <input
                type="text"
                id="shortname-input"
                bind:value={shortname}
                class="shortname-input-field"
                class:input-error={shortnameError}
                pattern={shortnamePattern}
                placeholder={$_("create_entry.shortname.placeholder")}
                aria-label={$_("create_entry.shortname.placeholder")}
                oninput={handleShortnameInput}
              />
              <button
                type="button"
                class="shortname-auto-btn"
                onclick={() => (shortname = "auto")}
                title="Use auto-generated shortname"
              >
                Auto
              </button>
            </div>
            {#if shortnameError}
              <div class="shortname-error">
                <svg class="error-icon" fill="currentColor" viewBox="0 0 20 20">
                  <path fill-rule="evenodd" d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7 4a1 1 0 11-2 0 1 1 0 012 0zm-1-9a1 1 0 00-1 1v4a1 1 0 102 0V6a1 1 0 00-1-1z" clip-rule="evenodd"></path>
                </svg>
                {shortnameError}
              </div>
            {:else}
              <div class="shortname-help">
                <small>{$_("create_entry.shortname.help_text")}</small>
              </div>
            {/if}
          </div>
        </div>

        <div class="form-row">
          <label for="slug-input" class="form-row-label">
            {$_("fields.slug")}
          </label>
          <input
            type="text"
            id="slug-input"
            bind:value={slug}
            class="form-row-input"
            placeholder={$_("placeholders.slug")}
            aria-label={$_("fields.slug")}
          />
        </div>

        <div class="form-block">
          <span class="form-row-label">{$_("create_entry.tags.section_title")}</span>
          <div class="tag-input-container">
            <input
              type="text"
              id="tag-input"
              bind:value={newTag}
              placeholder={$_("create_entry.tags.placeholder")}
              class="tag-input"
              onkeydown={(e) => {
                if (e.key === "Enter") addTag();
              }}
            />
            <button
              aria-label={$_("create_entry.tags.add_button")}
              class="add-tag-button"
              onclick={addTag}
              disabled={!newTag.trim()}
            >
              <PlusOutline class="icon button-icon" />
              <span>{$_("create_entry.tags.add_button")}</span>
            </button>
          </div>

          {#if tags.length > 0}
            <div class="tags-container">
              {#each tags as tag, index}
                <div class="tag-item">
                  <TagOutline class="tag-icon" />
                  <span class="tag-text">{tag}</span>
                  <button
                    class="tag-remove"
                    onclick={() => removeTag(index)}
                    aria-label={$_("create_entry.tags.remove_aria")}
                  >
                    <CloseCircleOutline class="icon" />
                  </button>
                </div>
              {/each}
            </div>
          {/if}
        </div>
      </div>
    </div>

    {#if entryType === "content"}
      <div class="section">
        <div class="section-header">
          <FileCheckSolid class="section-icon" />
          <h2>{$_("create_entry.content.section_title")}</h2>
          <div class="editor-selector">
            <div class="editor-selector-label">
              {$_("create_entry.content.editor_type")}
            </div>
            <div class="editor-toggle">
              <button
                class="editor-toggle-btn"
                class:active={selectedEditorType === "html"}
                onclick={() => (selectedEditorType = "html")}
              >
                <span class="editor-icon">🎨</span>
                <span>{$_("create_entry.content.html_editor")}</span>
              </button>
              <button
                class="editor-toggle-btn"
                class:active={selectedEditorType === "markdown"}
                onclick={() => (selectedEditorType = "markdown")}
              >
                <span class="editor-icon">📝</span>
                <span>{$_("create_entry.content.markdown_editor")}</span>
              </button>
            </div>
          </div>
        </div>
        <div class="section-content">
          <div class="editor-container">
            {#if selectedEditorType === "html"}
              <HtmlEditor
                bind:this={htmlEditorRef}
                bind:content={htmlEditor}
                uid="main-editor"
                {attachments}
                {resource_type}
                subpath={$params.subpath}
                space_name={selectedSpace}
                parent_shortname={shortname}
              />
            {:else}
              <MarkdownEditor
                bind:content={markdownContent}
                bind:this={markdownEditorRef}
              />
            {/if}
          </div>
        </div>
      </div>
    {:else if entryType === "json"}
      <!-- Schema Selection Section -->
      <div class="section">
        <div class="section-header">
          <FileCheckSolid class="section-icon" />
          <h2>{$_("create_entry.schema.selection_title")}</h2>
        </div>
        <div class="section-content">
          {#if loadingSchemas}
            <div class="loading-state">
              <p>{$_("create_entry.schema.loading")}</p>
            </div>
          {:else if filteredSchemas.length > 0}
            <div class="schema-selector">
              <label for="schema-select" class="selector-label"
                >{$_("create_entry.schema.select_label")}</label
              >
              <select
                id="schema-select"
                onchange={handleSchemaChange}
                class="destination-select"
              >
                <option value=""
                  >{$_("create_entry.schema.choose_option")}</option
                >
                {#each filteredSchemas as schema}
                  <option value={schema.shortname}>{schema.title}</option>
                {/each}
              </select>
              {#if selectedSchema}
                <div class="schema-info">
                  <h4>{selectedSchema.title}</h4>
                  {#if selectedSchema.description}
                    <p class="schema-description">
                      {selectedSchema.description}
                    </p>
                  {/if}
                </div>
              {/if}
            </div>
          {:else}
            <div class="empty-state">
              <FileCheckSolid class="empty-icon" />
              <p>{$_("create_entry.schema.no_schemas")}</p>
            </div>
          {/if}
        </div>
      </div>

      {#if selectedSchema && selectedSchema.schema}
        <div class="section">
          <div class="section-header">
            <FileCheckSolid class="section-icon" />
            <h2>{$_("create_entry.schema.entry_data_title")}</h2>
          </div>
          <div class="section-content">
            <DynamicSchemaBasedForms
              bind:content={jsonFormData}
              schema={selectedSchema.schema}
            />
          </div>
        </div>
      {/if}

      <!-- Schema-based Template Form -->
      {#if selectedSchema && schemaBasedTemplate && schemaBasedTemplate.schema}
        <div class="section">
          <div class="section-header">
            <FileCheckSolid class="section-icon" />
            <h2>{$_("create_entry.template.schema_template_title") || "Template Data"}</h2>
          </div>
          <div class="section-content">
            <div class="template-info-box">
              <p class="template-info-text">
                {$_("create_entry.template.schema_template_description") || "This schema includes a template. Fill in the template data below."}
              </p>
            </div>
            
            <!-- Template Data Form Card -->
            <div class="template-data-card">
              <div class="template-data-card-header">
                <h3 class="template-data-title">
                  {schemaBasedTemplate.title}
                </h3>
              </div>
              <div class="template-data-card-body">
                <div class="template-form">
                  {#each parseTemplateFields(schemaBasedTemplate.schema) as field}
                    <div class="form-field">
                      <label for="schema-template-{field.name}" class="field-label">
                        {field.label}
                        {#if field.required}
                          <span class="required-indicator">*</span>
                        {/if}
                        <span class="field-type">({field.originalType})</span>
                      </label>
                      {#if field.originalType === "list"}
                        <!-- List Input with Add/Delete -->
                        <div class="list-input-container">
                          {#if !templateFormData[field.name]}
                            {templateFormData[field.name] = [''], ''}
                          {/if}
                          {#each templateFormData[field.name] as item, index (index)}
                            <div class="list-input-row">
                              <input
                                type="text"
                                bind:value={templateFormData[field.name][index]}
                                class="field-input list-input"
                                placeholder={`Item ${index + 1}`}
                              />
                              <button
                                type="button"
                                class="list-btn list-btn-remove"
                                onclick={() => {
                                  templateFormData[field.name] = templateFormData[field.name].filter((_: any, i: any) => i !== index);
                                }}
                                title="Remove item"
                              >
                                ✕
                              </button>
                            </div>
                          {/each}
                          <button
                            type="button"
                            class="list-btn list-btn-add"
                            onclick={() => {
                              templateFormData[field.name] = [...templateFormData[field.name], ''];
                            }}
                          >
                            + Add Item
                          </button>
                        </div>
                      {:else if field.type === "textarea"}
                        <textarea
                          id="schema-template-{field.name}"
                          bind:value={templateFormData[field.name]}
                          class="field-input field-textarea"
                          placeholder={field.placeholder}
                          required={field.required}
                          rows={field.originalType === "object" || field.originalType === "list_object"
                            ? 5
                            : 3}
                        ></textarea>
                        {#if field.originalType === "object"}
                          <small class="field-hint"
                            >Enter valid JSON object</small
                          >
                        {:else if field.originalType === "list_object"}
                          <small class="field-hint"
                            >Enter valid JSON array of objects</small
                          >
                        {/if}
                      {:else if field.type === "checkbox"}
                        <div class="checkbox-wrapper">
                          <input
                            id="schema-template-{field.name}"
                            type="checkbox"
                            bind:checked={templateFormData[field.name]}
                            class="field-checkbox"
                          />
                          <span class="checkbox-label">Yes</span>
                        </div>
                      {:else}
                        <input
                          id="schema-template-{field.name}"
                          type={field.type}
                          bind:value={templateFormData[field.name]}
                          class="field-input field-text"
                          placeholder={field.placeholder}
                          required={field.required}
                        />
                      {/if}
                    </div>
                  {/each}
                </div>
              </div>
            </div>

            <!-- Template Preview -->
            <div class="template-preview-section">
              <h3 class="template-preview-title">
                {$_("create_entry.template.preview_title")}
              </h3>
              <div class="template-preview markdown-preview">
                {@html marked(generateContentFromSchemaTemplate())}
              </div>
            </div>
          </div>
        </div>
      {/if}
    {:else if entryType === "structured"}
      <!-- Structured Entry: Schema Selection Section -->
      <div class="section">
        <div class="section-header">
          <FileCheckSolid class="section-icon" />
          <h2>{$_("create_entry.schema.selection_title")}</h2>
        </div>
        <div class="section-content">
          {#if loadingSchemas}
            <div class="loading-state">
              <p>{$_("create_entry.schema.loading")}</p>
            </div>
          {:else if filteredSchemas.length > 0}
            <div class="schema-selector">
              <label for="schema-select" class="selector-label"
                >{$_("create_entry.schema.select_label")}</label
              >
              <select
                id="schema-select"
                onchange={handleSchemaChange}
                class="destination-select"
              >
                <option value=""
                  >{$_("create_entry.schema.choose_option")}</option
                >
                {#each filteredSchemas as schema}
                  <option value={schema.shortname}>{schema.title}</option>
                {/each}
              </select>
              {#if selectedSchema}
                <div class="schema-info">
                  <h4>{selectedSchema.title}</h4>
                  {#if selectedSchema.description}
                    <p class="schema-description">
                      {selectedSchema.description}
                    </p>
                  {/if}
                </div>
              {/if}
            </div>
          {:else}
            <div class="empty-state">
              <FileCheckSolid class="empty-icon" />
              <p>{$_("create_entry.schema.no_schemas")}</p>
            </div>
          {/if}
        </div>
      </div>

      {#if selectedSchema && selectedSchema.schema}
        {#if schemaBasedTemplate && schemaBasedTemplate.schema}
          <!-- Structured Entry with Markdown Template: Side-by-side layout -->
          <div class="structured-entry-layout">
            <div class="structured-entry-form">
              <div class="section">
                <div class="section-header">
                  <FileCheckSolid class="section-icon" />
                  <h2>{$_("create_entry.schema.entry_data_title")}</h2>
                </div>
                <div class="section-content">
                  <DynamicSchemaBasedForms
                    bind:content={jsonFormData}
                    schema={selectedSchema.schema}
                  />
                </div>
              </div>

              <div class="section">
                <div class="section-header">
                  <FileCheckSolid class="section-icon" />
                  <h2>{$_("create_entry.template.schema_template_title") || "Template Data"}</h2>
                </div>
                <div class="section-content">
                  <div class="template-info-box">
                    <p class="template-info-text">
                      Fill in the template data below. This will be merged with the form data to create the final structured entry.
                    </p>
                  </div>
                  
                  <div class="template-data-card">
                    <div class="template-data-card-header">
                      <h3 class="template-data-title">
                        {schemaBasedTemplate.title}
                      </h3>
                    </div>
                    <div class="template-data-card-body">
                      <div class="template-form">
                        {#each parseTemplateFields(schemaBasedTemplate.schema) as field}
                          <div class="form-field">
                            <label for="structured-template-{field.name}" class="field-label">
                              {field.label}
                              {#if field.required}
                                <span class="required-indicator">*</span>
                              {/if}
                              <span class="field-type">({field.originalType})</span>
                            </label>
                            {#if field.originalType === "list"}
                              <div class="list-input-container">
                                {#if !templateFormData[field.name]}
                                  {templateFormData[field.name] = [''], ''}
                                {/if}
                                {#each templateFormData[field.name] as item, index (index)}
                                  <div class="list-input-row">
                                    <input
                                      type="text"
                                      bind:value={templateFormData[field.name][index]}
                                      class="field-input list-input"
                                      placeholder={`Item ${index + 1}`}
                                    />
                                    <button
                                      type="button"
                                      class="list-btn list-btn-remove"
                                      onclick={() => {
                                        templateFormData[field.name] = templateFormData[field.name].filter((_: any, i: any) => i !== index);
                                      }}
                                      title="Remove item"
                                    >
                                      ✕
                                    </button>
                                  </div>
                                {/each}
                                <button
                                  type="button"
                                  class="list-btn list-btn-add"
                                  onclick={() => {
                                    templateFormData[field.name] = [...templateFormData[field.name], ''];
                                  }}
                                >
                                  + Add Item
                                </button>
                              </div>
                            {:else if field.type === "textarea"}
                              <textarea
                                id="structured-template-{field.name}"
                                bind:value={templateFormData[field.name]}
                                class="field-input field-textarea"
                                placeholder={field.placeholder}
                                required={field.required}
                                rows={field.originalType === "object" || field.originalType === "list_object" ? 5 : 3}
                              ></textarea>
                              {#if field.originalType === "object"}
                                <small class="field-hint">Enter valid JSON object</small>
                              {:else if field.originalType === "list_object"}
                                <small class="field-hint">Enter valid JSON array of objects</small>
                              {/if}
                            {:else if field.type === "checkbox"}
                              <div class="checkbox-wrapper">
                                <input
                                  id="structured-template-{field.name}"
                                  type="checkbox"
                                  bind:checked={templateFormData[field.name]}
                                  class="field-checkbox"
                                />
                                <span class="checkbox-label">Yes</span>
                              </div>
                            {:else}
                              <input
                                id="structured-template-{field.name}"
                                type={field.type}
                                bind:value={templateFormData[field.name]}
                                class="field-input field-text"
                                placeholder={field.placeholder}
                                required={field.required}
                              />
                            {/if}
                          </div>
                        {/each}
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>

            <div class="structured-entry-preview">
              <div class="section preview-section">
                <div class="section-header">
                  <FileCheckSolid class="section-icon" />
                  <h2>Preview</h2>
                </div>
                <div class="section-content">
                  <div class="template-preview markdown-preview">
                    {@html marked(generateContentFromSchemaTemplate())}
                  </div>
                </div>
              </div>
            </div>
          </div>
        {:else}
          <!-- Structured Entry without Markdown Template: Form only -->
          <div class="section">
            <div class="section-header">
              <FileCheckSolid class="section-icon" />
              <h2>{$_("create_entry.schema.entry_data_title")}</h2>
            </div>
            <div class="section-content">
              <DynamicSchemaBasedForms
                bind:content={jsonFormData}
                schema={selectedSchema.schema}
              />
            </div>
          </div>
        {/if}
      {/if}
    {:else if entryType === "poll"}
      <!-- Poll Entry Data Section -->
      <div class="section">
        <div class="section-header">
          <FileCheckSolid class="section-icon" />
          <h2>{$_("create_entry.poll.entry_data_title")}</h2>
        </div>
        <div class="section-content">
          {#if loadingPollSchema}
            <div class="loading-state">
              <p>{$_("create_entry.schema.loading")}</p>
            </div>
          {:else if pollSchema && pollSchema.schema}
            <DynamicSchemaBasedForms
              bind:content={pollFormData}
              schema={pollSchema.schema}
            />
          {:else}
            <div class="empty-state">
              <FileCheckSolid class="empty-icon" />
              <p>Failed to load poll schema</p>
            </div>
          {/if}
        </div>
      </div>
    {/if}

    <!-- Only show attachments section for non-poll entries -->
    {#if entryType !== "poll"}
      <!-- Attachments Section -->
      <div class="section">
        <div class="section-header">
          <PaperClipOutline class="section-icon" />
          <h2>
            {$_("create_entry.attachments.section_title", {
              values: { count: attachments.length },
            })}
          </h2>
          <input
            type="file"
            id="fileInput"
            multiple
            onchange={handleFileChange}
            style="display: none;"
          />
          <button
            aria-label={$_("create_entry.attachments.add_files")}
            class="add-files-button"
            disabled={isLoading}
            onclick={() => document.getElementById("fileInput")?.click()}
          >
            <UploadOutline class="icon button-icon" />
            <span>{$_("create_entry.attachments.add_files")}</span>
          </button>
        </div>
        <div class="section-content">
          {#if attachments.length > 0}
            <div class="attachments-list">
              {#each attachments as attachment, index}
                <div class="attachment-row" data-status={attachment.status}>
                  <div class="attachment-preview">
                    {#if getPreviewUrl(attachment.file)}
                      {#if attachment.file.type.startsWith("image/")}
                        <img
                          src={getPreviewUrl(attachment.file) || "/placeholder.svg"}
                          alt={attachment.file.name || "no-image"}
                          class="attachment-image"
                        />
                      {:else if attachment.file.type.startsWith("video/")}
                        <video
                          src={getPreviewUrl(attachment.file)}
                          class="attachment-video"
                        >
                          <track
                            kind="captions"
                            src=""
                            srclang="en"
                            label="English"
                          />
                        </video>
                        <div class="video-overlay">
                          <PlayOutline class="play-icon" />
                        </div>
                      {:else if attachment.file.type === "application/pdf"}
                        <div class="file-preview">
                          <FilePdfOutline class="file-icon pdf" />
                        </div>
                      {/if}
                    {:else}
                      <div class="file-preview">
                        <FileImportSolid class="file-icon" />
                      </div>
                    {/if}
                    {#if attachment.status === "uploading"}
                      <div class="attachment-status-overlay uploading" aria-label="Uploading">
                        <span class="attachment-spinner" aria-hidden="true"></span>
                      </div>
                    {:else if attachment.status === "success"}
                      <div class="attachment-status-overlay success" aria-label="Uploaded">
                        <CheckCircleSolid class="status-icon" />
                      </div>
                    {:else if attachment.status === "error"}
                      <div class="attachment-status-overlay error" aria-label="Upload failed">
                        <CloseCircleSolid class="status-icon" />
                      </div>
                    {/if}
                  </div>
                  <div class="attachment-body">
                    <div class="attachment-info">
                      <p class="attachment-name">{attachment.file.name}</p>
                      <p class="attachment-size">
                        {(attachment.file.size / 1024).toFixed(1)} KB
                      </p>
                    </div>
                    <div class="attachment-metadata">
                      <label class="metadata-field">
                        <span class="metadata-label">{$_("create_entry.attachments.shortname_label")}</span>
                        <input
                          type="text"
                          class="metadata-input"
                          bind:value={attachments[index].shortname}
                          placeholder={$_("create_entry.attachments.shortname_placeholder")}
                        />
                      </label>
                      <div class="metadata-field">
                        <span class="metadata-label">{$_("create_entry.attachments.displayname_label")}</span>
                        <div class="metadata-lang-grid">
                          <input
                            type="text"
                            class="metadata-input"
                            bind:value={attachments[index].displayname.en}
                            placeholder="English"
                          />
                          <input
                            type="text"
                            class="metadata-input"
                            bind:value={attachments[index].displayname.ar}
                            placeholder="العربية"
                            dir="rtl"
                          />
                          <input
                            type="text"
                            class="metadata-input"
                            bind:value={attachments[index].displayname.ku}
                            placeholder="کوردی"
                            dir="rtl"
                          />
                        </div>
                      </div>
                      <div class="metadata-field">
                        <span class="metadata-label">{$_("create_entry.attachments.description_label")}</span>
                        <div class="metadata-lang-grid">
                          <textarea
                            class="metadata-input"
                            rows="2"
                            bind:value={attachments[index].description.en}
                            placeholder="English"
                          ></textarea>
                          <textarea
                            class="metadata-input"
                            rows="2"
                            bind:value={attachments[index].description.ar}
                            placeholder="العربية"
                            dir="rtl"
                          ></textarea>
                          <textarea
                            class="metadata-input"
                            rows="2"
                            bind:value={attachments[index].description.ku}
                            placeholder="کوردی"
                            dir="rtl"
                          ></textarea>
                        </div>
                      </div>
                    </div>
                  </div>
                  <button
                    aria-label={$_("create_entry.attachments.remove_file", {
                      values: { name: attachment.file.name },
                    })}
                    class="remove-attachment"
                    disabled={attachment.status === "uploading" ||
                      attachment.status === "success"}
                    onclick={() => removeAttachment(index)}
                  >
                    <TrashBinSolid class="icon" />
                  </button>
                </div>
              {/each}
            </div>
          {:else}
            <div class="empty-attachments">
              <CloudArrowUpOutline class="empty-icon" />
              <h3>{$_("create_entry.attachments.empty_title")}</h3>
              <p>{$_("create_entry.attachments.empty_description")}</p>
            </div>
          {/if}
        </div>
      </div>
    {/if}
  </div>
</div>

<style>
  .create-header {
    margin-bottom: 2rem;
  }

  .create-header-inner {
    display: flex;
    align-items: center;
    gap: 1rem;
  }

  .create-page-title {
    font-family: var(--font-display);
    font-weight: 700;
    font-size: clamp(1.5rem, 3vw, 1.75rem);
    line-height: 1.2;
    letter-spacing: -0.02em;
    color: var(--color-gray-900);
  }

  .create-breadcrumb-link {
    font-size: 0.875rem;
    color: var(--color-gray-400);
    cursor: pointer;
    transition: color var(--duration-fast) var(--ease-out);
    background: none;
    border: none;
    padding: 0;
  }

  .create-breadcrumb-link:hover {
    color: var(--color-primary-600);
  }

  .create-breadcrumb-current {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-gray-900);
  }

  .rtl {
    direction: rtl;
  }

  .rtl .selector-label {
    text-align: right;
  }

  .rtl .destination-select {
    text-align: right;
  }

  .rtl .tag-input {
    text-align: right;
  }

  .rtl .tag-remove {
    margin-left: 0;
    margin-right: 0.25rem;
  }

  .selector-label {
    font-weight: 600;
    color: #374151;
    font-size: 0.875rem;
  }

  :root {
    --primary-color: #2563eb;
    --primary-light: #3b82f6;
    --primary-dark: #1d4ed8;
    --secondary-color: #64748b;
    --success-color: #10b981;
    --danger-color: #ef4444;
    --warning-color: #f59e0b;
    --gray-50: #f8fafc;
    --gray-100: #f1f5f9;
    --gray-200: #e2e8f0;
    --gray-300: #cbd5e1;
    --gray-400: #94a3b8;
    --gray-500: #64748b;
    --gray-600: #475569;
    --gray-700: #334155;
    --gray-800: #1e293b;
    --gray-900: #0f172a;
    --white: #ffffff;
    --shadow-sm: 0 1px 2px 0 rgba(0, 0, 0, 0.05);
    --shadow-md: 0 4px 6px -1px rgba(0, 0, 0, 0.1),
      0 2px 4px -1px rgba(0, 0, 0, 0.06);
    --shadow-lg: 0 10px 15px -3px rgba(0, 0, 0, 0.1),
      0 4px 6px -2px rgba(0, 0, 0, 0.05);
    --shadow-xl: 0 20px 25px -5px rgba(0, 0, 0, 0.1),
      0 10px 10px -5px rgba(0, 0, 0, 0.04);
    --radius-sm: 0.375rem;
    --radius-md: 0.5rem;
    --radius-lg: 0.75rem;
    --radius-xl: 1rem;
  }

  * {
    box-sizing: border-box;
  }

  .page-container {
    min-height: 100vh;
    background: linear-gradient(
      135deg,
      var(--gray-50) 0%,
      var(--gray-100) 100%
    );
    font-family:
      "uthmantn",
      -apple-system,
      BlinkMacSystemFont,
      "Segoe UI",
      Roboto,
      "Helvetica Neue",
      Arial,
      sans-serif;
    color: var(--gray-800);
    line-height: 1.6;
  }

  .content-wrapper {
    max-width: 1200px;
    margin: 0 auto;
    padding: 2rem;
  }

  .action-section {
    background: var(--white);
    border-radius: var(--radius-xl);
    padding: 2rem;
    margin-bottom: 2rem;
    box-shadow: var(--shadow-md);
    border: 1px solid var(--gray-200);
  }

  .action-content {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 2rem;
  }

  .action-info {
    display: flex;
    align-items: center;
    gap: 1rem;
  }

  .action-icon {
    width: 3rem;
    height: 3rem;
    background: var(--primary-color);
    color: var(--white);
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }

  .action-text h3 {
    margin: 0 0 0.25rem 0;
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--gray-800);
  }

  .action-text p {
    margin: 0;
    color: var(--gray-600);
    font-size: 0.875rem;
  }

  .action-buttons {
    display: flex;
    gap: 1rem;
  }

  .draft-button,
  .publish-button {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.75rem 1.5rem;
    border-radius: var(--radius-lg);
    font-weight: 500;
    transition: all 0.2s ease;
    cursor: pointer;
    border: none;
  }

  .draft-button {
    background: var(--gray-100);
    color: var(--gray-700);
    border: 1px solid var(--gray-200);
  }

  .draft-button:hover:not(:disabled) {
    background: var(--gray-200);
    transform: translateY(-1px);
    box-shadow: var(--shadow-md);
  }

  .publish-button {
    background: var(--primary-color);
    color: var(--white);
  }

  .publish-button:hover:not(:disabled) {
    background: var(--primary-dark);
    transform: translateY(-1px);
    box-shadow: var(--shadow-lg);
  }

  .draft-button:disabled,
  .publish-button:disabled {
    opacity: 0.6;
    cursor: not-allowed;
  }

  .section {
    background: var(--white);
    border-radius: var(--radius-xl);
    margin-bottom: 2rem;
    box-shadow: var(--shadow-md);
    border: 1px solid var(--gray-200);
    overflow: hidden;
  }

  .section-header {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 1.5rem 2rem;
    background: var(--gray-50);
    border-bottom: 1px solid var(--gray-200);
  }

  .section-header h2 {
    margin: 0;
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--gray-800);
    flex: 1;
  }

  .section-content {
    padding: 2rem;
  }

  .details-content {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }

  .form-row {
    display: grid;
    grid-template-columns: 160px 1fr;
    align-items: start;
    gap: 1rem;
  }

  .form-row-label {
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--gray-700);
    margin: 0;
    padding-top: 0.625rem;
  }

  .form-row-control {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    min-width: 0;
  }

  .form-row-input {
    width: 100%;
    padding: 0.625rem 0.875rem;
    border: 1px solid var(--gray-200);
    border-radius: var(--radius-lg);
    font-size: 0.95rem;
    color: var(--gray-800);
    transition: all 0.2s ease;
    outline: none;
    background: var(--white);
  }

  .form-row-input:focus {
    border-color: var(--primary-color);
    box-shadow: 0 0 0 3px rgba(37, 99, 235, 0.1);
  }

  .form-block {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  @media (max-width: 640px) {
    .form-row {
      grid-template-columns: 1fr;
      gap: 0.375rem;
    }
  }

  .shortname-input-group {
    display: flex;
    align-items: stretch;
    border: 1px solid var(--gray-200);
    border-radius: var(--radius-lg);
    overflow: hidden;
    background: var(--white);
    transition:
      border-color 0.2s ease,
      box-shadow 0.2s ease;
  }

  .shortname-input-group:focus-within {
    border-color: var(--primary-color);
    box-shadow: 0 0 0 3px rgba(37, 99, 235, 0.1);
  }

  .shortname-input-field {
    flex: 1;
    border: none !important;
    border-radius: 0 !important;
    box-shadow: none !important;
    padding: 0.625rem 0.875rem;
    font-size: 0.95rem;
    font-weight: 500;
    color: var(--gray-800);
    background: transparent;
    outline: none;
    min-width: 0;
  }

  .shortname-auto-btn {
    flex-shrink: 0;
    padding: 0 1rem;
    background: var(--gray-100);
    border: none;
    border-left: 1px solid var(--gray-200);
    color: var(--primary-color);
    font-size: 0.8125rem;
    font-weight: 700;
    letter-spacing: 0.03em;
    cursor: pointer;
    transition:
      background 0.15s ease,
      color 0.15s ease;
    white-space: nowrap;
  }

  .shortname-auto-btn:hover {
    background: var(--primary-color);
    color: #fff;
    border-left-color: var(--primary-color);
  }

  .shortname-help {
    color: var(--gray-500);
  }

  .shortname-error {
    color: var(--danger-color);
    font-size: 0.875rem;
    display: flex;
    align-items: center;
    gap: 0.375rem;
  }

  .shortname-input-group.input-error {
    border-color: var(--danger-color) !important;
    box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.1) !important;
  }

  .shortname-input-field.input-error {
    color: var(--danger-color);
  }

  .error-icon {
    width: 1rem;
    height: 1rem;
    flex-shrink: 0;
  }

  .tag-input-container {
    display: flex;
    align-items: center;
    gap: 0.75rem;
  }

  .tag-input {
    flex: 1;
    padding: 0.75rem 1rem;
    border: 1px solid var(--gray-200);
    border-radius: var(--radius-lg);
    font-size: 0.875rem;
    transition: all 0.2s ease;
    outline: none;
  }

  .tag-input:focus {
    border-color: var(--primary-color);
    box-shadow: 0 0 0 3px rgba(37, 99, 235, 0.1);
  }

  .add-tag-button {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.75rem 1.5rem;
    background: var(--primary-color);
    color: var(--white);
    border: none;
    border-radius: var(--radius-lg);
    font-weight: 500;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .add-tag-button:hover:not(:disabled) {
    background: var(--primary-dark);
    transform: translateY(-1px);
    box-shadow: var(--shadow-md);
  }

  .add-tag-button:disabled {
    opacity: 0.6;
    cursor: not-allowed;
  }

  .tags-container {
    display: flex;
    flex-wrap: wrap;
    gap: 0.75rem;
  }

  .tag-item {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem 0.75rem;
    background: var(--gray-100);
    border: 1px solid var(--gray-200);
    border-radius: var(--radius-xl);
    font-size: 0.875rem;
    color: var(--gray-700);
    transition: all 0.2s ease;
    position: relative;
  }

  .tag-item:hover {
    background: var(--gray-200);
    transform: translateY(-1px);
    box-shadow: var(--shadow-sm);
  }

  .tag-text {
    font-weight: 500;
  }

  .tag-remove {
    background: var(--danger-color);
    color: var(--white);
    border: none;
    border-radius: 50%;
    width: 1.25rem;
    height: 1.25rem;
    display: flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    transition: all 0.2s ease;
    margin-left: 0.25rem;
  }

  .tag-remove:hover {
    background: #dc2626;
    transform: scale(1.1);
  }

  .empty-state {
    text-align: center;
    display: flex;
    flex-direction: column;
    align-items: center;
    padding: 3rem 1rem;
    color: var(--gray-500);
  }

  .empty-state p {
    margin: 0;
    font-size: 0.875rem;
    margin-top: 12px;
  }

  .editor-container {
    border: 1px solid var(--gray-200);
    border-radius: var(--radius-lg);
    overflow: hidden;
    height: 500px;
  }

  .add-files-button {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.75rem 1.5rem;
    background: var(--primary-color);
    color: var(--white);
    border: none;
    border-radius: var(--radius-lg);
    font-weight: 500;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .add-files-button:hover {
    background: var(--primary-dark);
    transform: translateY(-1px);
    box-shadow: var(--shadow-md);
  }

  .attachments-list {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }

  .attachment-row {
    background: var(--white);
    border: 1px solid var(--gray-200);
    border-radius: var(--radius-lg);
    overflow: hidden;
    transition: all 0.2s ease;
    position: relative;
    display: flex;
    align-items: stretch;
    gap: 0;
  }

  .attachment-row:hover {
    border-color: var(--primary-color);
    box-shadow: var(--shadow-md);
  }

  .attachment-preview {
    flex: 0 0 10rem;
    width: 10rem;
    height: auto;
    min-height: 10rem;
    position: relative;
    overflow: hidden;
    background: var(--gray-50);
    border-right: 1px solid var(--gray-200);
  }

  .attachment-image,
  .attachment-video {
    width: 100%;
    height: 100%;
    object-fit: cover;
  }

  .video-overlay {
    position: absolute;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    background: rgba(0, 0, 0, 0.6);
    border-radius: 50%;
    width: 3rem;
    height: 3rem;
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .file-preview {
    width: 100%;
    height: 100%;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--gray-50);
  }

  .attachment-body {
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
    flex-direction: column;
    padding: 1rem 1rem 1rem 1rem;
    gap: 0.75rem;
  }

  .attachment-info {
    padding: 0;
    border-top: none;
    padding-right: 2.5rem;
  }

  .attachment-name {
    margin: 0 0 0.25rem 0;
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--gray-800);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .attachment-size {
    margin: 0;
    font-size: 0.75rem;
    color: var(--gray-500);
  }

  .attachment-metadata {
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.625rem;
  }

  .metadata-lang-grid {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 0.5rem;
  }

  .metadata-field {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  .metadata-label {
    font-size: 0.75rem;
    font-weight: 500;
    color: var(--gray-600);
  }

  .metadata-input {
    width: 100%;
    padding: 0.5rem 0.625rem;
    border: 1px solid var(--gray-200);
    border-radius: var(--radius-md, 0.5rem);
    font-size: 0.8125rem;
    color: var(--gray-800);
    background: var(--white);
    transition: border-color 0.15s ease, box-shadow 0.15s ease;
    resize: vertical;
    font-family: inherit;
  }

  .metadata-input:focus {
    outline: none;
    border-color: var(--primary-color);
    box-shadow: 0 0 0 2px rgba(99, 102, 241, 0.15);
  }

  .attachment-status-overlay {
    position: absolute;
    inset: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    pointer-events: none;
  }

  .attachment-status-overlay.uploading {
    background: rgba(17, 24, 39, 0.45);
  }

  .attachment-status-overlay.success {
    background: rgba(16, 185, 129, 0.35);
    color: #047857;
  }

  .attachment-status-overlay.error {
    background: rgba(239, 68, 68, 0.35);
    color: #b91c1c;
  }

  .attachment-status-overlay :global(.status-icon) {
    width: 2.25rem;
    height: 2.25rem;
    filter: drop-shadow(0 1px 2px rgba(0, 0, 0, 0.25));
  }

  .attachment-spinner {
    width: 1.75rem;
    height: 1.75rem;
    border: 3px solid rgba(255, 255, 255, 0.35);
    border-top-color: #ffffff;
    border-radius: 50%;
    animation: attachment-spin 0.75s linear infinite;
  }

  @keyframes attachment-spin {
    to { transform: rotate(360deg); }
  }

  .attachment-row[data-status="uploading"],
  .attachment-row[data-status="success"] {
    pointer-events: none;
  }

  .attachment-row[data-status="uploading"] .attachment-body,
  .attachment-row[data-status="success"] .attachment-body,
  .attachment-row[data-status="error"] .attachment-body {
    opacity: 0.75;
  }

  .attachments-upload-banner {
    display: flex;
    align-items: center;
    gap: 1rem;
    margin-bottom: 1rem;
    padding: 1rem 1.25rem;
    background: linear-gradient(
      135deg,
      rgba(99, 102, 241, 0.08) 0%,
      rgba(99, 102, 241, 0.15) 100%
    );
    border: 1px solid rgba(99, 102, 241, 0.25);
    border-radius: var(--radius-lg, 0.75rem);
    color: var(--primary-color, #4f46e5);
  }

  .attachments-upload-banner-spinner {
    flex: 0 0 1.5rem;
    width: 1.5rem;
    height: 1.5rem;
    border: 3px solid rgba(99, 102, 241, 0.25);
    border-top-color: var(--primary-color, #4f46e5);
    border-radius: 50%;
    animation: attachment-spin 0.75s linear infinite;
  }

  .attachments-upload-banner-text {
    flex: 1 1 auto;
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    min-width: 0;
  }

  .attachments-upload-banner-text strong {
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--primary-color, #4f46e5);
  }

  .attachments-upload-banner-progress {
    width: 100%;
    height: 0.375rem;
    background: rgba(99, 102, 241, 0.15);
    border-radius: 999px;
    overflow: hidden;
  }

  .attachments-upload-banner-progress-fill {
    height: 100%;
    background: var(--primary-color, #4f46e5);
    border-radius: 999px;
    transition: width 0.25s ease;
  }

  .remove-attachment {
    position: absolute;
    top: 0.5rem;
    right: 0.5rem;
    background: var(--danger-color);
    color: var(--white);
    border: none;
    border-radius: 50%;
    width: 2rem;
    height: 2rem;
    display: flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    transition: all 0.2s ease;
    opacity: 0;
  }

  .remove-attachment:disabled {
    cursor: not-allowed;
    opacity: 0 !important;
  }

  .attachment-row:hover .remove-attachment {
    opacity: 1;
  }

  .remove-attachment:hover {
    background: #dc2626;
    transform: scale(1.1);
  }

  .empty-attachments {
    text-align: center;
    padding: 4rem 2rem;
    background: var(--gray-50);
    border: 2px dashed var(--gray-200);
    border-radius: var(--radius-lg);
    color: var(--gray-500);
    display: flex;
    flex-direction: column;
    align-items: center;
  }

  .empty-attachments h3 {
    margin: 1rem 0 0.5rem 0;
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--gray-600);
  }

  .empty-attachments p {
    margin: 0;
    font-size: 0.875rem;
  }

  @media (max-width: 768px) {
    .content-wrapper {
      padding: 1rem;
    }

    .action-content {
      flex-direction: column;
      gap: 1.5rem;
    }

    .action-buttons {
      justify-content: center;
    }

    .section-content {
      padding: 1.5rem;
    }

    .tag-input-container {
      flex-direction: column;
      gap: 0.75rem;
    }

    .attachments-list {
      gap: 0.75rem;
    }

    .attachment-row {
      flex-direction: column;
    }

    .attachment-preview {
      flex: 0 0 9rem;
      width: 100%;
      height: 9rem;
      min-height: 0;
      border-right: none;
      border-bottom: 1px solid var(--gray-200);
    }

    .metadata-lang-grid {
      grid-template-columns: 1fr;
    }
  }

  .editor-selector {
    display: flex;
    align-items: center;
    gap: 1rem;
    margin-left: auto;
  }

  .editor-selector-label {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--gray-600);
  }

  .editor-toggle {
    display: flex;
    background: var(--gray-100);
    border-radius: var(--radius-lg);
    padding: 0.25rem;
    border: 1px solid var(--gray-200);
  }

  .editor-toggle-btn {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem 1rem;
    background: transparent;
    border: none;
    border-radius: var(--radius-md);
    color: var(--gray-600);
    font-size: 0.875rem;
    font-weight: 500;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .editor-toggle-btn:hover {
    color: var(--gray-800);
  }

  .editor-toggle-btn.active {
    background: var(--white);
    color: var(--primary-color);
    box-shadow: var(--shadow-sm);
  }

  .editor-icon {
    font-size: 1rem;
  }

  @media (max-width: 768px) {
    .editor-selector {
      flex-direction: column;
      gap: 0.5rem;
      margin-left: 0;
      margin-top: 1rem;
    }

    .editor-toggle-btn {
      padding: 0.375rem 0.75rem;
      font-size: 0.75rem;
    }

    .editor-icon {
      font-size: 0.875rem;
    }
  }

  /* Added styles for entry type selection */
  .entry-type-selector {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }

  .entry-type-selector.compact {
    flex-direction: row;
    flex-wrap: wrap;
    gap: 0.75rem;
  }

  .entry-type-selector.compact .entry-type-option {
    flex: 1 1 220px;
    padding: 0.625rem 0.875rem;
    border-width: 1px;
  }

  .entry-type-option {
    display: flex;
    align-items: flex-start;
    gap: 0.75rem;
    padding: 1rem;
    border: 2px solid var(--gray-200);
    border-radius: var(--radius-lg);
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .entry-type-option:hover {
    border-color: var(--primary-color);
    background: var(--gray-50);
  }

  .entry-type-option input[type="radio"] {
    margin-top: 0.125rem;
  }

  .entry-type-option input[type="radio"]:checked + .entry-type-label {
    color: var(--primary-color);
  }

  .entry-type-option:has(:global(input[type="radio"]:checked)) {
    border-color: var(--primary-color);
    background: rgba(37, 99, 235, 0.05);
  }

  .entry-type-label {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  .entry-type-label strong {
    font-weight: 600;
    color: var(--gray-800);
  }

  .entry-type-label small {
    color: var(--gray-600);
    font-size: 0.875rem;
  }

  .schema-selector {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }

  .schema-info {
    padding: 1rem;
    background: var(--gray-50);
    border-radius: var(--radius-lg);
    border: 1px solid var(--gray-200);
  }

  .schema-info h4 {
    margin: 0 0 0.5rem 0;
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--gray-800);
  }

  .schema-description {
    margin: 0;
    color: var(--gray-600);
    font-size: 0.875rem;
  }

  .loading-state {
    text-align: center;
    padding: 2rem;
    color: var(--gray-500);
  }

  @media (max-width: 768px) {
    .entry-type-selector {
      gap: 0.75rem;
    }

    .entry-type-option {
      padding: 0.75rem;
    }
  }

  .template-preview {
    background-color: var(--color-surface-secondary);
    border: 1px solid var(--color-border);
    border-radius: 0.5rem;
    padding: 1rem;
  }

  .template-form {
    display: flex;
    flex-direction: column;
    gap: 1.5rem;
  }

  .form-field {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .field-label {
    font-weight: 600;
    color: var(--color-text-primary);
    font-size: 0.875rem;
  }

  .required-indicator {
    color: var(--color-error);
    margin-left: 0.25rem;
  }

  .field-input {
    padding: 0.75rem;
    border: 2px solid #d1d5db;
    border-radius: 0.5rem;
    background-color: white;
    color: #374151;
    font-size: 0.875rem;
    transition: all 0.2s ease;
    width: 100%;
    box-sizing: border-box;
  }

  .field-input:focus {
    outline: none;
    border-color: #3b82f6;
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.15);
    background-color: white;
  }

  .field-input:hover {
    border-color: #9ca3af;
  }

  .field-textarea {
    resize: vertical;
    min-height: 80px;
    font-family: inherit;
  }

  .field-text {
    height: 42px;
  }

  .field-checkbox {
    transform: scale(1.3);
    cursor: pointer;
    width: 20px;
    height: 20px;
    accent-color: #3b82f6;
  }

  .checkbox-wrapper {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem 0;
  }

  .checkbox-label {
    font-size: 0.875rem;
    color: #374151;
  }

  .list-input-container {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .list-input-row {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .list-input {
    flex: 1;
  }

  .list-btn {
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 0.5rem 0.75rem;
    border: none;
    border-radius: 0.375rem;
    font-size: 0.875rem;
    font-weight: 500;
    cursor: pointer;
    transition: all 0.15s ease;
  }

  .list-btn-remove {
    background: #fee2e2;
    color: #dc2626;
    width: 36px;
    height: 36px;
    padding: 0;
  }

  .list-btn-remove:hover {
    background: #fecaca;
  }

  .list-btn-add {
    background: #eff6ff;
    color: #2563eb;
    border: 2px dashed #bfdbfe;
    margin-top: 0.25rem;
    align-self: flex-start;
  }

  .list-btn-add:hover {
    background: #dbeafe;
    border-color: #93c5fd;
  }

  .field-hint {
    display: block;
    margin-top: 0.375rem;
    color: #6b7280;
    font-size: 0.75rem;
    font-style: italic;
  }

  .template-data-title {
    font-size: 1rem;
    font-weight: 600;
    color: var(--color-text-primary);
    margin: 0 0 1rem 0;
  }

  .template-data-card {
    background: white;
    border: 1px solid #e5e7eb;
    border-radius: 0.75rem;
    box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
    margin-top: 1.5rem;
    overflow: hidden;
  }

  .template-data-card-header {
    background: #f9fafb;
    padding: 1rem 1.25rem;
    border-bottom: 1px solid #e5e7eb;
  }

  .template-data-card-header .template-data-title {
    margin: 0;
    font-size: 1rem;
    font-weight: 600;
    color: #374151;
  }

  .template-data-card-body {
    padding: 1.25rem;
  }

  .template-preview-section {
    margin-top: 1.5rem;
    padding-top: 1.5rem;
    border-top: 1px solid var(--color-border);
  }

  .template-preview-title {
    font-size: 1rem;
    font-weight: 600;
    color: var(--color-text-primary);
    margin: 0 0 1rem 0;
  }

  .template-preview.markdown-preview {
    background: white;
    border: 1px solid var(--color-border);
    border-radius: 0.5rem;
    padding: 1rem;
    font-family:
      "uthmantn",
      -apple-system,
      BlinkMacSystemFont,
      "Segoe UI",
      Roboto,
      "Helvetica Neue",
      Arial,
      sans-serif;
    line-height: 1.6;
    color: #374151;
  }

  .template-preview.markdown-preview :global(h1) {
    font-size: 1.875rem;
    font-weight: 700;
    margin: 1.5rem 0 1rem 0;
    color: #1f2937;
    border-bottom: 2px solid #e5e7eb;
    padding-bottom: 0.5rem;
  }

  .template-preview.markdown-preview :global(h2) {
    font-size: 1.5rem;
    font-weight: 600;
    margin: 1.25rem 0 0.75rem 0;
    color: #1f2937;
  }

  .template-preview.markdown-preview :global(h3) {
    font-size: 1.25rem;
    font-weight: 600;
    margin: 1rem 0 0.5rem 0;
    color: #1f2937;
  }

  .template-preview.markdown-preview :global(p) {
    margin: 0.75rem 0;
  }

  .template-preview.markdown-preview :global(ul),
  .template-preview.markdown-preview :global(ol) {
    margin: 0.75rem 0;
    padding-left: 1.5rem;
  }

  .template-preview.markdown-preview :global(ul) {
    list-style-type: disc;
  }

  .template-preview.markdown-preview :global(ol) {
    list-style-type: decimal;
  }

  .template-preview.markdown-preview :global(li) {
    margin: 0.25rem 0;
  }

  .template-preview.markdown-preview :global(blockquote) {
    margin: 1rem 0;
    padding: 0.75rem 1rem;
    background: #f9fafb;
    border-left: 4px solid #d1d5db;
    color: #6b7280;
  }

  .template-preview.markdown-preview :global(code) {
    background: #f3f4f6;
    padding: 0.125rem 0.25rem;
    border-radius: 0.25rem;
    font-family: "Monaco", "Menlo", "Ubuntu Mono", monospace;
    font-size: 0.875rem;
  }

  .template-preview.markdown-preview :global(pre) {
    background: #1f2937;
    color: #f9fafb;
    padding: 1rem;
    border-radius: 0.5rem;
    overflow-x: auto;
    margin: 1rem 0;
  }

  .template-preview.markdown-preview :global(pre code) {
    background: transparent;
    padding: 0;
    color: inherit;
  }

  .template-preview.markdown-preview :global(table) {
    width: 100%;
    border-collapse: collapse;
    margin: 1rem 0;
  }

  .template-preview.markdown-preview :global(th),
  .template-preview.markdown-preview :global(td) {
    padding: 0.5rem 0.75rem;
    border: 1px solid #d1d5db;
    text-align: left;
  }

  .template-preview.markdown-preview :global(th) {
    background: #f9fafb;
    font-weight: 600;
  }


  /* Schema-based Template Info Box */
  .template-info-box {
    background: linear-gradient(135deg, #dbeafe 0%, #bfdbfe 100%);
    border: 1px solid #3b82f6;
    border-radius: var(--radius-lg);
    padding: 1rem 1.25rem;
    margin-bottom: 1.5rem;
  }

  .template-info-text {
    margin: 0;
    color: #1e40af;
    font-size: 0.875rem;
    font-weight: 500;
    line-height: 1.5;
  }

  /* Structured Entry Layout */
  .structured-entry-layout {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 1.5rem;
    align-items: start;
  }

  .structured-entry-form {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }

  .structured-entry-preview {
    position: sticky;
    top: 1rem;
  }

  .structured-entry-preview .section {
    margin: 0;
  }

  .structured-entry-preview .section-content {
    max-height: calc(100vh - 200px);
    overflow-y: auto;
  }

  @media (max-width: 1024px) {
    .structured-entry-layout {
      grid-template-columns: 1fr;
    }

    .structured-entry-preview {
      position: static;
    }

    .structured-entry-preview .section-content {
      max-height: none;
    }
  }
</style>
