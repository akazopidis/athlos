using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Athlos.Tenancy.Data;

public static class TenantScopes
{
    // Work that must touch several tenants (erasing one user's rows, design §5.2) does it one tenant
    // at a time: a fresh scope enters that tenant, and the work runs in one transaction tagged with
    // it, so the EF filter and RLS see that tenant only, exactly as for a request.
    public static async Task InTenantAsync<TDbContext>(
        this IServiceScopeFactory scopes, Guid tenantId, Func<TDbContext, Task> work, CancellationToken cancellationToken)
        where TDbContext : DbContext
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().EnterTenant(tenantId, role: null);
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await work(db);
        await transaction.CommitAsync(cancellationToken);
    }

    // The same for one user's own rows, for a slice that knows the user from something other than the
    // caller's sign-in (an email's signed unsubscribe link, design §5.3): RLS shows that user's rows only.
    public static async Task<T> AsUserAsync<TDbContext, T>(
        this IServiceScopeFactory scopes, Guid userId, Func<TDbContext, Task<T>> work, CancellationToken cancellationToken)
        where TDbContext : DbContext
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().ActForUser(userId);
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = await work(db);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
