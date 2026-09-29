namespace Athlos.Tenancy.Data;

// Every tenant-owned entity implements this, gets the EF query filter, and has an RLS policy
// with ENABLE and FORCE ROW LEVEL SECURITY on its table (design §3.2).
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}
