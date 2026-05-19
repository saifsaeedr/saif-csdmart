using System.Net;
using System.Text.RegularExpressions;
using Dmart.Models.Core;

namespace Dmart.Services;

// Loads the activation-email body templates (HTML + plain text) and renders
// them with per-user data. The email is sent as multipart/alternative
// carrying both parts; each format has its own template, and each resolves
// independently:
//
//   1. Embedded default at templates/ActivationEmailContent.{html,txt} —
//      always present in the AOT release binary because dmart.csproj does
//      <EmbeddedResource Include="templates/*.{html,txt}" LinkBase="templates" />.
//   2. Operator override at ~/.dmart/ActivationEmailContent.{html,txt} —
//      when present the override fully replaces the embedded template for
//      that format only. Operators can override either one, both, or
//      neither (per-format single-file replace; no per-fragment merge as we
//      do for languages).
//
// Variables exposed inside the templates: name, msisdn, shortname, link.
// To add more, populate the dict in BuildVars(...) below.
//
// Template language: a `{{varname}}` (or `{{ varname }}`) token substitutes
// the corresponding value. The HTML body renders HTML-escape every value
// (operator can't accidentally let a `<script>` in a displayname into the
// rendered email). The text body and the subject render values raw — text
// bodies are plain text and HtmlEncode would corrupt `&` etc., as would
// the subject line. Any unknown token is left verbatim so operators see
// immediately what wasn't substituted instead of getting an empty body or
// a silently-removed variable.
//
// Why not a real templating library: Scriban/Fluid/Stubble bring 100-500KB
// + reflection paths the AOT analyzer can't prove safe (requires
// TrimmerRootAssembly + IL warning suppressions to ship). SSTI vulnerability
// classes apply when an engine evaluates attacker-controlled template *text*
// — reducing the engine to a literal `{{var}}` regex eliminates the class.
// If the template ever needs conditionals/loops, the integration surface
// here is small enough to swap in one PR.
public sealed class ActivationTemplateLoader(ILogger<ActivationTemplateLoader> log)
{
    private string? _htmlBodyText;
    private string _htmlSource = "<unloaded>";

    private string? _textBodyText;
    private string _textSource = "<unloaded>";

    // {{ \s* name \s* }} where name is a C-style identifier. Compiled once
    // because Substitute() runs per email-send. The `|` operator from the
    // prior Scriban template syntax is NOT in [a-zA-Z0-9_], so legacy
    // `{{ name | html.escape }}` tokens will NOT match — they'd be left
    // verbatim. The embedded templates were updated; operators with their
    // own override must drop the `| html.escape` filter (HTML body renders
    // escape by default anyway).
    private static readonly Regex VarPattern = new(
        @"\{\{\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*\}\}",
        RegexOptions.Compiled);

    public void Load()
    {
        (_htmlBodyText, _htmlSource) = LoadFormat("html");
        (_textBodyText, _textSource) = LoadFormat("txt");
        if (_htmlBodyText is null && _textBodyText is null)
        {
            log.LogWarning("activation email templates not loaded — invitation emails will be empty");
        }
    }

    // Resolves one format (html or txt) through override → embedded. The
    // override file is preferred; if it can't be read OR is empty we fall
    // back to the embedded resource so an operator's permission issue or
    // an accidentally-truncated override doesn't take down the invitation
    // flow. Both sides resolving to null leaves that body empty — the
    // multipart sender either degrades to single-part or auto-derives the
    // other side (see RenderTextBody).
    private (string?, string) LoadFormat(string ext)
    {
        var picked = TryLoadOverride(ext);
        if (picked is { } o && o.text.Length > 0)
        {
            // Pre-multipart releases shipped the activation email as a single
            // HTML body under ActivationEmailContent.txt — operators with that
            // override see it loaded as the text/plain part now, which renders
            // literal <p> tags in mail clients that don't display HTML.
            // Detect the most obvious HTML smell (a `<…>` tag in a .txt
            // override) and emit a one-shot warning that points the operator
            // at the .html sibling, so the migration doesn't fail silently.
            if (string.Equals(ext, "txt", StringComparison.OrdinalIgnoreCase) && LooksLikeHtml(o.text))
            {
                log.LogWarning(
                    "activation email text override at {Source} appears to contain HTML — rename to ActivationEmailContent.html, "
                    + "or remove markup if you want it as plain text. Mail clients will render the <…> tags literally.",
                    o.origin);
            }
            log.LogInformation("activation email {Ext} template loaded from {Source}", ext, o.origin);
            return (o.text, o.origin);
        }
        if (picked is { } p)
        {
            log.LogWarning("activation email {Ext} override at {Source} is empty — falling back to embedded", ext, p.origin);
        }

        var embedded = LoadEmbedded(ext);
        if (embedded is { } e && e.text.Length > 0)
        {
            log.LogInformation("activation email {Ext} template loaded from {Source}", ext, e.origin);
            return (e.text, e.origin);
        }

        log.LogWarning("activation email {Ext} template not loaded — that part will be empty", ext);
        return (null, embedded?.origin ?? "<missing>");
    }

    // Operator override at ~/.dmart/ActivationEmailContent.<ext> — same root
    // convention as the language overlay in LanguageLoader.
    private (string text, string origin)? TryLoadOverride(string ext)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;
        var path = Path.Combine(home, ".dmart", $"ActivationEmailContent.{ext}");
        if (!File.Exists(path)) return null;
        try
        {
            return (File.ReadAllText(path), path);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "activation email {Ext} override read failed at {Path} — falling back to embedded", ext, path);
            return null;
        }
    }

    // Embedded resource name follows MSBuild's dotted convention:
    // "{RootNamespace}.templates.ActivationEmailContent.<ext>". We match on
    // the ".templates." marker so the loader is independent of the root
    // namespace (same pattern as LanguageLoader's ".languages." matching).
    private (string text, string origin)? LoadEmbedded(string ext)
    {
        try
        {
            var assembly = typeof(ActivationTemplateLoader).Assembly;
            var marker = $".templates.ActivationEmailContent.{ext}";
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                return (reader.ReadToEnd(), $"embedded:{name}");
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "activation email {Ext} embedded resource scan failed", ext);
        }
        return null;
    }

    // Renders the loaded HTML body template with per-user data. Returns
    // empty string when no template is loaded (logged as a warning at
    // Load()) so SmtpSender can decide whether to send multipart or degrade
    // to text-only.
    public string RenderHtmlBody(User user, string link)
    {
        if (_htmlBodyText is null) return string.Empty;
        return Substitute(_htmlBodyText, BuildVars(user, link), htmlEscape: true);
    }

    // Renders the loaded plain-text body template when one is available;
    // otherwise auto-derives a plain-text alternative from the rendered
    // HTML body so the multipart message always has a usable text part.
    // Auto-derive is the deepest fallback and only kicks in when both the
    // override .txt and the embedded .txt are unavailable (the embedded
    // .txt ships in the binary, so this branch is exercised mainly when
    // the operator has stripped the binary or the embedded resource fails
    // to load).
    public string RenderTextBody(User user, string link)
    {
        if (_textBodyText is not null)
        {
            return Substitute(_textBodyText, BuildVars(user, link), htmlEscape: false);
        }
        if (_htmlBodyText is null) return string.Empty;
        var html = Substitute(_htmlBodyText, BuildVars(user, link), htmlEscape: true);
        return HtmlToPlainText.Convert(html);
    }

    // Renders an inline subject template. Plain substitution — no
    // HtmlEncode because subject lines are plain text and HtmlEncode would
    // corrupt `&` (becomes `&amp;`), `<` (becomes `&lt;`), etc. The subject
    // source varies per user.Language so we don't cache parsed state —
    // Substitute() is just a regex Replace, which is cheap enough per call.
    public string RenderSubject(string source, User user, string link)
    {
        if (string.IsNullOrEmpty(source)) return string.Empty;
        return Substitute(source, BuildVars(user, link), htmlEscape: false);
    }

    // displayname.en wins, falling back to shortname so recipients always
    // see a name. Python parity: matches the prior
    // InvitationService.ActivationEmailBody behavior. Null msisdn / link
    // are mapped to empty so the template doesn't need to defend against
    // null and we don't render the literal string "null".
    private static Dictionary<string, string> BuildVars(User user, string link) =>
        new(StringComparer.Ordinal)
        {
            ["name"] = user.Displayname?.En ?? user.Shortname ?? string.Empty,
            ["msisdn"] = user.Msisdn ?? string.Empty,
            ["shortname"] = user.Shortname ?? string.Empty,
            ["link"] = link ?? string.Empty,
        };

    // Concrete Dictionary, not IReadOnlyDictionary — CA1859 (perf
    // analyzer) wants the concrete type for the indexed-lookup
    // hot-path. The dict is local to one Render call, never escapes.
    private static string Substitute(
        string template, Dictionary<string, string> vars, bool htmlEscape) =>
        VarPattern.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (!vars.TryGetValue(key, out var v)) return m.Value;
            return htmlEscape ? WebUtility.HtmlEncode(v) : v;
        });

    // Returns true when the content looks like HTML to the naked eye — used
    // only as a migration-warning heuristic for operator .txt overrides that
    // were written against the pre-multipart contract. False positives (a
    // plain-text file that happens to contain `<` and `>`) are acceptable:
    // the worst case is a one-line log warning the operator can ignore.
    // Looks for a `<word>` or `<word ...>` shape, which catches every common
    // HTML smell (<p>, <br>, <a href=…>) without matching arbitrary
    // angle-bracket text.
    private static readonly Regex HtmlTagSmell = new(
        @"<\s*[a-zA-Z][a-zA-Z0-9]*(\s[^>]*)?>",
        RegexOptions.Compiled);

    internal static bool LooksLikeHtml(string s) =>
        !string.IsNullOrEmpty(s) && HtmlTagSmell.IsMatch(s);
}

// Narrow HTML-to-plain-text converter used only as the deepest fallback for
// the text part of the multipart activation email — when no .txt template
// is loaded we derive a text alternative from the rendered HTML body.
//
// This is NOT a general-purpose HTML parser. It assumes the input is the
// embedded activation HTML template (or an operator's HTML override) — a
// small, well-formed document with paragraph/break/anchor markup. The
// rules are intentionally narrow:
//
//   1. Replace common block-closers (</p>, </div>, </li>, </h1>..</h6>) and
//      every form of <br> with a newline.
//   2. Strip every remaining tag with the simple regex <[^>]+> (does not
//      handle CDATA / comments / scripts — none of those appear in the
//      activation template).
//   3. HTML-decode entities so &amp; / &lt; / &quot; come through as
//      literal characters.
//   4. Trim trailing whitespace per line and collapse runs of 3+ newlines
//      down to 2 so the output reads as paragraphs.
internal static class HtmlToPlainText
{
    private static readonly Regex BlockBreaks = new(
        @"</(p|div|li|h[1-6])\s*>|<br\s*/?\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnyTag = new(
        "<[^>]+>",
        RegexOptions.Compiled);

    private static readonly Regex CollapseBlankLines = new(
        @"\n{3,}",
        RegexOptions.Compiled);

    private static readonly Regex TrailingWhitespacePerLine = new(
        @"[ \t]+(?=\n)",
        RegexOptions.Compiled);

    public static string Convert(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var withBreaks = BlockBreaks.Replace(html, "\n");
        var stripped = AnyTag.Replace(withBreaks, string.Empty);
        var decoded = WebUtility.HtmlDecode(stripped);
        var trimmedLines = TrailingWhitespacePerLine.Replace(decoded, string.Empty);
        var collapsed = CollapseBlankLines.Replace(trimmedLines, "\n\n");
        return collapsed.Trim();
    }
}
