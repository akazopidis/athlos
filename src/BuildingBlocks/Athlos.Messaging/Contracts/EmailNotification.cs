using System.Globalization;

namespace Athlos.Messaging.Contracts;

// An email Athlos sends, on the notifications queue (design §5.3). Services queue it through their
// outbox; the notifications-worker renders the template and sends it. Recipients are either explicit
// addresses (To) or "the owners of this tenant" (ToOwnersOfTenant), which the worker resolves with
// tenancy-api when it sends, so other services never handle coaches' addresses.
// Addresses travel in the message only; they are never logged.
public sealed record EmailNotification(
    string Template,
    IReadOnlyList<string> To,
    Guid? ToOwnersOfTenant,
    string Locale,
    IReadOnlyDictionary<string, string> Data)
{
    public const string Queue = "notifications";
}

// Template names (src/Workers/Athlos.Notifications.Worker/Templates/{name}.html and .txt).
public static class EmailTemplates
{
    // To the customer, queued by enrollment-api after it created the enrollment.
    public const string PurchaseConfirmation = "purchase-confirmation";

    // To the coach's owners, queued by ordering-api when the order is paid.
    public const string SaleMade = "sale-made";

    // To the customer, queued by ordering-api with OrderRefunded (design §2.2), whoever refunded.
    public const string RefundConfirmation = "refund-confirmation";

    // To the coach's owners, queued by ordering-api when a sale is refunded at Stripe or by a lost
    // dispute (a refund the coach made in the studio needs no email).
    public const string SaleRefunded = "sale-refunded";

    // To the coach's owners, queued by ordering-api when a customer disputes a payment (design §2.2).
    public const string SaleDisputed = "sale-disputed";

    // To the coach's owners, queued by ordering-api when their share of a sale could not be
    // transferred and waits in payout_retries (design §2.2): their Stripe account needs attention.
    public const string PayoutFailed = "payout-failed";

    // To the invitee, queued by tenancy-api with the invite (design §5.2).
    public const string StaffInvite = "staff-invite";
}

// Money for display in emails: integer minor units plus a currency code, as everywhere (CLAUDE.md).
public static class MoneyText
{
    // ISO 4217 currencies without minor units that Stripe supports; everything else has two.
    private static readonly HashSet<string> ZeroDecimal =
        ["BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF"];

    public static string Format(long amount, string currency) =>
        ZeroDecimal.Contains(currency)
            ? $"{amount.ToString("N0", CultureInfo.InvariantCulture)} {currency}"
            : $"{(amount / 100m).ToString("N2", CultureInfo.InvariantCulture)} {currency}";
}
