using Athlos.Messaging;
using Athlos.Messaging.Contracts;

namespace Athlos.Notifications.Worker;

// One EmailNotification from the notifications queue (design §5.3): find the recipients, render the
// template, send one email per recipient (so owners never see each other's addresses).
// At least once: if a send fails, the message is retried and earlier recipients may get the email
// twice. A rare duplicate is accepted and documented; after maxDeliveryCount it is dead-lettered.
public sealed partial class SendEmail(EmailRenderer renderer, IOwnerDirectory owners, IEmailSender sender, ILogger<SendEmail> logger)
    : IMessageHandler<EmailNotification>
{
    public async Task HandleAsync(EmailNotification message, CancellationToken cancellationToken)
    {
        var email = renderer.Render(message.Template, message.Data);

        var recipients = new List<string>(message.To);
        if (message.ToOwnersOfTenant is { } tenantId)
        {
            recipients.AddRange(await owners.GetOwnerEmailsAsync(tenantId, cancellationToken));
        }

        var distinct = recipients.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var to in distinct)
        {
            await sender.SendAsync(to, email, cancellationToken);
        }

        // The count, never the addresses (design §5.3: addresses are not logged).
        LogSent(logger, message.Template, distinct.Count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Template} to {RecipientCount} recipient(s)")]
    private static partial void LogSent(ILogger logger, string template, int recipientCount);
}
