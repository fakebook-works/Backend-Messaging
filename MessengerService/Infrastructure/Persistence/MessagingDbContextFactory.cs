using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MessengerService.Infrastructure.Persistence;

public sealed class MessagingDbContextFactory : IDesignTimeDbContextFactory<MessagingDbContext>
{
    public MessagingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSQL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // `migrations add` only needs provider metadata and does not open this connection.
            connectionString =
                "Host=localhost;Port=5432;Database=fakebook;Username=fakebook;Password=design-time-only";
        }

        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseNpgsql(
                connectionString,
                postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", MessagingDbContext.Schema))
            .Options;

        return new MessagingDbContext(options);
    }
}
