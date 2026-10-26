using System.Collections.Concurrent;
using Athlos.Tenancy.Api.Features.Reports;

namespace Athlos.Testing;

// Stands in for catalog-api when someone reports a program and the admin decides: the programs on sale
// a test adds, and the block each one was last set to; or fails like an unreachable service.
public sealed class FakeReportedPrograms : IReportedPrograms
{
    private readonly ConcurrentDictionary<Guid, ProgramOnSale> _onSale = new();
    private readonly ConcurrentDictionary<Guid, bool> _failing = new();

    public ConcurrentDictionary<Guid, bool> Blocked { get; } = new();

    public ProgramOnSale Sell(Guid tenantId, string coachSlug, string? title = null)
    {
        var program = new ProgramOnSale(Guid.CreateVersion7(), tenantId, coachSlug, title ?? $"Program {Guid.NewGuid():N}"[..20]);
        _onSale[program.ProgramId] = program;
        return program;
    }

    public void FailFor(Guid programId) => _failing[programId] = true;

    public Task<ProgramOnSale?> FindOnSaleAsync(Guid programId, CancellationToken cancellationToken) =>
        Task.FromResult(_onSale.TryGetValue(programId, out var program) && !Blocked.GetValueOrDefault(programId) ? program : null);

    public Task<bool> BlockAsync(Guid tenantId, Guid programId, bool blocked, CancellationToken cancellationToken)
    {
        if (_failing.ContainsKey(programId))
        {
            throw new HttpRequestException("catalog-api is not reachable");
        }

        if (!_onSale.TryGetValue(programId, out var program) || program.TenantId != tenantId)
        {
            return Task.FromResult(false);
        }

        Blocked[programId] = blocked;
        return Task.FromResult(true);
    }
}
