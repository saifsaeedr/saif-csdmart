// Headless smoke test for cxb + catalog dist bundles. Loads each
// index page in Chrome via puppeteer-core (system google-chrome —
// no browser download), captures every console error, uncaught
// exception, and failed network request, then forces every JS chunk
// in the dist asset tree to evaluate via dynamic import — catches
// "Foo is not defined" issues in lazy chunks that wouldn't fire just
// by visiting the entry page (the v0.8.16 prismjs class of bug).
//
// Invoked via admin_scripts/smoke-test.sh which handles puppeteer-core
// install in a workdir + spinning up the static HTTP server.

import puppeteer from "puppeteer-core";
import { readdirSync } from "node:fs";
import { join } from "node:path";

const CHROME_PATH = process.env.SMOKE_CHROME_PATH || "/usr/bin/google-chrome";

function listAssetUrls(distDir, prefix) {
    const out = [];
    function walk(dir, rel) {
        for (const ent of readdirSync(dir, { withFileTypes: true })) {
            const path = join(dir, ent.name);
            const u = rel ? `${rel}/${ent.name}` : ent.name;
            if (ent.isDirectory()) walk(path, u);
            else if (ent.isFile() && ent.name.endsWith(".js")) {
                out.push(`${prefix}${u}`);
            }
        }
    }
    walk(distDir, "");
    return out;
}

async function smokeTest({ label, indexUrl, distDir, assetsBaseUrl }) {
    console.log(`\n=== ${label} ===`);
    console.log(`index: ${indexUrl}`);
    const browser = await puppeteer.launch({
        executablePath: CHROME_PATH,
        headless: true,
        args: ["--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage"],
    });
    const findings = {
        consoleErrors: [],
        pageErrors: [],
        failedRequests: [],
        chunkLoadErrors: [],
    };
    try {
        const page = await browser.newPage();

        page.on("console", msg => {
            if (msg.type() === "error") {
                findings.consoleErrors.push({
                    text: msg.text(),
                    location: msg.location(),
                });
            }
        });
        page.on("pageerror", err => {
            findings.pageErrors.push({ message: err.message, stack: err.stack });
        });
        page.on("requestfailed", req => {
            findings.failedRequests.push({
                url: req.url(),
                failure: req.failure()?.errorText ?? "unknown",
            });
        });
        page.on("response", resp => {
            if (resp.status() >= 400 && resp.url().endsWith(".js")) {
                findings.failedRequests.push({
                    url: resp.url(),
                    failure: `HTTP ${resp.status()}`,
                });
            }
        });

        await page.goto(indexUrl, { waitUntil: "networkidle0", timeout: 30000 });
        // Some SPAs do post-load routing that destroys the initial JS
        // context. Settle, then try to settle the post-route page too.
        await new Promise(r => setTimeout(r, 2500));
        try { await page.waitForNetworkIdle({ timeout: 5000 }); } catch {}

        const chunkUrls = listAssetUrls(distDir, assetsBaseUrl);
        console.log(`forcing ${chunkUrls.length} chunks to load via dynamic import...`);

        async function runImports() {
            return page.evaluate(async (urls) => {
                const errors = [];
                for (const url of urls) {
                    try {
                        await import(/* @vite-ignore */ url);
                    } catch (e) {
                        errors.push({ url, message: e?.message || String(e) });
                    }
                }
                return errors;
            }, chunkUrls);
        }
        try {
            findings.chunkLoadErrors = await runImports();
        } catch (e) {
            if (/Execution context was destroyed/.test(e?.message || "")) {
                console.log("  page navigated mid-test, retrying dynamic-import sweep...");
                await new Promise(r => setTimeout(r, 1500));
                try {
                    findings.chunkLoadErrors = await runImports();
                } catch (e2) {
                    findings.chunkLoadErrors = [{ url: "<page>", message: `retry failed: ${e2.message}` }];
                }
            } else {
                throw e;
            }
        }
    } finally {
        await browser.close();
    }

    return findings;
}

// Caller passes `<label>=<distDir>=<urlBase>=<assetsSubdir>` triples.
// Example invocation from smoke-test.sh:
//   node smoke-test.mjs cxb=<dist>/cxb/dist/client=http://host:8123/cxb/=assets \
//                       catalog=<dist>/catalog/dist/client=http://host:8123/cat/=assets/js
const tests = process.argv.slice(2).map(spec => {
    const [label, distDir, indexUrl, assetsSubdir] = spec.split("=");
    return {
        label,
        indexUrl,
        distDir: join(distDir, assetsSubdir),
        assetsBaseUrl: `${indexUrl}${assetsSubdir}/`,
    };
});

if (tests.length === 0) {
    console.error("usage: smoke-test.mjs <label=distDir=indexUrl=assetsSubdir> [...]");
    process.exit(2);
}

let totalIssues = 0;
const summary = [];
for (const t of tests) {
    const f = await smokeTest(t);
    const issues = f.consoleErrors.length + f.pageErrors.length +
                   f.failedRequests.length + f.chunkLoadErrors.length;
    totalIssues += issues;
    summary.push({ label: t.label, issues });

    if (f.consoleErrors.length) {
        console.log(`  console.error (${f.consoleErrors.length}):`);
        for (const e of f.consoleErrors.slice(0, 10)) {
            console.log(`    - ${e.text}`);
            if (e.location?.url) console.log(`        at ${e.location.url}:${e.location.lineNumber}`);
        }
        if (f.consoleErrors.length > 10) console.log(`    ... ${f.consoleErrors.length - 10} more`);
    }
    if (f.pageErrors.length) {
        console.log(`  uncaught page errors (${f.pageErrors.length}):`);
        for (const e of f.pageErrors.slice(0, 10)) console.log(`    - ${e.message}`);
        if (f.pageErrors.length > 10) console.log(`    ... ${f.pageErrors.length - 10} more`);
    }
    if (f.failedRequests.length) {
        console.log(`  failed network requests (${f.failedRequests.length}):`);
        for (const e of f.failedRequests.slice(0, 10)) console.log(`    - ${e.failure}  ${e.url}`);
        if (f.failedRequests.length > 10) console.log(`    ... ${f.failedRequests.length - 10} more`);
    }
    if (f.chunkLoadErrors.length) {
        console.log(`  chunk import errors (${f.chunkLoadErrors.length}):`);
        for (const e of f.chunkLoadErrors.slice(0, 10)) {
            console.log(`    - ${e.url}`);
            console.log(`        ${e.message}`);
        }
        if (f.chunkLoadErrors.length > 10) console.log(`    ... ${f.chunkLoadErrors.length - 10} more`);
    }
    if (issues === 0) console.log("  clean — no errors");
}

console.log(`\n=== summary ===`);
for (const s of summary) console.log(`  ${s.label}: ${s.issues} issue(s)`);
process.exit(totalIssues > 0 ? 1 : 0);
