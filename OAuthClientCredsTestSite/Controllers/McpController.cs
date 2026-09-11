using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OAuthClientCredsTestSite.Models;
using OAuthClientCredsTestSite.Services;

namespace OAuthClientCredsTestSite.Controllers;

[ApiController]
[Route("api/mcp")]
public sealed class McpController(
    OAuthOnBehalfOfClient oboClient,
    ILogger<McpController> logger) : ControllerBase
{
    [HttpPost("obo/call")]
    [Authorize(Policy = "AgentObo")]
    public async Task<IActionResult> CallOnBehalfOfAsync(
        [FromBody] McpOboRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest(new { error = "Request path is required." });
        }

        if (!Request.Headers.TryGetValue("Authorization", out var authHeaderValue) ||
            !AuthenticationHeaderValue.TryParse(authHeaderValue.ToString(), out var authHeader) ||
            !string.Equals(authHeader.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authHeader.Parameter))
        {
            return Unauthorized(new { error = "Missing bearer token." });
        }

        try
        {
            var response = await oboClient.ForwardAsync(authHeader.Parameter, request, cancellationToken);
            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                ContentType = response.ContentType,
                Content = response.Body
            };
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid OBO request/configuration.");
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Downstream OBO call failed.");
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}