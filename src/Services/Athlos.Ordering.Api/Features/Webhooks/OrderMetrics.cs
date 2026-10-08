using System.Diagnostics.Metrics;
using Athlos.Tenancy;

namespace Athlos.Ordering.Api.Features.Webhooks;

// athlos.orders.paid (design §6): paid orders per coach, for the tenant-aware workbook.
public static class OrderMetrics
{
    private static readonly Meter Meter = new("Athlos.Ordering");

    private static readonly Counter<long> Paid = Meter.CreateCounter<long>("athlos.orders.paid", description: "Orders paid, per coach (tenant).");

    public static void OrderPaid(Guid tenantId, string coachSlug, string currency) =>
        Paid.Add(1,
            new KeyValuePair<string, object?>(TelemetryTags.TenantId, tenantId.ToString()),
            new KeyValuePair<string, object?>(TelemetryTags.TenantSlug, coachSlug),
            new KeyValuePair<string, object?>("currency", currency));
}
