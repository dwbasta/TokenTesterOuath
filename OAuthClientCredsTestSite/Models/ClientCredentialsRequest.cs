using System.ComponentModel.DataAnnotations;

namespace OAuthClientCredsTestSite.Models;

public sealed class ClientCredentialsRequest
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Scope (.default)")]
    public string Scope { get; set; } = string.Empty;

    [Display(Name = "API URL (optional)")]
    public string? ApiUrl { get; set; }
}