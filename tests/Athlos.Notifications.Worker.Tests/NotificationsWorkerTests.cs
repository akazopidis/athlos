using System.Collections.Concurrent;
using System.Text.Json;
using Athlos.Messaging;
using Athlos.Messaging.Contracts;
using Athlos.Notifications.Worker;
using Athlos.Ordering.Api.Data;
using Athlos.Ordering.Api.Features.Refunds;
using Athlos.Ordering.Api.Features.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athlos.Notifications.Worker.Tests;

// The notifications-worker without Service Bus or ACS: messages are delivered to the consumer
// directly, owners come from a fake tenancy-api, emails go to a recording sender.
public sealed class NotificationsWorkerTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private readonly FakeOwners _owners = new();
    private readonly RecordingSender _sender = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sale_email_goes_to_each_owner_separately()
    {
        _owners.Emails[Tenant] = ["owner@coach.test", "co-owner@coach.test"];

        var outcome = await Deliver(SaleEmail());

        Assert.Equal("completed", outcome);
        Assert.Equal(["co-owner@coach.test", "owner@coach.test"], _sender.Sent.Select(s => s.To).Order());
        Assert.All(_sender.Sent, s => Assert.StartsWith("You made a sale: Strong start", s.Email.Subject, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recipients_are_deduplicated_and_a_workspace_without_owners_gets_nothing()
    {
        _owners.Emails[Tenant] = ["Owner@coach.test"];
        var both = SaleEmail() with { To = ["owner@coach.test"] };

        await Deliver(both);
        await Deliver(SaleEmail() with { ToOwnersOfTenant = Guid.CreateVersion7() });

        Assert.Single(_sender.Sent);
    }

    [Fact]
    public void Data_is_HTML_encoded_in_the_HTML_part_only()
    {
        var email = new EmailRenderer().Render(EmailTemplates.SaleMade, Data(title: "<b>Abs</b> & more"));

        Assert.Contains("&lt;b&gt;Abs&lt;/b&gt; &amp; more", email.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Abs</b>", email.Html, StringComparison.Ordinal);
        Assert.Contains("<b>Abs</b> & more", email.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_message_the_template_cannot_render_is_retried_and_an_unreadable_one_dead_lettered()
    {
        var missingData = await Deliver(SaleEmail() with { Data = new Dictionary<string, string> { ["programTitle"] = "x" } });
        var unknownTemplate = await Deliver(SaleEmail() with { Template = "no-such-template" });
        var unreadable = await DeliverRaw(BinaryData.FromString("{"));

        Assert.Equal(("abandoned", "abandoned", "dead-lettered"), (missingData, unknownTemplate, unreadable));
        Assert.Empty(_sender.Sent);
    }

    // Producer and template agree: what ordering-api queues renders without missing data.
    [Fact]
    public void Ordering_s_sale_email_renders()
    {
        var order = new Order
        {
            TenantId = Tenant, CustomerId = Guid.CreateVersion7(), CustomerEmail = "buyer@test.local", ProgramId = Guid.CreateVersion7(),
            ProgramTitle = "Strong start", CoachSlug = "anna", Amount = 4900, Currency = "EUR", PlatformFee = 490,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:PublicUrl"] = "https://athlos.test" }).Build();

        var email = new EmailRenderer().Render(EmailTemplates.SaleMade, StripeWebhook.SaleEmail(order, configuration).Data);

        Assert.Contains("44.10 EUR", email.PlainText, StringComparison.Ordinal);
        Assert.Contains("https://athlos.test/studio/anna", email.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordering_s_refund_email_renders()
    {
        var order = new Order
        {
            TenantId = Tenant, CustomerId = Guid.CreateVersion7(), CustomerEmail = "buyer@test.local", ProgramId = Guid.CreateVersion7(),
            ProgramTitle = "Strong start", CoachSlug = "anna", Amount = 4900, Currency = "EUR", PlatformFee = 490,
        };

        var email = new EmailRenderer().Render(EmailTemplates.RefundConfirmation, OrderRefunds.RefundEmail(order).Data);

        Assert.Equal("Your refund for Strong start", email.Subject);
        Assert.Contains("49.00 EUR", email.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EmailTemplates.SaleRefunded, "the platform refunded it in Stripe")]
    [InlineData(EmailTemplates.SaleDisputed, null)]
    public void Ordering_s_coach_emails_render(string template, string? reason)
    {
        var order = new Order
        {
            TenantId = Tenant, CustomerId = Guid.CreateVersion7(), CustomerEmail = "buyer@test.local", ProgramId = Guid.CreateVersion7(),
            ProgramTitle = "Strong start", CoachSlug = "anna", Amount = 4900, Currency = "EUR", PlatformFee = 490,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:PublicUrl"] = "https://athlos.test" }).Build();

        var email = new EmailRenderer().Render(template, OrderRefunds.CoachEmail(template, order, configuration, reason).Data);

        Assert.Contains("44.10 EUR", email.PlainText, StringComparison.Ordinal);
        Assert.Contains("https://athlos.test/studio/anna/sales", email.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4900, "EUR", "49.00 EUR")]
    [InlineData(123456, "USD", "1,234.56 USD")]
    [InlineData(5000, "JPY", "5,000 JPY")]
    public void Money_is_shown_in_the_currency_s_units(long amount, string currency, string expected) =>
        Assert.Equal(expected, MoneyText.Format(amount, currency));

    private static EmailNotification SaleEmail() =>
        new(EmailTemplates.SaleMade, To: [], ToOwnersOfTenant: Tenant, Locale: "en", Data());

    private static Dictionary<string, string> Data(string title = "Strong start") => new()
    {
        ["programTitle"] = title,
        ["price"] = "49.00 EUR",
        ["earnings"] = "44.10 EUR",
        ["salesUrl"] = "https://athlos.test/studio/anna",
    };

    private Task<string> Deliver(EmailNotification message) =>
        DeliverRaw(BinaryData.FromString(JsonSerializer.Serialize(message, JsonSerializerOptions.Web)));

    private async Task<string> DeliverRaw(BinaryData body)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<EmailRenderer>()
            .AddSingleton<IOwnerDirectory>(_owners)
            .AddSingleton<IEmailSender>(_sender)
            .AddScoped<IMessageHandler<EmailNotification>, SendEmail>()
            .BuildServiceProvider();
        var consumer = new QueueConsumer<EmailNotification>(
            services, services.GetRequiredService<IServiceScopeFactory>(), EmailNotification.Queue, NullLogger<QueueConsumer<EmailNotification>>.Instance);

        var settlement = new Settlement();
        await consumer.ProcessAsync(body, Guid.NewGuid().ToString(), deliveryCount: 1, settlement, Cancel);
        return settlement.Outcome;
    }

    private sealed class FakeOwners : IOwnerDirectory
    {
        public ConcurrentDictionary<Guid, List<string>> Emails { get; } = new();

        public Task<IReadOnlyList<string>> GetOwnerEmailsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(Emails.GetValueOrDefault(tenantId) ?? []);
    }

    private sealed class RecordingSender : IEmailSender
    {
        public ConcurrentQueue<(string To, RenderedEmail Email)> Sent { get; } = new();

        public Task SendAsync(string recipient, RenderedEmail email, CancellationToken cancellationToken)
        {
            Sent.Enqueue((recipient, email));
            return Task.CompletedTask;
        }
    }

    private sealed class Settlement : IMessageSettlement
    {
        public string Outcome { get; private set; } = "none";

        public Task CompleteAsync(CancellationToken cancellationToken) => Set("completed");

        public Task AbandonAsync(CancellationToken cancellationToken) => Set("abandoned");

        public Task DeadLetterAsync(string reason, string description, CancellationToken cancellationToken) => Set("dead-lettered");

        private Task Set(string outcome)
        {
            Outcome = outcome;
            return Task.CompletedTask;
        }
    }
}
