using HotChocolate.AspNetCore;
using MessengerService.Application;
using MessengerService.Application.Abstractions;
using MessengerService.Configuration;
using MessengerService.Contracts.Internal;
using MessengerService.Controllers;
using MessengerService.GraphQL;
using MessengerService.GraphQL.Types;
using MessengerService.Infrastructure.Http;
using MessengerService.Infrastructure.Persistence;
using MessengerService.Infrastructure.Realtime;
using MessengerService.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSQL");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:PostgreSQL is required. Supply it through environment configuration or User Secrets.");
}

builder.Services.AddDbContext<MessagingDbContext>(options =>
    options.UseNpgsql(
        connectionString,
        postgres => postgres.MigrationsHistoryTable(
            "__EFMigrationsHistory",
            MessagingDbContext.Schema)));

builder.Services
    .AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection(GatewayOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>();

builder.Services
    .AddOptions<InternalServicesOptions>()
    .Bind(builder.Configuration.GetSection(InternalServicesOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<InternalServicesOptions>, InternalServicesOptionsValidator>();

builder.Services
    .AddOptions<MessagingOptions>()
    .Bind(builder.Configuration.GetSection(MessagingOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<MessagingOptions>, MessagingOptionsValidator>();

builder.Services
    .AddOptions<MessagingRulesOptions>()
    .Bind(builder.Configuration.GetSection(MessagingRulesOptions.SectionName))
    .Validate(options => options.MaxGroupParticipants is >= 3 and <= 1_000,
        "Messaging:MaxGroupParticipants must be between 3 and 1000.")
    .Validate(options => options.MaxMessageLength is >= 1 and <= 10_000,
        "Messaging:MaxMessageLength must be between 1 and 10000.")
    .Validate(options => options.MaxAttachmentsPerMessage is >= 0 and <= 10,
        "Messaging:MaxAttachmentsPerMessage must be between 0 and 10.")
    .Validate(options => options.MaxAttachmentUrlLength is >= 1 and <= 2_048,
        "Messaging:MaxAttachmentUrlLength must be between 1 and 2048.")
    .Validate(options => options.MaxPresenceUserIds is >= 1 and <= 250,
        "Messaging:MaxPresenceUserIds must be between 1 and 250.")
    .Validate(options => options.EditWindowMinutes is > 0 and <= 1_440,
        "Messaging:EditWindowMinutes must be between 1 and 1440.")
    .Validate(options => options.PresenceTtlSeconds > 0 && options.TypingTtlSeconds > 0,
        "Messaging presence and typing TTL values must be positive.")
    .ValidateOnStart();

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ITrustedUserContextAccessor, TrustedUserContextAccessor>();
builder.Services.AddScoped<IMessagingUserProvisioningService, MessagingUserProvisioningService>();
builder.Services.AddScoped<DirectContactQueryService>();
builder.Services.AddScoped<MessagingApplicationService>();
builder.Services.AddSingleton<ISubscriptionAuthorizationChecker, SubscriptionAuthorizationChecker>();
builder.Services.AddSingleton<OutboxWakeSignal>();
builder.Services
    .AddHttpClient<ISocialGraphPermissionClient, SocialGraphPermissionClient>(client =>
        client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services
    .AddHttpClient<IUploadMediaClient, UploadMediaClient>(client =>
        client.Timeout = Timeout.InfiniteTimeSpan);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var subscriptionConnectionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
{
    Pooling = false,
    KeepAlive = 30,
    Enlist = false,
    ApplicationName = "fakebook-messaging-subscriptions"
};
var subscriptionDataSource = NpgsqlDataSource.Create(subscriptionConnectionBuilder.ConnectionString);
builder.Services.AddSingleton(subscriptionDataSource);

builder.Services
    .AddGraphQLServer("Messaging")
    .AddSourceSchemaDefaults()
    .AddQueryType<MessagingQuery>()
    .AddMutationType<MessagingMutation>()
    .AddSubscriptionType<MessagingSubscription>()
    .AddType<UserReference>()
    .AddTypeExtension<ConversationParticipantTypeExtension>()
    .AddTypeExtension<MessageTypeExtension>()
    .AddTypeExtension<MessageReactionTypeExtension>()
    .AddErrorFilter<MessagingErrorFilter>()
    .AddPostgresSubscriptions(options =>
    {
        options.ChannelName = "fakebook_messaging_subscriptions";
        options.ConnectionFactory = subscriptionDataSource.OpenConnectionAsync;
        options.MaxMessagePayloadSize = 7_500;
        options.SubscriptionOptions.TopicPrefix = "messaging";
    });

builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<PresenceExpiryWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<InternalApiAuthenticationMiddleware>();
app.UseMiddleware<GatewayTrustMiddleware>();

app.MapControllers();
app.MapGraphQL("/graphql").WithOptions(options =>
{
    options.Tool.Enable = app.Environment.IsDevelopment();
});

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "Messaging" }));
app.MapGet("/health/ready", async (MessagingDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready", service = "Messaging" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.RunWithGraphQLCommands(args);

public partial class Program;
