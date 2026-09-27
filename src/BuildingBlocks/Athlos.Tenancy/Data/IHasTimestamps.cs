namespace Athlos.Tenancy.Data;

// created_at / updated_at on every table (design §2.3), stamped by TenantDbContext.
public interface IHasTimestamps
{
    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset UpdatedAt { get; set; }
}
