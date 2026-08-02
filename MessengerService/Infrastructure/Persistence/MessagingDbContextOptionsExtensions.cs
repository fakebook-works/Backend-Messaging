using Microsoft.EntityFrameworkCore;

namespace MessengerService.Infrastructure.Persistence;

public static class MessagingDbContextOptionsExtensions
{
    public static DbContextOptionsBuilder UseMessagingPostgreSql(
        this DbContextOptionsBuilder options,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options.UseNpgsql(
            connectionString,
            postgres => postgres.MigrationsHistoryTable(
                "__EFMigrationsHistory",
                MessagingDbContext.Schema));
        return options;
    }
}
