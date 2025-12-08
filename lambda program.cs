using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
public class Function
{
    private static readonly HttpClient http = new HttpClient();
    private static Secrets? secretsCache;

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest req, ILambdaContext ctx)
    {
        try
        {
            var secrets = await GetSecrets();
            var accessToken = await RefreshAccessToken(secrets);

            if (req.Path.EndsWith("/nowplaying", StringComparison.OrdinalIgnoreCase))
            {
                var data = await SpotifyGet(accessToken, "https://api.spotify.com/v1/me/player/currently-playing");
                // If nothing is playing, Spotify returns 204
                if (data == null) return Json(200, new { ok = true, data = new { item = (object?)null } });
                return Json(200, data);
            }
            if (req.Path.EndsWith("/recent", StringComparison.OrdinalIgnoreCase))
            {
                var data = await SpotifyGet(accessToken, "https://api.spotify.com/v1/me/player/recently-played?limit=8");
                return Json(200, data ?? new { items = Array.Empty<object>() });
            }
            return Json(404, new { ok = false, error = "not_found" });
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex.ToString());
            return Json(500, new { ok = false, error = "server_error" });
        }
    }

    private async Task<object?> SpotifyGet(string token, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await http.SendAsync(req);
        if (res.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        var txt = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<object>(txt);
    }

    private async Task<string> RefreshAccessToken(Secrets s)
    {
        var body = new Dictionary<string, string>{
            {"grant_type", "refresh_token"},
            {"refresh_token", s.refresh_token},
            {"client_id", s.client_id},
            {"client_secret", s.client_secret}
        };
        var res = await http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(body));
        res.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        return json.GetProperty("access_token").GetString()!;
    }

    private async Task<Secrets> GetSecrets()
    {
        if (secretsCache != null) return secretsCache;
        var name = Environment.GetEnvironmentVariable("SPOTIFY_SECRET_ID") ?? "spotify/portfolio";
        using var sm = new AmazonSecretsManagerClient();
        var resp = await sm.GetSecretValueAsync(new GetSecretValueRequest { SecretId = name });
        var s = JsonSerializer.Deserialize<Secrets>(resp.SecretString!)!;
        secretsCache = s;
        return s;
    }

    private APIGatewayProxyResponse Json(int status, object obj) =>
        new APIGatewayProxyResponse
        {
            StatusCode = status,
            Headers = new Dictionary<string, string>{
                {"Content-Type","application/json"},
                {"Access-Control-Allow-Origin","*"}
            },
            Body = JsonSerializer.Serialize(obj)
        };

    private record Secrets(string client_id, string client_secret, string refresh_token);
}
