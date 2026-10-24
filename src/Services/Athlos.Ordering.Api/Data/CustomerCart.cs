using Athlos.Tenancy.Data;

namespace Athlos.Ordering.Api.Data;

// A signed-in customer's cart (design §2.2, §8), kept with their account so it follows them from device
// to device. The customer's row, not a tenant's, like checkouts: RLS lets only a session acting for
// that customer read or write it (customer_id = app.user_id). The lines are what the shop showed;
// checkout asks catalog again and charges the current price.
public sealed class CustomerCart : IHasTimestamps
{
    // Entra object id of the customer: one cart each.
    public required Guid CustomerId { get; init; }

    public List<CartLine> Lines { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CartLine
{
    public Guid ProgramId { get; set; }

    public string CoachSlug { get; set; } = "";

    public string Slug { get; set; } = "";

    public string Title { get; set; } = "";

    public long PriceAmount { get; set; }

    public string Currency { get; set; } = "EUR";

    // For the shop's generated cover of a program without an image.
    public string? Category { get; set; }
}
