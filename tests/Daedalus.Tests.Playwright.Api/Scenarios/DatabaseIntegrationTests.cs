using System.Data;
using Npgsql;

namespace Daedalus.Tests.Playwright.Api.Scenarios;

/// <summary>
///     E2E integration tests with database container.
///     Demonstrates how to use Testcontainers for database-dependent tests.
/// </summary>
[TestFixture]
[Category("E2E-Integration")]
public class DatabaseIntegrationTests : ApiTestBase
{
    [Test]
    [Description("Database container starts successfully")]
    public async Task Should_Start_Database_Container()
    {
        // Act
        var connectionString = await StartPostgresContainerAsync(
            "test_db",
            "test_user",
            "test_password"
        ).ConfigureAwait(false);

        // Assert
        connectionString.Should().NotBeNullOrEmpty("connection string should be generated");
        DbConnectionString.Should().Be(connectionString);
    }

    [Test]
    [Description("Can connect to database container")]
    public async Task Should_Connect_To_Database()
    {
        // Arrange
        var connectionString = await StartPostgresContainerAsync().ConfigureAwait(false);

        // Act & Assert
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        connection.State.Should().Be(ConnectionState.Open);

        await connection.CloseAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Can execute SQL scripts in database container")]
    public async Task Should_Execute_SQL_Scripts()
    {
        // Arrange
        await StartPostgresContainerAsync().ConfigureAwait(false);

        var sqlScript = @"
            CREATE TABLE users (
                id SERIAL PRIMARY KEY,
                username VARCHAR(100) NOT NULL UNIQUE,
                email VARCHAR(255) NOT NULL UNIQUE,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            INSERT INTO users (username, email) VALUES
                ('john_doe', 'john@example.com'),
                ('jane_smith', 'jane@example.com'),
                ('bob_wilson', 'bob@example.com');
        ";

        // Act
        await ExecuteSqlAsync(sqlScript).ConfigureAwait(false);

        // Assert - Verify data was inserted
        await using var connection = new NpgsqlConnection(DbConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM users", connection);
        var count = (long)await command.ExecuteScalarAsync().ConfigureAwait(false);

        count.Should().Be(3, "should have inserted 3 users");

        await connection.CloseAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Can query data from database container")]
    public async Task ShouldQueryDatabaseData()
    {
        // Arrange
        await StartPostgresContainerAsync().ConfigureAwait(false);

        await ExecuteSqlAsync(@"
            CREATE TABLE products (
                id SERIAL PRIMARY KEY,
                name VARCHAR(255) NOT NULL,
                price DECIMAL(10, 2) NOT NULL,
                stock INT NOT NULL
            );

            INSERT INTO products (name, price, stock) VALUES
                ('Product A', 29.99, 100),
                ('Product B', 39.99, 50),
                ('Product C', 49.99, 75);
        ").ConfigureAwait(false);

        // Act
        await using var connection = new NpgsqlConnection(DbConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "SELECT id, name, price, stock FROM products WHERE price > @price ORDER BY price",
            connection);

        command.Parameters.AddWithValue("@price", 35.00m);

        var products = new List<(int Id, string Name, decimal Price, int Stock)>();

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            products.Add((
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetDecimal(2),
                reader.GetInt32(3)
            ));
        }

        await connection.CloseAsync().ConfigureAwait(false);

        // Assert
        products.Should().HaveCount(2);
        products.Should().AllSatisfy(p => p.Price.Should().BeGreaterThan(35));
    }

    [Test]
    [Description("Multiple tests can use separate database instances")]
    public async Task ShouldUseIsolatedDatabaseInstances()
    {
        // Arrange
        var connectionString = await StartPostgresContainerAsync(
            "isolated_db_test"
        ).ConfigureAwait(false);

        await ExecuteSqlAsync(@"
            CREATE TABLE test_data (
                id SERIAL PRIMARY KEY,
                value VARCHAR(255)
            );

            INSERT INTO test_data (value) VALUES ('test_value');
        ").ConfigureAwait(false);

        // Act
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM test_data", connection);
        var count = (long)await command.ExecuteScalarAsync().ConfigureAwait(false);

        await connection.CloseAsync().ConfigureAwait(false);

        // Assert
        count.Should().Be(1);
    }

    [Test]
    [Description("Database cleanup after test")]
    public async Task ShouldCleanUpDatabaseAfterTest()
    {
        // Arrange
        await StartPostgresContainerAsync().ConfigureAwait(false);

        // Act
        var tableName = "cleanup_test";
        await ExecuteSqlAsync($@"
            CREATE TABLE {tableName} (id SERIAL PRIMARY KEY, data VARCHAR(255))
        ").ConfigureAwait(false);

        // Assert
        await using var connection = new NpgsqlConnection(DbConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = '{tableName}')",
            connection);

        var exists = (bool)await command.ExecuteScalarAsync().ConfigureAwait(false);
        exists.Should().BeTrue();

        await connection.CloseAsync().ConfigureAwait(false);

        // PostgreSQL container will be disposed in TearDown
    }
}
