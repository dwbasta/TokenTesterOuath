namespace OUathMCPServer.Models;

public sealed record OboForwardResult(
    int StatusCode,
    string ContentType,
    string Body);