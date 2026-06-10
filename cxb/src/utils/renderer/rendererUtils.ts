export function generateObjectFromSchema(schema) {
    if (schema.type === 'object' && schema.properties) {
        const generatedObject = {};
        Object.keys(schema.properties).forEach((property) => {
            const propertySchema = schema.properties[property];
            if (propertySchema.type === 'object') {
                generatedObject[property] = generateObjectFromSchema(propertySchema);
                if (generatedObject[property] === undefined) {
                    generatedObject[property] = {};
                }
            } else if (propertySchema.type === 'array' && propertySchema.items) {
                generatedObject[property] = [generateObjectFromSchema(propertySchema.items)];
                if (generatedObject[property][0] === undefined) {
                    generatedObject[property] = [];
                }
            } else {
                if (propertySchema.type === 'string') {
                    generatedObject[property] = "";
                } else if (propertySchema.type === 'number') {
                    generatedObject[property] = 0;
                } else if (propertySchema.type === 'integer') {
                    generatedObject[property] = 0;
                } else if (propertySchema.type === 'boolean') {
                    generatedObject[property] = true;
                } else {
                    generatedObject[property] = null;
                }
            }
        });
        return generatedObject;
    }
}

export function generateSchemaFromObject(obj: any): any {
    if (obj === null || obj === undefined) {
        return { type: "string" }; // default fallback
    }

    if (Array.isArray(obj)) {
        if (obj.length > 0) {
            return {
                type: "array",
                items: generateSchemaFromObject(obj[0])
            };
        }
        return { type: "array", items: { type: "string" } };
    }

    if (typeof obj === 'object') {
        const properties: Record<string, any> = {};
        Object.keys(obj).forEach(key => {
            properties[key] = generateSchemaFromObject(obj[key]);
        });
        return {
            type: "object",
            properties
        };
    }

    if (typeof obj === 'number') {
        return Number.isInteger(obj) ? { type: "integer" } : { type: "number" };
    }

    if (typeof obj === 'boolean') {
        return { type: "boolean" };
    }

    return { type: "string" };
}

/**
 * Infers a JSON-Schema type string from a runtime value. Used by the form
 * renderer to render data props that the schema does not declare.
 */
export function inferType(value: any): string {
    return generateSchemaFromObject(value).type;
}

/**
 * Ordered union of a schema's declared property names and the keys actually
 * present in the data object. Schema order comes first (preserving the schema's
 * intended layout); any extra data-only keys are appended. This guarantees that
 * every prop in the record data is rendered, even when absent from the schema.
 */
export function unionKeys(schema: any, value: any): string[] {
    const keys: string[] = [];
    const seen = new Set<string>();

    if (schema && schema.properties) {
        for (const key of Object.keys(schema.properties)) {
            if (!seen.has(key)) {
                seen.add(key);
                keys.push(key);
            }
        }
    }

    if (value && typeof value === "object" && !Array.isArray(value)) {
        for (const key of Object.keys(value)) {
            if (!seen.has(key)) {
                seen.add(key);
                keys.push(key);
            }
        }
    }

    return keys;
}

/**
 * Builds a sensible default value for a schema node, used when adding a new
 * array item. Objects are seeded from their properties, arrays start empty,
 * and scalars use their declared default or a type-appropriate empty value.
 */
export function buildDefaultForSchema(schema: any): any {
    if (!schema || !schema.type) {
        return "";
    }
    switch (schema.type) {
        case "object":
            return schema.properties ? (generateObjectFromSchema(schema) ?? {}) : {};
        case "array":
            return schema.default ?? [];
        case "number":
        case "integer":
            return schema.default !== undefined ? schema.default : null;
        case "boolean":
            return schema.default !== undefined ? schema.default : false;
        case "string":
            return schema.default !== undefined ? schema.default : "";
        default:
            return null;
    }
}

export function scrollToElById(elementId: string) {
    const el = document.getElementById(elementId);
    if (el) {
        el.scrollIntoView({ behavior: "smooth" });
    }
}