using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace HomePortal;

/// <summary>
/// An address opened from the Google box with Ctrl+Enter. Key is the address as it is shown
/// on the page and matched against what is typed.
/// </summary>
public sealed class Jump
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Url { get; set; } = "";
    public int Uses { get; set; }
    public DateTimeOffset LastUsed { get; set; }
    public bool HasIcon { get; set; }
}

/// <summary>Body for recording one open.</summary>
public sealed class JumpInput
{
    public string? Url { get; set; }
}

/// <summary>
/// The jumps table. Only Ctrl+Enter creates rows, but every later way of opening one goes
/// through the same upsert, so the count covers all of them.
/// </summary>
public sealed class JumpStore : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;

    public JumpStore(string connectionString) => _source = NpgsqlDataSource.Create(connectionString);

    public ValueTask DisposeAsync() => _source.DisposeAsync();

    /// <summary>Creates the table on the first run, a no-op on every start after that.</summary>
    public async Task InitializeAsync()
    {
        await using var cmd = _source.CreateCommand("""
            CREATE TABLE IF NOT EXISTS jumps (
                id        text PRIMARY KEY,
                key       text NOT NULL UNIQUE,
                url       text NOT NULL,
                uses      integer NOT NULL DEFAULT 0,
                last_used timestamptz NOT NULL DEFAULT now(),
                icon      bytea,
                icon_type text
            );
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Most-opened first, ties going to the one opened more recently.</summary>
    public async Task<List<Jump>> ListAsync()
    {
        await using var cmd = _source.CreateCommand("""
            SELECT id, key, url, uses, last_used, icon IS NOT NULL FROM jumps
            ORDER BY uses DESC, last_used DESC
            """);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<Jump>();
        while (await reader.ReadAsync()) list.Add(Read(reader));
        return list;
    }

    /// <summary>Counts one open, creating the row the first time the key is seen.</summary>
    /// <returns>The row after counting. An existing row keeps the address it was created with.</returns>
    public async Task<Jump> RecordAsync(string key, string url)
    {
        await using var cmd = _source.CreateCommand("""
            INSERT INTO jumps (id, key, url, uses, last_used) VALUES (@id, @key, @url, 1, now())
            ON CONFLICT (key) DO UPDATE SET uses = jumps.uses + 1, last_used = now()
            RETURNING id, key, url, uses, last_used, icon IS NOT NULL
            """);
        cmd.Parameters.AddWithValue("id", "j_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("url", url);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return Read(reader);
    }

    public async Task SetIconAsync(string id, byte[] data, string type)
    {
        await using var cmd = _source.CreateCommand("UPDATE jumps SET icon = @data, icon_type = @type WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("data", data);
        cmd.Parameters.AddWithValue("type", type);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <returns>The stored icon, or null when the row is gone or has no icon yet.</returns>
    public async Task<(byte[] Data, string Type)?> GetIconAsync(string id)
    {
        await using var cmd = _source.CreateCommand(
            "SELECT icon, icon_type FROM jumps WHERE id = @id AND icon IS NOT NULL");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetFieldValue<byte[]>(0), reader.GetString(1));
    }

    /// <returns>False when there was no such row.</returns>
    public async Task<bool> DeleteAsync(string id)
    {
        await using var cmd = _source.CreateCommand("DELETE FROM jumps WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    /// <summary>
    /// The identity of an address. Typing github and typing github.com land on
    /// https://www.github.com and https://github.com, which are the same site to the person
    /// typing, so the scheme, a leading www. and a trailing slash are left out.
    /// </summary>
    public static string KeyOf(Uri url)
    {
        var host = url.Host.StartsWith("www.") ? url.Host[4..] : url.Host;
        var port = url.IsDefaultPort ? "" : ":" + url.Port;
        return host + port + url.PathAndQuery.TrimEnd('/');
    }

    private static Jump Read(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Key = reader.GetString(1),
        Url = reader.GetString(2),
        Uses = reader.GetInt32(3),
        LastUsed = reader.GetFieldValue<DateTimeOffset>(4),
        HasIcon = reader.GetBoolean(5)
    };
}

/// <summary>
/// Fetches a site's icon from the server, so the page can draw it from the portal itself
/// without the browser having to reach the site.
/// </summary>
public static class Favicon
{
    /// <summary>For the whole lookup, page and icons together, not per request.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    private const int MaxIcon = 512 * 1024;

    /// <summary>Icon links sit in head; a megabyte of page is far past where head ends.</summary>
    private const int MaxPage = 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static readonly Regex LinkTag = new(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Attr = new(
        @"\b(rel|href)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            // The Budget token bounds every request; a second, per-request clock would only muddle that
            Timeout = Timeout.InfiniteTimeSpan
        };
        // Some sites refuse a request that carries no User-Agent at all
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MyPortal)");
        return client;
    }

    /// <summary>
    /// Tries the icons the page declares with rel="icon", then apple-touch-icon, then
    /// /favicon.ico, and keeps the first one that really is an image.
    /// </summary>
    /// <returns>The icon, or null when the site cannot be reached or offers no image in time.</returns>
    public static async Task<(byte[] Data, string Type)?> FetchAsync(Uri site)
    {
        using var budget = new CancellationTokenSource(Budget);
        try
        {
            foreach (var candidate in await CandidatesAsync(site, budget.Token))
            {
                var icon = await DownloadAsync(candidate, budget.Token);
                if (icon is not null) return icon;
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<List<Uri>> CandidatesAsync(Uri site, CancellationToken ct)
    {
        var icons = new List<Uri>();
        var touch = new List<Uri>();
        var landed = site;
        try
        {
            using var res = await Http.GetAsync(site, HttpCompletionOption.ResponseHeadersRead, ct);
            // Redirects are followed, so relative hrefs resolve against where we ended up
            landed = res.RequestMessage?.RequestUri ?? site;
            if (res.IsSuccessStatusCode && res.Content.Headers.ContentType?.MediaType == "text/html")
            {
                var html = Encoding.UTF8.GetString(await ReadAsync(res, MaxPage, ct));
                foreach (Match tag in LinkTag.Matches(html))
                {
                    string? rel = null, href = null;
                    foreach (Match a in Attr.Matches(tag.Value))
                    {
                        var value = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value
                            : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
                        if (a.Groups[1].Value.Equals("rel", StringComparison.OrdinalIgnoreCase)) rel = value;
                        else href = value;
                    }
                    if (rel is null || href is null || !Uri.TryCreate(landed, href, out var url)) continue;
                    if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) continue;

                    var tokens = rel.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Contains("icon")) icons.Add(url);
                    else if (tokens.Contains("apple-touch-icon")) touch.Add(url);
                }
            }
        }
        catch (HttpRequestException)
        {
            // The page itself failed; /favicon.ico below is still worth one try
        }

        icons.AddRange(touch);
        icons.Add(new Uri(landed, "/favicon.ico"));
        return icons.Distinct().ToList();
    }

    private static async Task<(byte[] Data, string Type)?> DownloadAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return null;
            var data = await ReadAsync(res, MaxIcon + 1, ct);
            if (data.Length == 0 || data.Length > MaxIcon) return null;
            var type = ImageType(data, res.Content.Headers.ContentType?.MediaType);
            return type is null ? null : (data, type);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Trusts a declared image type. Otherwise looks at the bytes: plenty of servers send
    /// favicon.ico as application/octet-stream, and a single-page app answers every path,
    /// /favicon.ico included, with its HTML.
    /// </summary>
    private static string? ImageType(byte[] data, string? declared)
    {
        if (declared is not null && declared.StartsWith("image/")) return declared;
        if (data.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0x01, 0x00 })) return "image/x-icon";
        if (data.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })) return "image/png";
        if (data.AsSpan().StartsWith("GIF8"u8)) return "image/gif";
        if (data.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        return null;
    }

    /// <summary>Reads at most limit bytes, so a huge or endless body cannot eat the server's memory.</summary>
    private static async Task<byte[]> ReadAsync(HttpResponseMessage res, int limit, CancellationToken ct)
    {
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[limit];
        var total = 0;
        int read;
        while (total < limit && (read = await stream.ReadAsync(buffer.AsMemory(total, limit - total), ct)) > 0)
            total += read;
        return buffer[..total];
    }
}

public static class JumpApi
{
    /// <summary>Maps the /api/jumps endpoints used by the Google box and the icon row.</summary>
    public static void MapJumps(this WebApplication app, JumpStore store)
    {
        app.MapGet("/api/jumps", async () => Results.Ok(await store.ListAsync()));

        // Counts one open. A row without an icon gets another fetch attempt each time, so a
        // site that was unreachable once is not left iconless for good.
        app.MapPost("/api/jumps", async (JumpInput input) =>
        {
            var url = (input.Url ?? "").Trim();
            var error = Validator.UrlError(url);
            if (error is not null) return Results.Json(new { error }, statusCode: 400);

            var jump = await store.RecordAsync(JumpStore.KeyOf(new Uri(url)), url);
            if (!jump.HasIcon)
            {
                var icon = await Favicon.FetchAsync(new Uri(jump.Url));
                if (icon is null)
                {
                    app.Logger.LogInformation("No icon found for {Key}", jump.Key);
                }
                else
                {
                    await store.SetIconAsync(jump.Id, icon.Value.Data, icon.Value.Type);
                    jump.HasIcon = true;
                }
            }
            return Results.Ok(jump);
        });

        app.MapGet("/api/jumps/{id}/icon", async (string id, HttpResponse response) =>
        {
            var icon = await store.GetIconAsync(id);
            if (icon is null) return Results.NotFound();

            // An icon is written once and never replaced, and a deleted row takes its id with it
            response.Headers.CacheControl = "public, max-age=31536000, immutable";
            // WARNING: these bytes come from arbitrary sites. An SVG opened directly would run its
            // scripts on the portal's origin; the sandbox keeps it an inert picture.
            response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(icon.Value.Data, icon.Value.Type);
        });

        app.MapDelete("/api/jumps/{id}", async (string id) =>
            await store.DeleteAsync(id)
                ? Results.NoContent()
                : Results.Json(new { error = "That shortcut is gone — reload the page" }, statusCode: 404));
    }
}
