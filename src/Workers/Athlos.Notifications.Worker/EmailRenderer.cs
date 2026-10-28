using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace Athlos.Notifications.Worker;

public sealed record RenderedEmail(string Subject, string PlainText, string Html);

// Renders the templates in Templates/ (embedded resources): {name}.txt starts with a "Subject: ..."
// line, then the plain-text body; {name}.html is the HTML body. {{key}} is replaced by the message's
// data, HTML-encoded in the HTML part. A placeholder without data is an error, not an empty string:
// a producer and a template that disagree should fail loudly (and the message retries, then dead-letters).
//
// An email whose data has an unsubscribeUrl (an optional email, design §5.3) gets a footer with that
// link. The templates do not name it, so a message queued before its producer added the link still
// renders, and one with the link renders on a worker that does not know it yet.
public sealed partial class EmailRenderer
{
    public const string UnsubscribeUrl = "unsubscribeUrl";

    private const string SubjectPrefix = "Subject: ";

    private readonly ConcurrentDictionary<string, (string Subject, string Text, string Html)> _templates = new();

    public RenderedEmail Render(string template, IReadOnlyDictionary<string, string> data)
    {
        var (subject, text, html) = _templates.GetOrAdd(template, Load);
        var email = new RenderedEmail(Fill(subject, data, encode: false), Fill(text, data, encode: false), Fill(html, data, encode: true));
        return data.TryGetValue(UnsubscribeUrl, out var url) ? WithUnsubscribe(email, url) : email;
    }

    private static RenderedEmail WithUnsubscribe(RenderedEmail email, string url)
    {
        var footer = $"""<p style="color: #666; font-size: 0.75rem;">Don't want emails like this one? <a href="{HtmlEncoder.Default.Encode(url)}">Unsubscribe</a>, or choose your emails in your account.</p>""";
        var end = email.Html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return email with
        {
            PlainText = $"{email.PlainText.TrimEnd()}\n\nDon't want emails like this one? Unsubscribe: {url}\n",
            Html = end < 0 ? email.Html + footer : $"{email.Html[..end]}  {footer}\n{email.Html[end..]}",
        };
    }

    private static (string, string, string) Load(string template)
    {
        if (!TemplateName().IsMatch(template))
        {
            throw new ArgumentException($"'{template}' is not a template name.", nameof(template));
        }

        var text = Read($"{template}.txt");
        var html = Read($"{template}.html");
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        if (!text.StartsWith(SubjectPrefix, StringComparison.Ordinal) || newline < 0)
        {
            throw new InvalidOperationException($"Template {template}.txt must start with a '{SubjectPrefix}' line.");
        }

        return (text[SubjectPrefix.Length..newline].Trim(), text[(newline + 1)..].TrimStart('\r', '\n'), html);
    }

    private static string Read(string file)
    {
        using var stream = typeof(EmailRenderer).Assembly.GetManifestResourceStream($"Templates.{file}")
            ?? throw new InvalidOperationException($"No email template {file}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Fill(string template, IReadOnlyDictionary<string, string> data, bool encode) =>
        Placeholder().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (!data.TryGetValue(key, out var value))
            {
                throw new InvalidOperationException($"The email data has no '{key}'.");
            }

            return encode ? HtmlEncoder.Default.Encode(value) : value;
        });

    [GeneratedRegex(@"\{\{\s*([a-zA-Z][a-zA-Z0-9]*)\s*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$")]
    private static partial Regex TemplateName();
}
