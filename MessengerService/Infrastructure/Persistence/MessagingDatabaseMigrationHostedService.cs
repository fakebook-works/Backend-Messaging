using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MessengerService.Infrastructure.Persistence;

public sealed class DatabaseMigrationOptions
{
    public const string SectionName = "DatabaseMigrations";

    public bool Enabled { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 300;
}

public sealed class MessagingDatabaseMigrationHostedService(
    IConfiguration configuration,
    IOptions<DatabaseMigrationOptions> options,
    ILogger<MessagingDatabaseMigrationHostedService> logger) : IHostedService
{
    private const int AdvisoryLockNamespace = 0x46414B45; // "FAKE"
    private const int AdvisoryLockService = 0x4D534747; // "MSGG"

    private readonly DatabaseMigrationOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation(
                "Automatic Messaging database migrations are disabled. The database must be migrated before this instance starts serving traffic.");
            return;
        }

        var connectionString = configuration.GetConnectionString("PostgreSQLMigration");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = configuration.GetConnectionString("PostgreSQL");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:PostgreSQLMigration or ConnectionStrings:PostgreSQL is required when automatic database migrations are enabled.");
        }

        var migrationConnection = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Multiplexing = false,
            Enlist = false,
            ApplicationName = "fakebook-messaging-migrations"
        };
        var optionsBuilder = new DbContextOptionsBuilder<MessagingDbContext>();
        optionsBuilder.UseMessagingPostgreSql(migrationConnection.ConnectionString);
        await using var dbContext = new MessagingDbContext(optionsBuilder.Options);
        dbContext.Database.SetCommandTimeout(_options.CommandTimeoutSeconds);
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var lockHeld = false;
        try
        {
            // Keeping EF's connection explicitly open makes the session advisory lock cover
            // history inspection and every migration transaction. EF Core's own migration
            // lock remains active as well, coordinating with `dotnet ef database update`.
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
            await SetMigrationLockAsync(connection, acquire: true, cancellationToken);
            lockHeld = true;

            await dbContext.Database.MigrateAsync(cancellationToken);
            var remainingMigrations = await dbContext.Database
                .GetPendingMigrationsAsync(cancellationToken);
            if (remainingMigrations.Any())
            {
                throw new InvalidOperationException(
                    "Messaging migrations completed but pending EF migrations remain.");
            }

            logger.LogInformation(
                "Messaging EF migrations are current; history is stored in {Schema}.{HistoryTable}.",
                MessagingDbContext.Schema,
                "__EFMigrationsHistory");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Messaging database migration failed; startup is aborted.");
            throw;
        }
        finally
        {
            if (lockHeld && connection.State == ConnectionState.Open)
            {
                try
                {
                    await SetMigrationLockAsync(connection, acquire: false, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    // Closing the physical connection below releases session locks even if
                    // PostgreSQL cannot confirm the explicit unlock.
                    logger.LogWarning(exception, "Could not explicitly release the Messaging migration advisory lock; closing the connection will release it.");
                }
            }

            if (connection.State != ConnectionState.Closed)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SetMigrationLockAsync(
        NpgsqlConnection connection,
        bool acquire,
        CancellationToken cancellationToken)
    {
        var function = acquire ? "pg_advisory_lock" : "pg_advisory_unlock";
        await using var command = new NpgsqlCommand(
            $"SELECT {function}(@lockNamespace, @lockService);",
            connection)
        {
            CommandTimeout = _options.CommandTimeoutSeconds
        };
        command.Parameters.AddWithValue("lockNamespace", AdvisoryLockNamespace);
        command.Parameters.AddWithValue("lockService", AdvisoryLockService);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (!acquire && result is not true)
        {
            throw new InvalidOperationException(
                "The Messaging migration advisory lock was not held by this database session.");
        }
    }
}
