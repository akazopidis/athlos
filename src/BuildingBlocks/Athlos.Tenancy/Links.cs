using System.ComponentModel.DataAnnotations;

namespace Athlos.Tenancy;

// Links a coach puts on public pages (backlog item 45): only https, so a shop served over HTTPS never
// links or loads anything over plain http. An empty value is no link and passes.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public class HttpsUrlAttribute : ValidationAttribute
{
    public HttpsUrlAttribute()
        : base("Use a link that starts with https://.")
    {
    }

    public override bool IsValid(object? value) =>
        value is not string text || string.IsNullOrWhiteSpace(text) || (Uri.TryCreate(text, UriKind.Absolute, out var uri) && Accepts(uri));

    protected virtual bool Accepts(Uri uri) => uri.Scheme == Uri.UriSchemeHttps;
}

// A workout's video: https on YouTube or Vimeo, the hosts customers expect a training video on.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class VideoUrlAttribute : HttpsUrlAttribute
{
    public static readonly IReadOnlySet<string> Hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "youtu.be", "www.youtube-nocookie.com",
        "vimeo.com", "www.vimeo.com", "player.vimeo.com",
    };

    public VideoUrlAttribute() => ErrorMessage = "Use a YouTube or Vimeo link that starts with https://.";

    protected override bool Accepts(Uri uri) => base.Accepts(uri) && Hosts.Contains(uri.Host);
}
