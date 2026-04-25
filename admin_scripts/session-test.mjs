// Session-stability smoke test. Drives a real browser login through the
// cxb or catalog SPA, then polls an authenticated endpoint at fixed
// intervals to verify the session stays alive across the configured
// duration. Surfaces the exact dmart error code (47=invalid, 48=expired,
// 49=not-authenticated) the moment auth breaks, plus the JWT iat/exp
// the server actually issued — both useful when diagnosing reports of
// "session expires in N minutes" without a clear cause.
//
// Driven by admin_scripts/session-test.sh.
//
// node session-test.mjs --url=URL --user=USER --pass=PASS \
//      [--duration=180] [--interval=30] [--ui=cxb|catalog]
//
// Args can also be set via env (SESSION_URL, SESSION_USER, ...).

import puppeteer from "puppeteer-core";

function arg(name, fallback) {
    const flag = process.argv.find(a => a.startsWith(`--${name}=`));
    if (flag) return flag.slice(name.length + 3);
    return process.env[`SESSION_${name.toUpperCase()}`] ?? fallback;
}

const URL_BASE = (arg("url", "http://localhost:8282/cxb/")).replace(/\/?$/, "/");
const USER = arg("user", "dmart");
const PASS = arg("pass");
const DURATION = parseInt(arg("duration", "180"), 10); // seconds
const INTERVAL = parseInt(arg("interval", "30"), 10);  // seconds
const UI = arg("ui", URL_BASE.includes("/cat/") ? "catalog" : "cxb").toLowerCase();
const CHROME_PATH = arg("chrome", "/usr/bin/google-chrome");

if (!PASS) {
    console.error("session-test: --pass=... (or SESSION_PASS=...) is required");
    process.exit(2);
}

const PROFILES = {
    // Each profile knows where to send the browser to find the login
    // form, the input selectors, and the post-login landing route used
    // for the keep-alive poll.
    cxb: {
        loginRoute: "management",
        userSel: "#username",
        passSel: "#password",
        pollEndpoint: "/user/profile",
    },
    catalog: {
        loginRoute: "login",
        userSel: "#identifier",
        passSel: "#password",
        pollEndpoint: "/user/profile",
    },
};

const profile = PROFILES[UI];
if (!profile) {
    console.error(`unknown ui '${UI}', expected cxb or catalog`);
    process.exit(2);
}

function decodeJwt(token) {
    try {
        const payload = token.split(".")[1];
        const padded = payload + "=".repeat((4 - payload.length % 4) % 4);
        const json = Buffer.from(padded.replace(/-/g, "+").replace(/_/g, "/"), "base64").toString("utf8");
        return JSON.parse(json);
    } catch { return null; }
}

console.log(`session-test  ui=${UI}  url=${URL_BASE}  user=${USER}  duration=${DURATION}s  interval=${INTERVAL}s`);

const browser = await puppeteer.launch({
    executablePath: CHROME_PATH,
    headless: true,
    args: ["--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage"],
});
const page = await browser.newPage();

const events = [];
page.on("response", r => {
    const u = r.url();
    if (u.includes("/user/") || u.includes("/managed/") || u.includes("/public/")) {
        events.push({ t: Date.now(), code: r.status(), method: r.request().method(), url: u });
    }
});
page.on("console", m => {
    if (m.type() === "error") events.push({ t: Date.now(), kind: "console.error", text: m.text() });
});

try {
    // Login through the SPA form (exercises the same path a real user takes).
    const loginUrl = URL_BASE + profile.loginRoute;
    console.log(`navigating to ${loginUrl} ...`);
    await page.goto(loginUrl, { waitUntil: "networkidle0", timeout: 30000 });
    await new Promise(r => setTimeout(r, 1500));

    await page.waitForSelector(profile.userSel, { timeout: 5000 });
    await page.click(profile.userSel);
    await page.type(profile.userSel, USER);
    await page.click(profile.passSel);
    await page.type(profile.passSel, PASS);
    const submit = await page.$('button[type="submit"]') || await page.$('button:not([type="button"])');
    if (!submit) throw new Error("no submit button found in login form");
    await submit.click();
    await new Promise(r => setTimeout(r, 3500));

    const post = await page.evaluate(() => ({
        token: localStorage.getItem("authToken"),
        signedinUser: localStorage.getItem("user"),
        url: location.href,
    }));
    if (!post.token) {
        console.error("login failed — no authToken in localStorage. Final URL:", post.url);
        const last = events.find(e => e.code && e.url.includes("/user/login"));
        if (last) console.error(`  /user/login response: HTTP ${last.code}`);
        process.exit(1);
    }
    console.log(`logged in. URL=${post.url}  token-len=${post.token.length}`);

    // Decode the JWT once so the operator can see what the server actually
    // issued: a 30-day token vs. a 120-second token immediately distinguishes
    // a config-driven "expires fast" report from a runtime issue.
    const payload = decodeJwt(post.token);
    if (payload?.iat && payload?.exp) {
        const lifetime = payload.exp - payload.iat;
        const now = Math.floor(Date.now() / 1000);
        console.log(`jwt iat=${payload.iat}  exp=${payload.exp}  lifetime=${lifetime}s (${(lifetime / 60).toFixed(1)}min)`);
        console.log(`     time-to-expiry from now: ${payload.exp - now}s`);
        console.log(`     server clock vs client: ${payload.iat - now}s skew (positive = server ahead)`);
    }

    // Poll the authenticated endpoint at the configured interval. The
    // request runs from page context so cookie + Bearer header are both
    // sent exactly the way the SPA itself sends them.
    const start = Date.now();
    const polls = [];
    const totalPolls = Math.floor(DURATION / INTERVAL);
    console.log(`\npolling ${profile.pollEndpoint} every ${INTERVAL}s for ${DURATION}s (${totalPolls} polls)...`);
    for (let i = 1; i <= totalPolls; i++) {
        await new Promise(r => setTimeout(r, INTERVAL * 1000));
        const result = await page.evaluate(async (endpoint) => {
            try {
                const tok = localStorage.getItem("authToken");
                const r = await fetch(endpoint, {
                    credentials: "include",
                    headers: tok ? { Authorization: `Bearer ${tok}` } : {},
                });
                let body = null;
                try { body = await r.json(); } catch {}
                return {
                    status: r.status,
                    errCode: body?.error?.code,
                    errMessage: body?.error?.message,
                    errType: body?.error?.type,
                };
            } catch (e) {
                return { status: 0, error: e?.message || String(e) };
            }
        }, profile.pollEndpoint);

        const elapsed = Math.round((Date.now() - start) / 1000);
        polls.push({ elapsed, ...result });
        const tag = result.status === 200
            ? "OK"
            : `BROKEN  errCode=${result.errCode ?? "-"}  errType=${result.errType ?? "-"}  msg="${result.errMessage ?? result.error ?? "?"}"`;
        console.log(`  t+${String(elapsed).padStart(4)}s  HTTP ${result.status}  ${tag}`);
        if (result.status !== 200) {
            console.log("  session broke — stopping early");
            break;
        }
    }

    const broken = polls.find(p => p.status !== 200);
    if (broken) {
        console.log(`\nresult: SESSION BROKE at t+${broken.elapsed}s with HTTP ${broken.status} (errCode=${broken.errCode ?? "-"})`);
        process.exit(1);
    }
    console.log(`\nresult: ${polls.length} consecutive successful polls over ${DURATION}s — session stable`);
} finally {
    await browser.close();
}
