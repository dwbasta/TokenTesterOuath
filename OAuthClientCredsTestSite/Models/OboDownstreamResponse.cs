using System.Net;

namespace OAuthClientCredsTestSite.Models;

public sealed record OboDownstreamResponse(
    HttpStatusCode StatusCode,
    string ContentType,
    string Body);