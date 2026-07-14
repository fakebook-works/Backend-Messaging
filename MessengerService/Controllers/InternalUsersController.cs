using MessengerService.Contracts.Internal;
using Microsoft.AspNetCore.Mvc;

namespace MessengerService.Controllers;

[ApiController]
[Route("internal/users")]
public sealed class InternalUsersController(
    IMessagingUserProvisioningService users) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<InternalUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<InternalUserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateInternalUserRequest request,
        CancellationToken cancellationToken)
    {
        if (request.UserId <= 0)
        {
            return InvalidUserId();
        }

        var outcome = await users.ProvisionAsync(request.UserId, cancellationToken);
        var response = new InternalUserResponse(request.UserId);

        return outcome switch
        {
            ProvisionUserOutcome.Created => StatusCode(StatusCodes.Status201Created, response),
            ProvisionUserOutcome.AlreadyActive => Ok(response),
            ProvisionUserOutcome.DeletedTombstone => DeletedUserConflict(request.UserId),
            _ => throw new InvalidOperationException($"Unexpected provision outcome: {outcome}.")
        };
    }

    [HttpDelete("{userId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Delete(
        long userId,
        CancellationToken cancellationToken)
    {
        if (userId <= 0)
        {
            return InvalidUserId();
        }

        await users.TombstoneAsync(userId, cancellationToken);
        return NoContent();
    }

    private ObjectResult InvalidUserId()
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "The user ID must be a positive Snowflake value.",
            Instance = HttpContext.Request.Path
        };
        problem.Extensions["code"] = "INVALID_USER_ID";
        problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return BadRequest(problem);
    }

    private ObjectResult DeletedUserConflict(long userId)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "A deleted Messaging user cannot be reactivated.",
            Instance = HttpContext.Request.Path
        };
        problem.Extensions["code"] = "USER_DELETED";
        problem.Extensions["userId"] = userId;
        problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return Conflict(problem);
    }
}
