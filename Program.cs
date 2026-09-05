using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace HomePortal;

/// <summary>One link in the config. A null Group means ungrouped.</summary>
public sealed class Site
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Group { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>The shape of the export file, and what import accepts.</summary>
public sealed class PortalData
{
    public List<Site> Sites { get; set; } = [];
    public List<string> Groups { get; set; } = [];
}

/// <summary>Body for adding a link. Id / AddedAt / Clicks are present when this is an undo of a delete.</summary>
public sealed class SiteInput
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Url { get; set; }
    public string? Group { get; set; }
    public DateTimeOffset? AddedAt { get; set; }
    public int? Clicks { get; set; }
}

public sealed class GroupInput
{
    public string? Name { get; set; }
}

public sealed class ReorderInput
{
    public List<string>? Groups { get; set; }
}

public sealed class ImportInput
{
    public List<SiteInput>? Sites { get; set; }
    public List<string>? Groups { get; set; }
}

/// <summary>
/// PostgreSQL data layer.
/// Group order lives in groups.sort_order; click counts are just the sites.clicks column.
/// The database is backed up as a whole, so there is no reason to keep counts apart the
/// way the file-based version had to.
/// </summary>
public sealed class Db : IAsyncDisposable
{
    /// <summary>Length caps for names, purely so one long string cannot blow out a card or a group header.</summary>
    public const int MaxName = 64;
    public const int MaxUrl = 2048;

    private readonly NpgsqlDataSource _source;

    public Db(string connectionString) => _source = NpgsqlDataSource.Create(connectionString);

    public ValueTask DisposeAsync() => _source.DisposeAsync();

    /// <summary>Creates the tables. Does the work on the first run against an empty database, a no-op on every start after that.</summary>
    public async Task InitializeAsync()
    {
        await using var cmd = _source.CreateCommand("""
            CREATE TABLE IF NOT EXISTS groups (
                name       text PRIMARY KEY,
                sort_order integer NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sites (
                id         text PRIMARY KEY,
                name       text NOT NULL,
                url        text NOT NULL,
                -- CASCADE carries members along on a rename; dropping a group leaves its links ungrouped
                group_name text REFERENCES groups(name) ON UPDATE CASCADE ON DELETE SET NULL,
                added_at   timestamptz NOT NULL DEFAULT now(),
                clicks     integer NOT NULL DEFAULT 0
            );
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<(List<Site> Sites, List<string> Groups, Dictionary<string, int> Clicks)> GetStateAsync()
    {
        var sites = new List<Site>();
        var clicks = new Dictionary<string, int>();
        await using (var cmd = _source.CreateCommand(
            "SELECT id, name, url, group_name, added_at, clicks FROM sites ORDER BY added_at"))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                sites.Add(new Site
                {
                    Id = id,
                    Name = reader.GetString(1),
                    Url = reader.GetString(2),
                    Group = reader.IsDBNull(3) ? null : reader.GetString(3),
                    AddedAt = reader.GetFieldValue<DateTimeOffset>(4)
                });
                clicks[id] = reader.GetInt32(5);
            }
        }
        return (sites, await GetGroupsAsync(), clicks);
    }

    private async Task<List<string>> GetGroupsAsync()
    {
        await using var cmd = _source.CreateCommand("SELECT name FROM groups ORDER BY sort_order");
        return await ReadGroupsAsync(cmd);
    }

    private static async Task<List<string>> ReadGroupsAsync(NpgsqlCommand cmd)
    {
        var groups = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) groups.Add(reader.GetString(0));
        return groups;
    }

    /// <summary>Reorders groups into the sequence the front end sent. Anything left out keeps its relative order at the end.</summary>
    /// <returns>The full group list after reordering.</returns>
    public async Task<List<string>> ReorderGroupsAsync(List<string> wanted)
    {
        await using var conn = await _source.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        List<string> current;
        await using (var read = new NpgsqlCommand("SELECT name FROM groups ORDER BY sort_order", conn, tx))
            current = await ReadGroupsAsync(read);

        var final = wanted.Where(current.Contains).Distinct().ToList();
        final.AddRange(current.Where(g => !final.Contains(g)));

        for (var i = 0; i < final.Count; i++)
        {
            await using var write = new NpgsqlCommand("UPDATE groups SET sort_order = @o WHERE name = @n", conn, tx);
            write.Parameters.AddWithValue("o", i);
            write.Parameters.AddWithValue("n", final[i]);
            await write.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return final;
    }

    /// <summary>Inserts a link. With id / addedAt / clicks supplied this is an undo, restoring the row as it was.</summary>
    /// <returns>The inserted row, or null when the id already exists.</returns>
    public async Task<Site?> AddSiteAsync(string id, Validated fields, DateTimeOffset addedAt, int clicks)
    {
        await using var conn = await _source.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        if (fields.Group is not null) await EnsureGroupAsync(conn, tx, fields.Group);

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO sites (id, name, url, group_name, added_at, clicks)
            VALUES (@id, @name, @url, @group, @added, @clicks)
            ON CONFLICT (id) DO NOTHING
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", fields.Name);
        cmd.Parameters.AddWithValue("url", fields.Url);
        cmd.Parameters.AddWithValue("group", (object?)fields.Group ?? DBNull.Value);
        cmd.Parameters.AddWithValue("added", addedAt);
        cmd.Parameters.AddWithValue("clicks", clicks);

        if (await cmd.ExecuteNonQueryAsync() == 0) return null;

        await PruneGroupsAsync(conn, tx);
        await tx.CommitAsync();
        return new Site { Id = id, Name = fields.Name, Url = fields.Url, Group = fields.Group, AddedAt = addedAt };
    }

    public async Task<Site?> UpdateSiteAsync(string id, Validated fields)
    {
        await using var conn = await _source.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        if (fields.Group is not null) await EnsureGroupAsync(conn, tx, fields.Group);

        await using var cmd = new NpgsqlCommand("""
            UPDATE sites SET name = @name, url = @url, group_name = @group
            WHERE id = @id
            RETURNING added_at
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", fields.Name);
        cmd.Parameters.AddWithValue("url", fields.Url);
        cmd.Parameters.AddWithValue("group", (object?)fields.Group ?? DBNull.Value);

        var raw = await cmd.ExecuteScalarAsync();
        if (raw is null) return null;

        await PruneGroupsAsync(conn, tx);
        await tx.CommitAsync();
        return new Site
        {
            Id = id,
            Name = fields.Name,
            Url = fields.Url,
            Group = fields.Group,
            // WARNING: Npgsql reads timestamptz as a DateTime with Kind=Utc. Unboxing demands an
            // exact type match, so casting the boxed value straight to DateTimeOffset throws.
            AddedAt = new DateTimeOffset((DateTime)raw)
        };
    }

    /// <returns>The deleted row's click count so undo can restore it, or null when there was no such row.</returns>
    public async Task<int?> DeleteSiteAsync(string id)
    {
        await using var conn = await _source.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using var cmd = new NpgsqlCommand("DELETE FROM sites WHERE id = @id RETURNING clicks", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        var clicks = await cmd.ExecuteScalarAsync();
        if (clicks is null) return null;

        await PruneGroupsAsync(conn, tx);
        await tx.CommitAsync();
        return (int)clicks;
    }

    public async Task<int?> ClickAsync(string id)
    {
        await using var cmd = _source.CreateCommand(
            "UPDATE sites SET clicks = clicks + 1 WHERE id = @id RETURNING clicks");
        cmd.Parameters.AddWithValue("id", id);
        return (int?)await cmd.ExecuteScalarAsync();
    }

    /// <summary>Renames a whole group. Renaming onto a name that already exists merges the two.</summary>
    /// <returns>The group list afterwards, or null when the original name is gone.</returns>
    public async Task<List<string>?> RenameGroupAsync(string from, string to)
    {
        await using var conn = await _source.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var exists = new NpgsqlCommand("SELECT 1 FROM groups WHERE name = @n", conn, tx))
        {
            exists.Parameters.AddWithValue("n", from);
            if (await exists.ExecuteScalarAsync() is null) return null;
        }

        await using (var target = new NpgsqlCommand("SELECT 1 FROM groups WHERE name = @n", conn, tx))
        {
            target.Parameters.AddWithValue("n", to);
            var merging = await target.ExecuteScalarAsync() is not null;

            if (merging)
            {
                // The target already exists, so the foreign key's CASCADE would collide with the
                // primary key: move the members by hand, then drop the empty shell
                await using var move = new NpgsqlCommand(
                    "UPDATE sites SET group_name = @to WHERE group_name = @from", conn, tx);
                move.Parameters.AddWithValue("to", to);
                move.Parameters.AddWithValue("from", from);
                await move.ExecuteNonQueryAsync();

                await using var drop = new NpgsqlCommand("DELETE FROM groups WHERE name = @n", conn, tx);
                drop.Parameters.AddWithValue("n", from);
                await drop.ExecuteNonQueryAsync();
            }
            else
            {
                // A name nobody is using yet, so ON UPDATE CASCADE carries the members' group_name along
                await using var rename = new NpgsqlCommand(
                    "UPDATE groups SET name = @to WHERE name = @from", conn, tx);
                rename.Parameters.AddWithValue("to", to);
                rename.Parameters.AddWithValue("from", from);
                await rename.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
        return await GetGroupsAsync();
    }

    public async Task<PortalData> ExportAsync()
    {
        var (sites, groups, _) = await GetStateAsync();
        return new PortalData { Sites = sites, Groups = groups };
    }

    /// <summary>Replaces everything. Click counts are not in the export file, so they start from zero after an import.</summary>
    public async Task ImportAsync(List<(string Id, Validated Fields, DateTimeOffset AddedAt)> rows, List<string> groups)
    {
        await using var conn = await _source.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var wipe = new NpgsqlCommand("DELETE FROM sites; DELETE FROM groups;", conn, tx))
            await wipe.ExecuteNonQueryAsync();

        var order = 0;
        foreach (var name in groups.Concat(rows.Select(r => r.Fields.Group)).Where(g => g is not null).Distinct())
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO groups (name, sort_order) VALUES (@n, @o) ON CONFLICT (name) DO NOTHING", conn, tx);
            insert.Parameters.AddWithValue("n", name!);
            insert.Parameters.AddWithValue("o", order++);
            await insert.ExecuteNonQueryAsync();
        }

        foreach (var (id, fields, addedAt) in rows)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO sites (id, name, url, group_name, added_at, clicks)
                VALUES (@id, @name, @url, @group, @added, 0)
                """, conn, tx);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("name", fields.Name);
            insert.Parameters.AddWithValue("url", fields.Url);
            insert.Parameters.AddWithValue("group", (object?)fields.Group ?? DBNull.Value);
            insert.Parameters.AddWithValue("added", addedAt);
            await insert.ExecuteNonQueryAsync();
        }

        await PruneGroupsAsync(conn, tx);
        await tx.CommitAsync();
    }

    private static async Task EnsureGroupAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string name)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO groups (name, sort_order)
            VALUES (@n, COALESCE((SELECT MAX(sort_order) + 1 FROM groups), 0))
            ON CONFLICT (name) DO NOTHING
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", name);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Drops groups with no members left; an empty group should not hold a slot in the UI.</summary>
    private static async Task PruneGroupsAsync(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM groups g
            WHERE NOT EXISTS (SELECT 1 FROM sites s WHERE s.group_name = g.name)
            """, conn, tx);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>Either the validated fields, or one sentence that can be shown to the user as it is.</summary>
public readonly record struct Validated(string Name, string Url, string? Group, string? Error)
{
    public bool Ok => Error is null;
}

public static class Validator
{
    /// <summary>
    /// Validates and normalises one link.
    /// Only http/https gets through: these fields end up as an &lt;a href&gt;, so accepting
    /// javascript: would be handing yourself an XSS hole.
    /// </summary>
    public static Validated Site(SiteInput input)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0) return Fail("Name cannot be empty");
        if (name.Length > Db.MaxName) return Fail($"Name is at most {Db.MaxName} characters");

        var url = (input.Url ?? "").Trim();
        if (url.Length == 0) return Fail("Address cannot be empty");
        if (url.Length > Db.MaxUrl) return Fail("Address is too long");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return Fail("Write the full address, like http://192.168.1.10:5000");
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return Fail("Only http and https addresses are supported");

        var group = (input.Group ?? "").Trim();
        if (group.Length > Db.MaxName) return Fail($"Group name is at most {Db.MaxName} characters");

        return new Validated(name, url, group.Length == 0 ? null : group, null);
    }

    private static Validated Fail(string message) => new("", "", null, message);
}

public static class Program
{
    public static async Task Main(string[] args)
    {
        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        var connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "CONNECTION_STRING is not set. Provide one, for example "
                + "Server=host;Port=5432;Database=web_portal;Username=postgres;Password=secret");

        await using var db = new Db(connectionString);
        await db.InitializeAsync();

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        // Tells systemd when the service is actually ready (Type=notify) and maps log levels
        // onto journald; a no-op when systemd is not around
        builder.Host.UseSystemd();
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        var app = builder.Build();

        // An unhandled exception otherwise returns an empty body, and all the front end can
        // report is that JSON failed to parse. Answer with something readable instead; the
        // stack trace stays in the journal.
        app.UseExceptionHandler(branch => branch.Run(async context =>
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "Server error — check journalctl -u home-portal" });
        }));

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            // Without this a browser keeps serving a stale style.css after a deploy, which
            // shows up as the new markup wearing the old styling. no-cache still caches;
            // it only forces revalidation, so an unchanged file costs a 304.
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
        });

        MapApi(app, db);

        app.Logger.LogInformation("Home Portal on http://localhost:{Port}", port);
        await app.RunAsync();
    }

    private static void MapApi(WebApplication app, Db db)
    {
        // Links, groups and counts in one round trip
        app.MapGet("/api/state", async () =>
        {
            var (sites, groups, clicks) = await db.GetStateAsync();
            return Results.Ok(new { sites, groups, clicks });
        });

        // Add. With id / addedAt / clicks this is an undo, so the original timestamp and count go back
        app.MapPost("/api/sites", async (SiteInput input) =>
        {
            var v = Validator.Site(input);
            if (!v.Ok) return Error(400, v.Error!);

            var id = string.IsNullOrWhiteSpace(input.Id) ? NewId() : input.Id!;
            var site = await db.AddSiteAsync(id, v, input.AddedAt ?? DateTimeOffset.UtcNow, input.Clicks ?? 0);
            return site is null ? Error(409, "That link already exists") : Results.Json(site, statusCode: 201);
        });

        app.MapPut("/api/sites/{id}", async (string id, SiteInput input) =>
        {
            var v = Validator.Site(input);
            if (!v.Ok) return Error(400, v.Error!);

            var site = await db.UpdateSiteAsync(id, v);
            return site is null ? Gone() : Results.Ok(site);
        });

        app.MapDelete("/api/sites/{id}", async (string id) =>
        {
            var clicks = await db.DeleteSiteAsync(id);
            return clicks is null ? Gone() : Results.Ok(new { clicks });
        });

        app.MapPost("/api/sites/{id}/click", async (string id) =>
        {
            var clicks = await db.ClickAsync(id);
            return clicks is null ? Gone() : Results.Ok(new { clicks });
        });

        // Drag-to-reorder. Deliberately not /api/groups/order: a group actually named "order"
        // would hijack the rename route
        app.MapPut("/api/groups", async (ReorderInput input) =>
        {
            if (input.Groups is null) return Error(400, "No groups in the request");
            return Results.Ok(new { groups = await db.ReorderGroupsAsync(input.Groups) });
        });

        // Rename a whole group; its links come along
        app.MapPut("/api/groups/{name}", async (string name, GroupInput input) =>
        {
            var to = (input.Name ?? "").Trim();
            if (to.Length == 0) return Error(400, "Group name cannot be empty");
            if (to.Length > Db.MaxName) return Error(400, $"Group name is at most {Db.MaxName} characters");

            var groups = await db.RenameGroupAsync(name, to);
            return groups is null
                ? Error(404, "That group is gone — reload the page")
                : Results.Ok(new { groups });
        });

        app.MapGet("/api/export", async (HttpResponse response) =>
        {
            response.Headers.ContentDisposition = "attachment; filename=\"portal.json\"";
            return Results.Ok(await db.ExportAsync());
        });

        // Full replace, for restoring onto a new machine
        app.MapPost("/api/import", async (ImportInput input) =>
        {
            if (input.Sites is null) return Error(400, "No sites in that file — it does not look like an exported config");

            var rows = new List<(string, Validated, DateTimeOffset)>();
            foreach (var raw in input.Sites)
            {
                var v = Validator.Site(raw);
                if (!v.Ok) return Error(400, v.Error!);
                rows.Add((string.IsNullOrWhiteSpace(raw.Id) ? NewId() : raw.Id!, v, raw.AddedAt ?? DateTimeOffset.UtcNow));
            }

            await db.ImportAsync(rows, input.Groups ?? []);
            var (sites, groups, clicks) = await db.GetStateAsync();
            return Results.Ok(new { sites, groups, clicks });
        });
    }

    private static string NewId() => "s_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    private static IResult Error(int status, string message) =>
        Results.Json(new { error = message }, statusCode: status);

    private static IResult Gone() => Error(404, "That link is gone — reload the page");
}
