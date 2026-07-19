using MessengerService.Application;
using MessengerService.Contracts.Internal;
using Microsoft.AspNetCore.Mvc;

namespace MessengerService.Controllers;

[ApiController]
[Route("internal/users")]
public sealed class InternalDirectContactsController(
    DirectContactQueryService contacts) : ControllerBase
{
    [HttpGet("{userId:long}/direct-contact-ids")]
    [ProducesResponseType<DirectContactIdsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DirectContactIdsResponse>> GetDirectContactIds(
        long userId,
        CancellationToken cancellationToken)
    {
        if (userId <= 0)
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

        var userIds = await contacts.GetContactIdsAsync(userId, cancellationToken);
        return Ok(new DirectContactIdsResponse(userIds));
    }
}
