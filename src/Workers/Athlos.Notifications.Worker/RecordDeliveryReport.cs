using Athlos.Messaging;

namespace Athlos.Notifications.Worker;

// Communication Services' delivery report, as Event Grid puts it on the email-events queue (Event Grid
// schema, one event per message). The stage's event subscription forwards only Bounced and Suppressed.
public sealed record EmailDeliveryReport(string EventType, EmailDeliveryReport.Report? Data)
{
    public const string Queue = "email-events";

    public const string ReportType = "Microsoft.Communication.EmailDeliveryReportReceived";

    public sealed record Report(string? Recipient, string? Status, string? MessageId);
}

// A bounced or suppressed address goes on the suppression list (design §5.3), so the next email to it
// is skipped. Any other status or event type is completed without effect. Recording is idempotent, so
// a redelivered report does no harm. The status is logged, never the address.
public sealed partial class RecordDeliveryReport(ISuppressionList suppressions, ILogger<RecordDeliveryReport> logger)
    : IMessageHandler<EmailDeliveryReport>
{
    private static readonly HashSet<string> Undeliverable = new(StringComparer.Ordinal) { "Bounced", "Suppressed" };

    public async Task HandleAsync(EmailDeliveryReport message, CancellationToken cancellationToken)
    {
        if (message.EventType != EmailDeliveryReport.ReportType
            || message.Data is not { Recipient: { Length: > 0 } recipient, Status: { } status }
            || !Undeliverable.Contains(status))
        {
            return;
        }

        await suppressions.RecordAsync(recipient, status, cancellationToken);
        LogRecorded(logger, status, message.Data.MessageId);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An email was not delivered ({Status}, ACS message {MessageId}); the address is skipped from now on")]
    private static partial void LogRecorded(ILogger logger, string status, string? messageId);
}
