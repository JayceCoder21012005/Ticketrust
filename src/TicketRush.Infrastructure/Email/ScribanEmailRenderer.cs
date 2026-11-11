using System.Net;
using System.Reflection;
using Microsoft.Extensions.Options;
using Scriban;
using Scriban.Runtime;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Options;

namespace TicketRush.Infrastructure.Email;

/// <summary>
/// Renders the embedded <c>Email/Templates</c> with Scriban. Each <c>.txt</c> starts with <c>Subject: ...</c> and a blank
/// line; the <c>.html</c> is a body that is dropped into <c>_layout.html</c>. Model members are exposed in camelCase.
/// Values are HTML-encoded in the HTML part and left as they are in the subject and text parts.
/// </summary>
internal sealed class ScribanEmailRenderer(IOptions<FrontendOptions> frontend) : IEmailRenderer
{
    private const string ResourcePrefix = "Email.Templates.";
    private const string LayoutName = "_layout";
    private const string SubjectPrefix = "Subject:";

    private static readonly Lazy<Catalog> Templates = new(Catalog.Load);

    private readonly string _logoUrl = $"{frontend.Value.BaseUrl.TrimEnd('/')}/logo-email.png";

    public RenderedEmail Render(string template, object model)
    {
        if (!Templates.Value.Emails.TryGetValue(template, out var parts))
        {
            throw new ArgumentException($"Unknown email template '{template}'.", nameof(template));
        }

        var values = new ScriptObject();
        values.Import(model, renamer: CamelCase);

        var body = Run(parts.Html, values, escapeHtml: true);
        var layout = new ScriptObject { ["body"] = body, ["logoUrl"] = _logoUrl };
        return new RenderedEmail(
            Run(parts.Subject, values, escapeHtml: false).Trim(),
            Run(Templates.Value.Layout, layout, escapeHtml: false),
            Run(parts.Text, values, escapeHtml: false));
    }

    private static string CamelCase(MemberInfo member) => char.ToLowerInvariant(member.Name[0]) + member.Name[1..];

    private static string Run(Template template, ScriptObject values, bool escapeHtml)
    {
        var context = new EncodingContext(escapeHtml) { MemberRenamer = CamelCase, StrictVariables = true };
        context.PushGlobal(values);
        return template.Render(context);
    }

    /// <summary>Encodes every interpolated value ({{ x }}); literal template text is written as it is.</summary>
    private sealed class EncodingContext(bool escapeHtml) : TemplateContext
    {
        public override string? ObjectToString(object? value, bool nested = false)
        {
            var text = base.ObjectToString(value, nested);
            return escapeHtml ? WebUtility.HtmlEncode(text) : text;
        }
    }

    private sealed record EmailParts(Template Subject, Template Html, Template Text);

    private sealed record Catalog(Template Layout, IReadOnlyDictionary<string, EmailParts> Emails)
    {
        public static Catalog Load()
        {
            var assembly = typeof(ScribanEmailRenderer).Assembly;
            var names = assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".txt", StringComparison.Ordinal))
                .Select(n => n[ResourcePrefix.Length..^".txt".Length])
                .ToList();

            var emails = names.ToDictionary(name => name, name => LoadEmail(assembly, name));
            return new Catalog(Parse(Read(assembly, LayoutName + ".html"), LayoutName), emails);
        }

        private static EmailParts LoadEmail(Assembly assembly, string name)
        {
            var text = Read(assembly, name + ".txt");
            if (!text.StartsWith(SubjectPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Template '{name}.txt' must start with '{SubjectPrefix}'.");
            }

            var lineEnd = text.IndexOf('\n', StringComparison.Ordinal);
            var subject = text[SubjectPrefix.Length..lineEnd];
            return new EmailParts(
                Parse(subject, name + ".subject"),
                Parse(Read(assembly, name + ".html"), name + ".html"),
                Parse(text[(lineEnd + 1)..].TrimStart('\n'), name + ".txt"));
        }

        private static string Read(Assembly assembly, string file)
        {
            using var stream = assembly.GetManifestResourceStream(ResourcePrefix + file)
                ?? throw new InvalidOperationException($"Email template resource '{file}' is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        private static Template Parse(string source, string name)
        {
            var template = Template.Parse(source, name);
            return template.HasErrors
                ? throw new InvalidOperationException($"Email template '{name}' is invalid: {string.Join("; ", template.Messages)}")
                : template;
        }
    }
}
