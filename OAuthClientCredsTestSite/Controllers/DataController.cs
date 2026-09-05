using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace OAuthClientCredsTestSite.Controllers;

[ApiController]
[Route("api/data")]
public sealed class DataController : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "ApiRead")]
    public IActionResult Get()
    {
        return Ok(new { message = "Read allowed." });
    }

    [HttpPost]
    [Authorize(Policy = "ApiWrite")]
    public IActionResult Post()
    {
        return Ok(new { message = "Write allowed." });
    }
}