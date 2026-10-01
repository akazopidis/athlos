using Azure;
using Azure.Communication.Email;
using Azure.Identity;

namespace Athlos.Notifications.Worker;

// Sends one rendered email to one address. Azure Communication Services in Azure; a sink locally.
public interface IEmailSender
{
    Task SendAsync(string recipient, RenderedEmail email, CancellationToken cancellationToken);
}

public sealed class EmailOptions
{
    public const string Section = "Email";

    // "Acs" sends through Azure Communication Services; "Sink" (the default) only writes files.
    public string Mode { get; set; } = "Sink";

    // Acs: the Communication Services endpoint (https://<name>.<region>.communication.azure.com).
    public string? Endpoint { get; set; }

    // Acs: the sender of the Azure-managed domain, DoNotReply@<guid>.azurecomm.net.
    public string? Sender { get; set; }

    // Sink: where emails are written; nothing is written when empty.
    public string? SinkFolder { get; set; }
}

// Azure Communication Services Email with the worker's managed identity (design §5.3, §11): no keys.
// WaitUntil.Started: ACS has accepted the email; delivery continues on its side.
public sealed class AcsEmailSender(EmailClient client, string sender) : IEmailSender
{
    public async Task SendAsync(string recipient, RenderedEmail email, CancellationToken cancellationToken)
    {
        var message = new EmailMessage(sender, recipient, new EmailContent(email.Subject) { PlainText = email.PlainText, Html = email.Html });
        await client.SendAsync(WaitUntil.Started, message, cancellationToken);
    }

    public static AcsEmailSender Create(EmailOptions options, string? managedIdentityClientId) =>
        new(
            new EmailClient(
                new Uri(options.Endpoint ?? throw new InvalidOperationException("Email:Endpoint is required in Acs mode.")),
                new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = managedIdentityClientId })),
            options.Sender ?? throw new InvalidOperationException("Email:Sender is required in Acs mode."));
}

// Local development (design §5.3): nothing leaves the machine. The email, with its recipient, goes to
// a file in the sink folder; the log line names the subject only, never the address.
public sealed partial class SinkEmailSender(EmailOptions options, TimeProvider time, ILogger<SinkEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string recipient, RenderedEmail email, CancellationToken cancellationToken)
    {
        string? file = null;
        if (!string.IsNullOrWhiteSpace(options.SinkFolder))
        {
            Directory.CreateDirectory(options.SinkFolder);
            file = Path.Combine(options.SinkFolder, $"{time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(file, $"To: {recipient}\nSubject: {email.Subject}\n\n{email.PlainText}\n\n----- HTML -----\n{email.Html}\n", cancellationToken);
        }

        LogSunk(logger, email.Subject, file ?? "(no sink folder)");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email \"{Subject}\" written to {File} instead of being sent")]
    private static partial void LogSunk(ILogger logger, string subject, string file);
}
