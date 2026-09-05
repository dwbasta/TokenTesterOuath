using Microsoft.AspNetCore.Mvc;
using OAuthClientCredsTestSite.Models;
using OAuthClientCredsTestSite.Services;

namespace OAuthClientCredsTestSite.Controllers;

public sealed class HomeController : Controller
{
    private readonly ClientCredentialsTokenService _tokenService;
    private readonly DownstreamApiService _apiService;

    public HomeController(ClientCredentialsTokenService tokenService, DownstreamApiService apiService)
    {
        _tokenService = tokenService;
        _apiService = apiService;
    }

    [HttpGet]
    public IActionResult Index()
    {
        var model = new TestPageViewModel
        {
            Request = new ClientCredentialsRequest
            {
                Scope = "api://<resource-app-id>/.default"
            }
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(TestPageViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            model.HasResult = true;
            model.IsSuccess = false;
            model.Error = "Input validation failed.";
            return View(model);
        }

        var (token, error) = await _tokenService.RequestTokenAsync(model.Request, cancellationToken);

        model.HasResult = true;

        if (!string.IsNullOrWhiteSpace(error) || token is null)
        {
            model.IsSuccess = false;
            model.Error = error ?? "Unknown token acquisition failure.";
            return View(model);
        }

        model.IsSuccess = true;
        model.Token = token;

        if (!string.IsNullOrWhiteSpace(model.Request.ApiUrl))
        {
            var (statusCode, body) = await _apiService.CallApiAsync(model.Request.ApiUrl, token, cancellationToken);
            model.ApiStatusCode = statusCode;
            model.ApiResponseBody = body;
        }

        return View(model);
    }
}