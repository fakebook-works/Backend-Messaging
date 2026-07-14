using HotChocolate;
using HotChocolate.Execution;
using MessengerService.Application;

namespace MessengerService.GraphQL;

public sealed class MessagingErrorFilter : IErrorFilter
{
    public IError OnError(IError error)
    {
        if (error.Exception is not MessagingApplicationException exception)
        {
            return error;
        }

        var builder = ErrorBuilder.New()
            .SetMessage(exception.Message)
            .SetCode(exception.Code);
        if (error.Path is not null)
        {
            builder.SetPath(error.Path);
        }

        return builder.Build();
    }
}
