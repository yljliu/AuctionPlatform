// Reverse proxy on port 5000. Sends each browser to the same web copy every time
// (cookie ".Auction.Affinity"), because a Blazor Server circuit lives on one web copy.

if (args is ["--probe", var url])
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await http.GetAsync(url);
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("yarp.json", optional: false, reloadOnChange: true);
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();
app.MapGet("/health", () => Results.Ok("ok"));
app.MapReverseProxy();
app.Run();
return 0;
