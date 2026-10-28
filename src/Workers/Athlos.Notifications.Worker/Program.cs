using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Notifications.Worker;
using Athlos.Tenancy;

// The notifications-worker (design §2.1, §5.3): no HTTP API and no database. It receives
// EmailNotification from the notifications queue and sends email through Azure Communication Services
// (or a local sink), and Communication Services' bounce reports from the email-events queue.
// Container Apps scales it from zero on the queues' length.
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.AddQueueConsumer<EmailNotification, SendEmail>(EmailNotification.Queue);
builder.AddQueueConsumer<EmailDeliveryReport, RecordDeliveryReport>(EmailDeliveryReport.Queue);
builder.Services.AddSingleton<EmailRenderer>();
builder.Services.AddSingleton(TimeProvider.System);

// Sale emails go to a workspace's owners, whom tenancy-api knows; asked with a signed call. Tenancy
// also keeps the addresses that bounce.
builder.Services.AddGatewaySignature(builder.Configuration);
builder.Services.AddHttpClient<IOwnerDirectory, TenancyOwnerDirectory>(http => http.BaseAddress = new Uri("https+http://tenancy-api"))
    .AddSignedRequests();
builder.Services.AddHttpClient<ISuppressionList, TenancySuppressionList>(http => http.BaseAddress = new Uri("https+http://tenancy-api"))
    .AddSignedRequests();

var email = builder.Configuration.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions();
builder.Services.AddSingleton(email);
if (string.Equals(email.Mode, "Acs", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IEmailSender>(_ => AcsEmailSender.Create(email, builder.Configuration["AZURE_CLIENT_ID"]));
}
else
{
    builder.Services.AddSingleton<IEmailSender, SinkEmailSender>();
}

builder.Build().Run();
