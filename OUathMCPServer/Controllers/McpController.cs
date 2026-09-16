using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OUathMCPServer.Models;
using OUathMCPServer.Services;

namespace OUathMCPServer.Controllers;

[ApiController]
[Route("api/mcp")]
public sealed class McpController(
    OboForwardingService oboForwardingService,
    ILogger<McpController> logger) : ControllerBase
{
    [HttpPost("obo/call")]
    [Authorize(Policy = "McpInvoke")]
    public async Task<IActionResult> CallAsync([FromBody] McpOboCallRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest(new { error = "Path is required." });
        }

        if (!Request.Headers.TryGetValue("Authorization", out var authorizationHeader) ||
            !AuthenticationHeaderValue.TryParse(authorizationHeader.ToString(), out var headerValue) ||
            !string.Equals(headerValue.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(headerValue.Parameter))
        {
            return Unauthorized(new { error = "Missing bearer token." });
        }

        try
        {
            var result = await oboForwardingService.ForwardAsync(headerValue.Parameter, request, cancellationToken);
            return new ContentResult
            {
                StatusCode = result.StatusCode,
                ContentType = result.ContentType,
                Content = result.Body
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Token correlation or audience validation failed.");
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Invalid MCP/OBO request.");
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Failed to call downstream API via OBO.");
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}