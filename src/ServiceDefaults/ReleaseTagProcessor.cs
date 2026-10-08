using System.Diagnostics;
using OpenTelemetry;

namespace Microsoft.Extensions.Hosting;

// Tags every span with the running release (APP_VERSION: v0.6.0 or sha-3f2a1c9), so App Insights
// queries and the workbook can compare releases (design §6).
public sealed class ReleaseTagProcessor(string release) : BaseProcessor<Activity>
{
    public const string Tag = "athlos.release";

    public override void OnStart(Activity data) => data.SetTag(Tag, release);
}
