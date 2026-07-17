namespace MessengerService.Infrastructure.Security;

public static class MessagingHeaders
{
    public const string GatewaySecret = "X-Gateway-Secret";
    public const string UserId = "X-User-Id";
    public const string InternalServiceSecret = "X-Internal-MessengerService-Secret";
    public const string SocialGraphServiceSecret = "X-Internal-SocialGraphService-Secret";
    public const string UploadServiceSecret = "X-Internal-UploadService-Secret";
    public const string CorrelationId = "X-Correlation-ID";
}
