using Microsoft.Data.SqlClient;

namespace Fulfillment.IntegrationTests.Infrastructure;

/// <summary>Plain SQL reads of persisted state, independent of the EF Core model under test.</summary>
public static class Db
{
    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<List<Dictionary<string, object?>>> RowsAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Number of rows of each business table that belong to one customer.</summary>
    public static async Task<(int Orders, int Lines, int History, int Keys, int StartRequests)> CountForCustomerAsync(string connectionString, string customerId)
    {
        var rows = await RowsAsync(connectionString,
            """
            SELECT
              (SELECT COUNT(*) FROM orders.[Order] WHERE CustomerId = @c) AS Orders,
              (SELECT COUNT(*) FROM orders.OrderLine l JOIN orders.[Order] o ON o.OrderId = l.OrderId WHERE o.CustomerId = @c) AS Lines,
              (SELECT COUNT(*) FROM orders.OrderStatusHistory h JOIN orders.[Order] o ON o.OrderId = h.OrderId WHERE o.CustomerId = @c) AS History,
              (SELECT COUNT(*) FROM orders.IdempotencyKey k JOIN orders.[Order] o ON o.OrderId = k.OrderId WHERE o.CustomerId = @c) AS Keys,
              (SELECT COUNT(*) FROM orders.WorkflowStartRequest r JOIN orders.[Order] o ON o.OrderId = r.OrderId WHERE o.CustomerId = @c) AS StartRequests
            """,
            ("@c", customerId));
        var r = rows.Single();
        return ((int)r["Orders"]!, (int)r["Lines"]!, (int)r["History"]!, (int)r["Keys"]!, (int)r["StartRequests"]!);
    }

    public static async Task<int> CountKeysAsync(string connectionString, string key) =>
        await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM orders.IdempotencyKey WHERE [Key] = @k", ("@k", key));
}
