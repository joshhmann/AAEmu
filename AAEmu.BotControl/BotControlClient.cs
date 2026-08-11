namespace AAEmu.BotControl;

/// <summary>
/// Thin HTTP client over the game's bot control API (P1 t_2ea94a20). Every
/// call carries the shared secret in the X-Auth-Token header — the same
/// token the game's WebApi validates. The MCP server never touches bot code
/// directly: all mutations execute inside the game process (single
/// execution boundary).
/// </summary>
public interface IBotControlClient
{
    Task<(int Status, string Body)> GetAsync(string path);
    Task<(int Status, string Body)> PostAsync(string path, string jsonBody);
}

public sealed class BotControlClient : IBotControlClient
{
    private readonly HttpClient _http;

    public BotControlClient(string baseUrl, string token)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Add("X-Auth-Token", token);
    }

    public async Task<(int Status, string Body)> GetAsync(string path)
        => await SendAsync(HttpMethod.Get, path, null);

    public async Task<(int Status, string Body)> PostAsync(string path, string jsonBody)
        => await SendAsync(HttpMethod.Post, path, jsonBody);

    private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string path, string? body)
    {
        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (body is not null)
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
