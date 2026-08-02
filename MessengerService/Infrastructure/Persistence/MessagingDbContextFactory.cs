using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MessengerService.Infrastructure.Persistence;

public sealed class MessagingDbContextFactory : IDesignTimeDbContextFactory<MessagingDbContext>
{
    public MessagingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSQLMigration");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSQL");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // `migrations add` only needs provider metadata and does not open this connection.
            connectionString =
                "Host=localhost;Port=5432;Database=fakebook;Username=fakebook;Password=design-time-only";
        }

        var optionsBuilder = new DbContextOptionsBuilder<MessagingDbContext>();
        optionsBuilder.UseMessagingPostgreSql(connectionString);

        return new MessagingDbContext(optionsBuilder.Options);
    }
}
