using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Athlos.Tenancy.Data;

// Layer 4 (design §3.2): tells PostgreSQL who is asking, so the RLS policies can check it.
//
// set_config(..., true) is transaction-local: the values vanish at commit or rollback, so a pooled
// connection can never carry one request's tenant into the next. That is also why every database
// call must run inside a transaction (TenantTransaction does this per request); without one the
// settings are empty and the policies match no rows, which fails closed.
public sealed class TenantSessionInterceptor(ITenantContext tenant) : DbTransactionInterceptor
{
    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection, result);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        using var command = CreateCommand(connection, result);
        command.ExecuteNonQuery();
        return result;
    }

    private DbCommand CreateCommand(DbConnection connection, DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT set_config('app.tenant_id', @tenant, true), " +
            "set_config('app.user_id', @user, true), " +
            "set_config('app.scope', @scope, true)";
        AddParameter(command, "tenant", tenant.TenantId?.ToString() ?? "");
        AddParameter(command, "user", tenant.UserId?.ToString() ?? "");
        AddParameter(command, "scope", tenant.Scope == TenantScope.PublicRead ? "public-read" : "");
        return command;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
