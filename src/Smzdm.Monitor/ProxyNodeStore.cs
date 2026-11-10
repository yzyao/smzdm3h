using Npgsql;

namespace Smzdm.Monitor;

public sealed record ProxyNode(string Name, string Url, bool IsHealthy, bool IsLeased);

public interface IProxyNodeStore
{
    Task<IReadOnlyList<ProxyNode>> ReadAsync(CancellationToken cancellationToken);
}

public sealed class ProxyNodeStore(MonitorOptions options) : IProxyNodeStore
{
    public async Task<IReadOnlyList<ProxyNode>> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(options.ProxyDbConnectionString)
            {
                Timeout = 8, CommandTimeout = 8
            };
            settings.Options = (settings.Options + " -c default_transaction_read_only=on").Trim();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            // Same table/column names as Vantage. No migrations, health updates or leases
            // are written. A leased node belongs to a Vantage worker and is not a candidate.
            await using var command = new NpgsqlCommand("""
                SELECT "Name", "Url", "IsHealthy",
                       COALESCE("LeaseUntilUtc" > (now() AT TIME ZONE 'UTC'), false)
                FROM proxy_nodes ORDER BY "Name", "Url"
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            var nodes = new List<ProxyNode>();
            while (await reader.ReadAsync(timeout.Token))
                nodes.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
            return nodes;
        }
        catch (Exception exception) when (exception is NpgsqlException or ArgumentException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var detail = exception is PostgresException postgres ? $"SQLSTATE {postgres.SqlState}" : exception.GetType().Name;
            // Never include provider exception text/inner exceptions: they may expose a
            // connection string or the original proxy credentials.
            throw new InvalidDataException($"读取 Vantage 代理池失败（{detail}），本轮不采集，不回退直连。");
        }
    }
}
