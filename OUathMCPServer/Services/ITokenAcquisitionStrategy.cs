namespace OUathMCPServer.Services;

public interface ITokenAcquisitionStrategy
{
    Task<string> AcquireAsync(string incomingToken, CancellationToken cancellationToken);
}