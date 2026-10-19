namespace Athlos.Catalog.Api.Data;

// A program the customer saved for later (design §2.4, the wishlist). The customer's row, not a
// tenant's, like ordering's checkouts: RLS lets only that customer read or change it
// (customer_id = app.user_id). The program's id is public; its foreign key cascades, so a deleted
// program (or workspace) takes the saved rows with it.
public sealed class SavedProgram
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    // Entra object id of the customer.
    public required Guid CustomerId { get; init; }

    public required Guid ProgramId { get; init; }

    public DateTimeOffset SavedAt { get; init; }
}
