using MessengerService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MessengerService.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public void Automatic_migrations_are_enabled_by_default()
    {
        var options = new DatabaseMigrationOptions();

        Assert.True(options.Enabled);
        Assert.InRange(options.CommandTimeoutSeconds, 1, 3_600);
    }

    [Fact]
    public async Task Disabled_migrations_do_not_require_a_database_connection()
    {
        var service = new MessagingDatabaseMigrationHostedService(
            new ConfigurationBuilder().Build(),
            Options.Create(new DatabaseMigrationOptions { Enabled = false }),
            NullLogger<MessagingDatabaseMigrationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
    }

    [Fact]
    public void Ef_migrations_and_history_use_the_messenger_schema()
    {
        var optionsBuilder = new DbContextOptionsBuilder<MessagingDbContext>();
        optionsBuilder.UseMessagingPostgreSql(
            "Host=localhost;Database=fake;Username=fake;Password=fake");
        using var context = new MessagingDbContext(optionsBuilder.Options);

        Assert.Equal(
            new[]
            {
                "20260713194105_InitialMessagingSchema",
                "20260713201435_AddOutboxRetentionIndex",
                "20260718101941_AddMessageAttachmentMetadata",
                "20260728121452_AddStructuredSystemMessages",
                "20260728140525_ExpandMessageTextForEditHistory"
            },
            context.Database.GetMigrations());

        var historySql = context.GetService<IHistoryRepository>().GetCreateScript();
        Assert.Contains("messenger", historySql, StringComparison.Ordinal);
        Assert.Contains("__EFMigrationsHistory", historySql, StringComparison.Ordinal);
    }
}
