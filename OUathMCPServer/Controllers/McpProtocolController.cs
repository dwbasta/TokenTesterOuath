using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OUathMCPServer.Models;
using OUathMCPServer.Services;

namespace OUathMCPServer.Controllers;

// Deprecated: MCP protocol routes are implemented in McpDiscoveryController.
// Keep this file route-free to avoid endpoint ambiguity.
internal static class McpProtocolController
{
}