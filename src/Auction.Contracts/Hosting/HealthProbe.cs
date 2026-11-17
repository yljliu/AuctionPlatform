namespace Auction.Contracts.Hosting;

/// <summary>
/// Lets a container check itself without curl:
///   dotnet Auction.Api.dll --probe http://localhost:7080/health/ready
/// Exit code 0 = healthy, 1 = not healthy.
/// </summary>
public static class HealthProbe
{
    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "--probe")
        {
            return null;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await http.GetAsync(args[1]);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
