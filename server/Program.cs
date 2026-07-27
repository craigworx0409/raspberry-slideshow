using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

const long MaxUploadBytes = 40L * 1024 * 1024;   // 40 MB per file

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUploadBytes);
var app = builder.Build();
const int HistoryDays = 7;
// The "full" variant is deliberately 1080p — it is what a Raspberry Pi frame
// displays, so shipping anything larger only costs memory on a 512 MB board.
const int FullMaxW = 1920, FullMaxH = 1080;
const int ThumbMaxW = 640, ThumbMaxH = 640;

var dbPath = app.Configuration["DbPath"] ?? "slideshow.db";
var mediaPath = Path.GetFullPath(app.Configuration["MediaPath"] ?? "media");
var deviceKey = app.Configuration["DeviceKey"] ?? "change-me-device-key";
var adminUser = app.Configuration["AdminUser"] ?? "admin";
var adminPass = app.Configuration["AdminPassword"] ?? "change-me-admin";
Directory.CreateDirectory(mediaPath);

SqliteConnection Open()
{
    var con = new SqliteConnection($"Data Source={dbPath}");
    con.Open();
    return con;
}

void Exec(SqliteConnection con, string sql, params (string Name, object? Value)[] ps)
{
    using var cmd = con.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    cmd.ExecuteNonQuery();
}

object? Scalar(SqliteConnection con, string sql, params (string Name, object? Value)[] ps)
{
    using var cmd = con.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    var v = cmd.ExecuteScalar();
    return v is DBNull ? null : v;
}

using (var con = Open())
{
    Exec(con, """
        CREATE TABLE IF NOT EXISTS devices(
            id          TEXT PRIMARY KEY,
            name        TEXT,
            ip          TEXT,
            current_url TEXT,
            desired_url TEXT,
            uptime_secs INTEGER,
            last_seen   TEXT,
            owner_email TEXT,
            health_json TEXT);
        CREATE TABLE IF NOT EXISTS commands(
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            device_id    TEXT NOT NULL,
            type         TEXT NOT NULL,
            created_at   TEXT NOT NULL,
            delivered_at TEXT);
        CREATE TABLE IF NOT EXISTS users(
            email         TEXT PRIMARY KEY,
            password_hash TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS sessions(
            token_hash TEXT PRIMARY KEY,
            email      TEXT NOT NULL,
            expires_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS collections(
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            name       TEXT NOT NULL UNIQUE COLLATE NOCASE,
            created_at TEXT NOT NULL);
        -- kind='upload' -> full/thumb are files under MediaPath
        -- kind='url'    -> src is a remote URL, no local files
        CREATE TABLE IF NOT EXISTS images(
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            kind       TEXT NOT NULL,
            title      TEXT,
            src        TEXT,
            full_file  TEXT,
            thumb_file TEXT,
            width      INTEGER,
            height     INTEGER,
            added_at   TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS image_collections(
            image_id      INTEGER NOT NULL,
            collection_id INTEGER NOT NULL,
            PRIMARY KEY (image_id, collection_id));
        CREATE TABLE IF NOT EXISTS health_history(
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            device_id TEXT NOT NULL,
            at        TEXT NOT NULL,
            wifi_dbm  INTEGER,
            swap_mb   INTEGER,
            temp_c    REAL,
            mem_mb    INTEGER);
        CREATE INDEX IF NOT EXISTS ix_health_device_at ON health_history(device_id, at);
        """);
}

// ---------- helpers ----------

static string HashPassword(string password)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
    return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
}

static bool VerifyPassword(string password, string stored)
{
    var parts = stored.Split('.');
    if (parts.Length != 2) return false;
    var salt = Convert.FromBase64String(parts[0]);
    var expected = Convert.FromBase64String(parts[1]);
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
    return CryptographicOperations.FixedTimeEquals(hash, expected);
}

static string Sha256B64(string s) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

static bool IsValidHttpUrl(string? url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");

bool IsAdmin(HttpContext ctx)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Basic ", StringComparison.Ordinal)) return false;
    string[] parts;
    try { parts = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..])).Split(':', 2); }
    catch (FormatException) { return false; }
    return parts.Length == 2 && parts[0] == adminUser && parts[1] == adminPass;
}

string? SessionEmail(HttpContext ctx)
{
    var token = ctx.Request.Cookies["session"];
    if (string.IsNullOrEmpty(token)) return null;
    using var con = Open();
    return Scalar(con, "SELECT email FROM sessions WHERE token_hash = $t AND expires_at > $now",
        ("$t", Sha256B64(token)), ("$now", DateTime.UtcNow.ToString("o"))) as string;
}

// ---------- auth gate ----------
// Anonymous: device heartbeat, login/logout/me, the slideshow page + its API,
// and /media (the frames fetch these with no credentials).
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path;
    var isAdminArea = p.StartsWithSegments("/admin") || p.StartsWithSegments("/library") ||
        (p.StartsWithSegments("/api") &&
         !p.StartsWithSegments("/api/heartbeat") && !p.StartsWithSegments("/api/login") &&
         !p.StartsWithSegments("/api/logout") && !p.StartsWithSegments("/api/me") &&
         !p.StartsWithSegments("/api/slideshow"));
    if (isAdminArea && !IsAdmin(ctx))
    {
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"slideshow\"";
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Text("ok"));

// ---------- media ----------

app.MapGet("/media/{file}", (string file) =>
{
    // Defend against traversal: only a bare filename from our own media dir.
    if (file.Contains('/') || file.Contains('\\') || file.Contains("..")) return Results.NotFound();
    var path = Path.Combine(mediaPath, file);
    if (!System.IO.File.Exists(path)) return Results.NotFound();
    return Results.File(path, "image/jpeg", enableRangeProcessing: true);
});

// ---------- device endpoint ----------

app.MapPost("/api/heartbeat", async (HttpContext ctx) =>
{
    if (ctx.Request.Headers["X-Api-Key"] != deviceKey) return Results.Unauthorized();
    var hb = await ctx.Request.ReadFromJsonAsync<Heartbeat>();
    if (hb?.DeviceId is not { Length: > 0 }) return Results.BadRequest();

    var now = DateTime.UtcNow.ToString("o");
    using var con = Open();
    var healthJson = hb.Health is null ? null : System.Text.Json.JsonSerializer.Serialize(hb.Health);
    Exec(con, """
        INSERT INTO devices(id, name, ip, current_url, uptime_secs, last_seen, health_json)
        VALUES ($id, $name, $ip, $url, $up, $now, $health)
        ON CONFLICT(id) DO UPDATE SET
            name = $name, ip = $ip, current_url = $url, uptime_secs = $up, last_seen = $now,
            health_json = COALESCE($health, health_json)
        """,
        ("$id", hb.DeviceId), ("$name", hb.Name),
        ("$ip", ctx.Connection.RemoteIpAddress?.ToString()),
        ("$url", hb.CurrentUrl), ("$up", hb.UptimeSecs), ("$now", now), ("$health", healthJson));

    if (hb.Health is { } hv)
    {
        double? Num(string k) => hv.TryGetValue(k, out var e) &&
            e.ValueKind == System.Text.Json.JsonValueKind.Number ? e.GetDouble() : null;
        Exec(con, """
            INSERT INTO health_history(device_id, at, wifi_dbm, swap_mb, temp_c, mem_mb)
            VALUES ($id, $now, $wifi, $swap, $temp, $mem)
            """,
            ("$id", hb.DeviceId), ("$now", now), ("$wifi", Num("wifiDbm")),
            ("$swap", Num("swapUsedMb")), ("$temp", Num("tempC")), ("$mem", Num("memAvailMb")));
        Exec(con, "DELETE FROM health_history WHERE at < $cutoff",
            ("$cutoff", DateTime.UtcNow.AddDays(-HistoryDays).ToString("o")));
    }

    var desired = Scalar(con, "SELECT desired_url FROM devices WHERE id = $id", ("$id", hb.DeviceId)) as string;

    var commands = new List<object>();
    using (var cmd = con.CreateCommand())
    {
        cmd.CommandText = "SELECT id, type FROM commands WHERE device_id = $id AND delivered_at IS NULL ORDER BY id";
        cmd.Parameters.AddWithValue("$id", hb.DeviceId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) commands.Add(new { id = r.GetInt64(0), type = r.GetString(1) });
    }
    Exec(con, "UPDATE commands SET delivered_at = $now WHERE device_id = $id AND delivered_at IS NULL",
        ("$now", now), ("$id", hb.DeviceId));

    return Results.Json(new { desiredUrl = desired, commands });
});

// ---------- slideshow feed (anonymous: the frames call this) ----------
// group=a,b  (omitted/empty = every collection)   img=full|thumb
app.MapGet("/api/slideshow", (string? group, string? img) =>
{
    var wantThumb = string.Equals(img, "thumb", StringComparison.OrdinalIgnoreCase);
    var groups = (group ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    using var con = Open();
    using var cmd = con.CreateCommand();
    if (groups.Length == 0)
    {
        cmd.CommandText = "SELECT id, kind, src, full_file, thumb_file, title FROM images ORDER BY id";
    }
    else
    {
        var names = string.Join(",", groups.Select((_, i) => $"$g{i}"));
        cmd.CommandText = $"""
            SELECT DISTINCT i.id, i.kind, i.src, i.full_file, i.thumb_file, i.title
            FROM images i
            JOIN image_collections ic ON ic.image_id = i.id
            JOIN collections c ON c.id = ic.collection_id
            WHERE c.name IN ({names}) COLLATE NOCASE
            ORDER BY i.id
            """;
        for (var i = 0; i < groups.Length; i++) cmd.Parameters.AddWithValue($"$g{i}", groups[i]);
    }

    var items = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        var kind = r.GetString(1);
        string? url = kind == "url"
            ? (r.IsDBNull(2) ? null : r.GetString(2))
            : "/media/" + (wantThumb && !r.IsDBNull(4) ? r.GetString(4) : r.GetString(3));
        if (url is null) continue;
        items.Add(new { id = r.GetInt64(0), url, title = r.IsDBNull(5) ? null : r.GetString(5) });
    }
    return Results.Json(new { count = items.Count, images = items });
});

// ---------- recipient auth ----------

app.MapPost("/api/login", async (HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<LoginReq>();
    if (req?.Email is not { Length: > 0 } || req.Password is not { Length: > 0 }) return Results.BadRequest();
    using var con = Open();
    var stored = Scalar(con, "SELECT password_hash FROM users WHERE email = $e COLLATE NOCASE",
        ("$e", req.Email.Trim())) as string;
    if (stored is null || !VerifyPassword(req.Password, stored))
    {
        await Task.Delay(300);
        return Results.Unauthorized();
    }
    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    Exec(con, "INSERT INTO sessions(token_hash, email, expires_at) VALUES ($t, $e, $exp)",
        ("$t", Sha256B64(token)), ("$e", req.Email.Trim().ToLowerInvariant()),
        ("$exp", DateTime.UtcNow.AddDays(90).ToString("o")));
    Exec(con, "DELETE FROM sessions WHERE expires_at < $now", ("$now", DateTime.UtcNow.ToString("o")));
    ctx.Response.Cookies.Append("session", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = !app.Environment.IsDevelopment(),   // plain http for local testing only
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(90),
    });
    return Results.Ok();
});

app.MapPost("/api/logout", (HttpContext ctx) =>
{
    var token = ctx.Request.Cookies["session"];
    if (!string.IsNullOrEmpty(token))
    {
        using var con = Open();
        Exec(con, "DELETE FROM sessions WHERE token_hash = $t", ("$t", Sha256B64(token)));
    }
    ctx.Response.Cookies.Delete("session");
    return Results.Ok();
});

// ---------- recipient API ----------

app.MapGet("/api/me", (HttpContext ctx) =>
{
    var email = SessionEmail(ctx);
    if (email is null) return Results.Unauthorized();
    using var con = Open();

    var devices = new List<object>();
    using (var cmd = con.CreateCommand())
    {
        cmd.CommandText = """
            SELECT id, name, current_url, desired_url, last_seen
            FROM devices WHERE owner_email = $e COLLATE NOCASE ORDER BY name
            """;
        cmd.Parameters.AddWithValue("$e", email);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            devices.Add(new
            {
                id = r.GetString(0),
                name = r.IsDBNull(1) ? null : r.GetString(1),
                currentUrl = r.IsDBNull(2) ? null : r.GetString(2),
                desiredUrl = r.IsDBNull(3) ? null : r.GetString(3),
                lastSeen = r.IsDBNull(4) ? null : r.GetString(4),
            });
    }
    return Results.Json(new { email, devices, collections = ListCollections(con) });
});

app.MapPost("/api/me/devices/{id}/url", async (string id, HttpContext ctx) =>
{
    var email = SessionEmail(ctx);
    if (email is null) return Results.Unauthorized();
    var req = await ctx.Request.ReadFromJsonAsync<SetUrl>();
    if (!IsValidHttpUrl(req?.Url)) return Results.BadRequest(new { error = "URL must start with http:// or https://" });
    using var con = Open();
    var owner = Scalar(con, "SELECT owner_email FROM devices WHERE id = $id", ("$id", id)) as string;
    if (!string.Equals(owner, email, StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
    Exec(con, "UPDATE devices SET desired_url = $url WHERE id = $id", ("$url", req!.Url), ("$id", id));
    return Results.Ok();
});

app.MapPost("/api/me/password", async (HttpContext ctx) =>
{
    var email = SessionEmail(ctx);
    if (email is null) return Results.Unauthorized();
    var req = await ctx.Request.ReadFromJsonAsync<ChangePasswordReq>();
    if (req?.Current is not { Length: > 0 } || req.New is not { Length: >= 8 })
        return Results.BadRequest(new { error = "New password must be at least 8 characters." });
    using var con = Open();
    var stored = Scalar(con, "SELECT password_hash FROM users WHERE email = $e COLLATE NOCASE", ("$e", email)) as string;
    if (stored is null || !VerifyPassword(req.Current, stored)) return Results.Unauthorized();
    Exec(con, "UPDATE users SET password_hash = $h WHERE email = $e COLLATE NOCASE",
        ("$h", HashPassword(req.New)), ("$e", email));
    return Results.Ok();
});

// ---------- admin: devices ----------

app.MapGet("/api/devices", () =>
{
    using var con = Open();
    using var cmd = con.CreateCommand();
    cmd.CommandText = """
        SELECT d.id, d.name, d.ip, d.current_url, d.desired_url, d.uptime_secs, d.last_seen, d.owner_email,
               (SELECT COUNT(*) FROM commands c WHERE c.device_id = d.id AND c.delivered_at IS NULL),
               d.health_json
        FROM devices d ORDER BY d.name, d.id
        """;
    var list = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
        list.Add(new
        {
            id = r.GetString(0),
            name = r.IsDBNull(1) ? null : r.GetString(1),
            ip = r.IsDBNull(2) ? null : r.GetString(2),
            currentUrl = r.IsDBNull(3) ? null : r.GetString(3),
            desiredUrl = r.IsDBNull(4) ? null : r.GetString(4),
            uptimeSecs = r.IsDBNull(5) ? (long?)null : r.GetInt64(5),
            lastSeen = r.IsDBNull(6) ? null : r.GetString(6),
            ownerEmail = r.IsDBNull(7) ? null : r.GetString(7),
            pendingCommands = r.GetInt64(8),
            health = r.IsDBNull(9)
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(r.GetString(9)),
        });
    return Results.Json(list);
});

app.MapPost("/api/devices/{id}/url", async (string id, HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<SetUrl>();
    if (!IsValidHttpUrl(req?.Url)) return Results.BadRequest(new { error = "URL must start with http:// or https://" });
    using var con = Open();
    Exec(con, "UPDATE devices SET desired_url = $url WHERE id = $id", ("$url", req!.Url), ("$id", id));
    return Results.Ok();
});

app.MapPost("/api/devices/{id}/command", async (string id, HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<QueueCommand>();
    if (req?.Type is not ("reload" or "reboot" or "poweroff" or "forget-wifi")) return Results.BadRequest();
    using var con = Open();
    Exec(con, "INSERT INTO commands(device_id, type, created_at) VALUES ($id, $type, $now)",
        ("$id", id), ("$type", req.Type), ("$now", DateTime.UtcNow.ToString("o")));
    return Results.Ok();
});

app.MapPost("/api/devices/{id}/owner", async (string id, HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<AssignOwnerReq>();
    if (req?.Email is not { Length: > 0 }) return Results.BadRequest(new { error = "Email required." });
    var email = req.Email.Trim().ToLowerInvariant();
    using var con = Open();
    if (Scalar(con, "SELECT 1 FROM devices WHERE id = $id", ("$id", id)) is null) return Results.NotFound();
    var exists = Scalar(con, "SELECT 1 FROM users WHERE email = $e COLLATE NOCASE", ("$e", email)) is not null;
    if (req.Password is { Length: > 0 })
    {
        if (req.Password.Length < 8) return Results.BadRequest(new { error = "Password must be at least 8 characters." });
        Exec(con, """
            INSERT INTO users(email, password_hash) VALUES ($e, $h)
            ON CONFLICT(email) DO UPDATE SET password_hash = $h
            """, ("$e", email), ("$h", HashPassword(req.Password)));
    }
    else if (!exists) return Results.BadRequest(new { error = "New user — an initial password is required." });
    Exec(con, "UPDATE devices SET owner_email = $e WHERE id = $id", ("$e", email), ("$id", id));
    return Results.Ok();
});

app.MapDelete("/api/devices/{id}/owner", (string id) =>
{
    using var con = Open();
    Exec(con, "UPDATE devices SET owner_email = NULL WHERE id = $id", ("$id", id));
    return Results.Ok();
});

app.MapDelete("/api/devices/{id}", (string id) =>
{
    using var con = Open();
    Exec(con, "DELETE FROM commands WHERE device_id = $id", ("$id", id));
    Exec(con, "DELETE FROM health_history WHERE device_id = $id", ("$id", id));
    Exec(con, "DELETE FROM devices WHERE id = $id", ("$id", id));
    return Results.Ok();
});

app.MapGet("/api/devices/{id}/history", (string id, double? hours) =>
{
    var since = DateTime.UtcNow.AddHours(-Math.Clamp(hours ?? 6, 0.05, 24 * HistoryDays));
    using var con = Open();
    using var cmd = con.CreateCommand();
    cmd.CommandText = """
        SELECT at, wifi_dbm, swap_mb, temp_c, mem_mb FROM health_history
        WHERE device_id = $id AND at >= $since ORDER BY at
        """;
    cmd.Parameters.AddWithValue("$id", id);
    cmd.Parameters.AddWithValue("$since", since.ToString("o"));
    var pts = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
        pts.Add(new
        {
            at = r.GetString(0),
            wifiDbm = r.IsDBNull(1) ? (int?)null : r.GetInt32(1),
            swapMb = r.IsDBNull(2) ? (int?)null : r.GetInt32(2),
            tempC = r.IsDBNull(3) ? (double?)null : r.GetDouble(3),
            memMb = r.IsDBNull(4) ? (int?)null : r.GetInt32(4),
        });
    return Results.Json(new { count = pts.Count, points = pts });
});

// ---------- collections ----------

List<object> ListCollections(SqliteConnection con)
{
    using var cmd = con.CreateCommand();
    cmd.CommandText = """
        SELECT c.id, c.name, (SELECT COUNT(*) FROM image_collections ic WHERE ic.collection_id = c.id)
        FROM collections c ORDER BY c.name
        """;
    var list = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read()) list.Add(new { id = r.GetInt64(0), name = r.GetString(1), imageCount = r.GetInt64(2) });
    return list;
}

app.MapGet("/api/collections", () =>
{
    using var con = Open();
    return Results.Json(ListCollections(con));
});

app.MapPost("/api/collections", async (HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<NameReq>();
    var name = req?.Name?.Trim();
    if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { error = "Name required." });
    if (name.Contains(',')) return Results.BadRequest(new { error = "Commas separate group names in URLs — pick a name without one." });
    using var con = Open();
    try
    {
        Exec(con, "INSERT INTO collections(name, created_at) VALUES ($n, $now)",
            ("$n", name), ("$now", DateTime.UtcNow.ToString("o")));
    }
    catch (SqliteException) { return Results.BadRequest(new { error = "That collection already exists." }); }
    return Results.Ok();
});

app.MapDelete("/api/collections/{id:long}", (long id) =>
{
    using var con = Open();
    Exec(con, "DELETE FROM image_collections WHERE collection_id = $id", ("$id", id));
    Exec(con, "DELETE FROM collections WHERE id = $id", ("$id", id));
    return Results.Ok();
});

// ---------- images ----------

app.MapGet("/api/images", (long? collectionId) =>
{
    using var con = Open();
    using var cmd = con.CreateCommand();
    cmd.CommandText = collectionId is null
        ? "SELECT id, kind, title, src, full_file, thumb_file, width, height FROM images ORDER BY id DESC"
        : """
          SELECT i.id, i.kind, i.title, i.src, i.full_file, i.thumb_file, i.width, i.height
          FROM images i JOIN image_collections ic ON ic.image_id = i.id
          WHERE ic.collection_id = $c ORDER BY i.id DESC
          """;
    if (collectionId is not null) cmd.Parameters.AddWithValue("$c", collectionId);

    var list = new List<object>();
    using (var r = cmd.ExecuteReader())
        while (r.Read())
        {
            var kind = r.GetString(1);
            list.Add(new
            {
                id = r.GetInt64(0),
                kind,
                title = r.IsDBNull(2) ? null : r.GetString(2),
                full = kind == "url" ? r.GetString(3) : "/media/" + r.GetString(4),
                thumb = kind == "url" ? r.GetString(3) : "/media/" + r.GetString(5),
                width = r.IsDBNull(6) ? (int?)null : r.GetInt32(6),
                height = r.IsDBNull(7) ? (int?)null : r.GetInt32(7),
            });
        }

    // Collection membership, so the library UI can show/edit tags.
    var member = new Dictionary<long, List<string>>();
    using (var mc = con.CreateCommand())
    {
        mc.CommandText = """
            SELECT ic.image_id, c.name FROM image_collections ic
            JOIN collections c ON c.id = ic.collection_id
            """;
        using var r = mc.ExecuteReader();
        while (r.Read())
        {
            var iid = r.GetInt64(0);
            if (!member.TryGetValue(iid, out var l)) member[iid] = l = new List<string>();
            l.Add(r.GetString(1));
        }
    }
    return Results.Json(new { images = list, collections = member });
});

// Upload one or more files; each is auto-oriented and re-encoded to a 1080p
// "full" variant plus a small thumb. Originals are intentionally not kept —
// a photo frame never needs them and they would dominate the disk.
app.MapPost("/api/images/upload", async (HttpContext ctx) =>
{
    if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Expected a multipart form." });
    var form = await ctx.Request.ReadFormAsync();
    var collectionIds = (form["collectionId"].ToString() ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => long.TryParse(s, out var v) ? v : -1).Where(v => v > 0).ToArray();

    var added = new List<long>();
    var failed = new List<string>();
    using var con = Open();

    foreach (var file in form.Files)
    {
        if (file.Length == 0 || file.Length > MaxUploadBytes) { failed.Add(file.FileName); continue; }
        try
        {
            await using var stream = file.OpenReadStream();
            using var image = await Image.LoadAsync(stream);   // throws if not an image
            image.Mutate(x => x.AutoOrient());

            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var slug = $"{stamp}-{Random.Shared.Next(1000, 9999)}";
            var fullName = slug + "-full.jpg";
            var thumbName = slug + "-thumb.jpg";

            using (var full = image.Clone(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max, Size = new Size(FullMaxW, FullMaxH)
            })))
            {
                await full.SaveAsJpegAsync(Path.Combine(mediaPath, fullName), new JpegEncoder { Quality = 85 });
            }
            using (var thumb = image.Clone(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max, Size = new Size(ThumbMaxW, ThumbMaxH)
            })))
            {
                await thumb.SaveAsJpegAsync(Path.Combine(mediaPath, thumbName), new JpegEncoder { Quality = 75 });
            }

            Exec(con, """
                INSERT INTO images(kind, title, full_file, thumb_file, width, height, added_at)
                VALUES ('upload', $t, $f, $th, $w, $h, $now)
                """,
                ("$t", Path.GetFileNameWithoutExtension(file.FileName)), ("$f", fullName), ("$th", thumbName),
                ("$w", image.Width), ("$h", image.Height), ("$now", DateTime.UtcNow.ToString("o")));
            var id = (long)Scalar(con, "SELECT last_insert_rowid()")!;
            foreach (var cid in collectionIds)
                Exec(con, "INSERT OR IGNORE INTO image_collections(image_id, collection_id) VALUES ($i, $c)",
                    ("$i", id), ("$c", cid));
            added.Add(id);
        }
        catch (Exception) { failed.Add(file.FileName); }
    }
    return Results.Json(new { added = added.Count, failed });
});

// Reference an image hosted elsewhere — nothing is copied or resized, so the
// remote server must be able to serve it to the frame directly.
app.MapPost("/api/images/url", async (HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<AddUrlImageReq>();
    if (!IsValidHttpUrl(req?.Url)) return Results.BadRequest(new { error = "URL must start with http:// or https://" });
    using var con = Open();
    Exec(con, """
        INSERT INTO images(kind, title, src, added_at) VALUES ('url', $t, $u, $now)
        """, ("$t", req!.Title), ("$u", req.Url), ("$now", DateTime.UtcNow.ToString("o")));
    var id = (long)Scalar(con, "SELECT last_insert_rowid()")!;
    foreach (var cid in req.CollectionIds ?? Array.Empty<long>())
        Exec(con, "INSERT OR IGNORE INTO image_collections(image_id, collection_id) VALUES ($i, $c)",
            ("$i", id), ("$c", cid));
    return Results.Json(new { id });
});

app.MapPost("/api/images/{id:long}/collections", async (long id, HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<IdsReq>();
    using var con = Open();
    Exec(con, "DELETE FROM image_collections WHERE image_id = $i", ("$i", id));
    foreach (var cid in req?.Ids ?? Array.Empty<long>())
        Exec(con, "INSERT OR IGNORE INTO image_collections(image_id, collection_id) VALUES ($i, $c)",
            ("$i", id), ("$c", cid));
    return Results.Ok();
});

app.MapDelete("/api/images/{id:long}", (long id) =>
{
    using var con = Open();
    using (var cmd = con.CreateCommand())
    {
        cmd.CommandText = "SELECT kind, full_file, thumb_file FROM images WHERE id = $i";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        if (r.Read() && r.GetString(0) == "upload")
            foreach (var f in new[] { r.GetString(1), r.GetString(2) })
            {
                var p = Path.Combine(mediaPath, f);
                if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
            }
    }
    Exec(con, "DELETE FROM image_collections WHERE image_id = $i", ("$i", id));
    Exec(con, "DELETE FROM images WHERE id = $i", ("$i", id));
    return Results.Ok();
});

app.Run();

record Heartbeat(string DeviceId, string? Name, string? CurrentUrl, long? UptimeSecs,
                 Dictionary<string, System.Text.Json.JsonElement>? Health);
record SetUrl(string Url);
record QueueCommand(string Type);
record LoginReq(string Email, string Password);
record ChangePasswordReq(string Current, string New);
record AssignOwnerReq(string Email, string? Password);
record NameReq(string Name);
record IdsReq(long[] Ids);
record AddUrlImageReq(string Url, string? Title, long[]? CollectionIds);
