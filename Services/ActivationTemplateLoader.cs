using System.Net;
using System.Text.RegularExpressions;
using Dmart.Models.Core;

namespace Dmart.Services;

// Loads the activation-email body template and renders it with per-user
// data. The body source can come from one of two places:
//
//   1. Embedded default at templates/ActivationEmailContent.txt — always
//      present in the AOT release binary because dmart.csproj does
//      <EmbeddedResource Include="templates/*.txt" /> with LinkBase="templates".
//   2. Operator override at ~/.dmart/ActivationEmailContent.txt — when present
//      it fully replaces the embedded template (single-file override; no
//      per-fragment merge as we do for languages, since this template is a
//      single document).
//
// Variables exposed inside the template: name, msisdn, shortname, link.
// To add more, populate the dict in BuildVars(...) below.
//
// Template language: a `{{varname}}` (or `{{ varname }}`) token substitutes
// the corresponding value. Body renders HTML-escape every value (operator
// can't accidentally let a `<script>` in a displayname into the rendered
// email). Subject renders pass values through raw — subject is plain text,
// HtmlEncode would corrupt `&` etc. Any unknown token is left verbatim so
// operators see immediately what wasn't substituted instead of getting an
// empty body or a silently-removed variable.
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
    private string? _bodyText;
    private string _bodySource = "<unloaded>";

    // {{ \s* name \s* }} where name is a C-style identifier. Compiled once
    // because Substitute() runs per email-send. The `|` operator from the
    // prior Scriban template syntax is NOT in [a-zA-Z0-9_], so legacy
    // `{{ name | html.escape }}` tokens will NOT match — they'd be left
    // verbatim. The embedded template was updated; operators with their own
    // override must drop the `| html.escape` filter (body renders escape
    // by default anyway).
    private static readonly Regex VarPattern = new(
        @"\{\{\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*\}\}",
        RegexOptions.Compiled);

    public void Load()
    {
        var (text, origin) = TryLoadOverride() ?? LoadEmbedded() ?? (string.Empty, "<missing>");
        _bodySource = origin;
        if (text.Length == 0)
        {
            log.LogWarning("activation email template not loaded — invitation emails will be empty");
            _bodyText = null;
            return;
        }
        _bodyText = text;
        log.LogInformation("activation email template loaded from {Source}", _bodySource);
    }

    // Operator override at ~/.dmart/ActivationEmailContent.txt — same root
    // convention as the language overlay in LanguageLoader.
    private (string text, string origin)? TryLoadOverride()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;
        var path = Path.Combine(home, ".dmart", "ActivationEmailContent.txt");
        if (!File.Exists(path)) return null;
        try
        {
            return (File.ReadAllText(path), path);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "activation email override read failed at {Path} — falling back to embedded", path);
            return null;
        }
    }

    // Embedded resource name follows MSBuild's dotted convention:
    // "{RootNamespace}.templates.ActivationEmailContent.txt". We match on the
    // ".templates." marker so the loader is independent of the root namespace
    // (same pattern as LanguageLoader's ".languages." matching).
    private (string text, string origin)? LoadEmbedded()
    {
        try
        {
            var assembly = typeof(ActivationTemplateLoader).Assembly;
            const string marker = ".templates.ActivationEmailContent.txt";
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
            log.LogWarning(ex, "activation email embedded resource scan failed");
        }
        return null;
    }

    // Renders the loaded body template with per-user data. HTML-escapes every
    // substituted value. Returns empty string when no template is loaded
    // (logged as a warning at Load()) so SmtpSender.SendEmailAsync can
    // refuse the send (it short-circuits on whitespace-only bodies).
    public string RenderBody(User user, string link)
    {
        if (_bodyText is null) return string.Empty;
        return Substitute(_bodyText, BuildVars(user, link), htmlEscape: true);
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
}
