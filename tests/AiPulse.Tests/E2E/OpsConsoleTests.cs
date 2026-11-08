using System.IO.Compression;
using System.Net;
using System.Text.Json;
using AiPulse.Services;
using AiPulse.Services.Ops;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiPulse.Tests.E2E;

/// <summary>The admin operations console (/ops) and its machine endpoints, against the real app.</summary>
[Collection(E2ECollection.Name)]
public class OpsConsoleTests(AiPulseAppFactory app)
{
    private async Task RunOnePollAsync()
    {
        // The background watcher is switched off in these tests; drive one poll by hand (canned feeds + one that always fails).
        await app.Services.GetRequiredService<FeedAggregatorService>().GetAsync(force: true);
    }

    [Fact]
    public async Task Ops_page_shows_every_section_to_an_admin()
    {
        await RunOnePollAsync();
        var html = await (await app.SignedInAs(AiPulseAppFactory.AdminUser)).GetStringAsync("/ops");

        Assert.Equal("Operations", AppRoutes.H1(html));
        foreach (var section in new[] { "System", "Background poll", "Feed health", "AI features", "Backups", "Audit log" })
            Assert.Contains($">{section}</h2>", html);
        Assert.Contains("E2E Broken", html);           // the always-failing source is a worst offender
        Assert.Contains("Create backup now", html);
        Assert.Contains("How to restore a backup", html);
        Assert.Contains("Login.Success", html);        // the audit log has the e2e sign-ins
        app.AssertNoServerErrors();
    }

    [Fact]
    public async Task Status_json_is_admin_only_and_describes_the_instance()
    {
        Assert.Contains("/login", (await app.Anonymous().GetAsync("/ops/status.json")).Headers.Location!.ToString());
        var denied = await (await app.SignedInAs(AiPulseAppFactory.ReaderUser)).GetAsync("/ops/status.json");
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);

        await RunOnePollAsync();
        var response = await (await app.SignedInAs(AiPulseAppFactory.AdminUser)).GetAsync("/ops/status.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("system").GetProperty("version").GetString()));
        Assert.Contains(".NET", root.GetProperty("system").GetProperty("runtime").GetString());
        Assert.True(root.GetProperty("system").GetProperty("databaseBytes").GetInt64() > 0);

        var poll = root.GetProperty("poll");
        Assert.Equal(3, poll.GetProperty("enabledSources").GetInt32());
        Assert.True(poll.GetProperty("status").GetProperty("pollsCompleted").GetInt32() >= 1);
        Assert.Equal(3, poll.GetProperty("status").GetProperty("lastFetched").GetInt32());
        Assert.Equal(1, poll.GetProperty("status").GetProperty("lastFailed").GetInt32());

        var health = root.GetProperty("feedHealth");
        Assert.Equal(3, health.GetProperty("total").GetInt32());
        Assert.Contains(health.GetProperty("worstOffenders").EnumerateArray(), o => o.GetProperty("name").GetString() == "E2E Broken");
        Assert.True(root.GetProperty("ai").TryGetProperty("modelCallsAllowed", out _));
    }

    [Fact]
    public async Task Audit_csv_export_is_admin_only_and_filters()
    {
        var reader = await app.SignedInAs(AiPulseAppFactory.ReaderUser);
        Assert.NotEqual(HttpStatusCode.OK, (await reader.GetAsync("/ops/audit.csv")).StatusCode);

        // A failed login guarantees at least one Login.Failed row.
        await AiPulseAppFactory.PostLoginAsync(app.Anonymous(), AiPulseAppFactory.AdminUser, "wrong-password-for-audit");

        var admin = await app.SignedInAs(AiPulseAppFactory.AdminUser);
        var all = await admin.GetAsync("/ops/audit.csv");
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        Assert.Equal("text/csv", all.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", all.Content.Headers.ContentDisposition?.ToString());
        var csv = (await all.Content.ReadAsStringAsync()).TrimStart('﻿');
        Assert.StartsWith("Id,TimestampUtc,Action,ActorUserId,ActorUsername,TargetUserId,TargetUsername,Detail", csv);
        Assert.Contains("Login.Success", csv);

        var failed = (await admin.GetStringAsync("/ops/audit.csv?action=Login.Failed")).TrimStart('﻿');
        var rows = failed.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Contains(",Login.Failed,", r));

        var tomorrow = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var none = (await admin.GetStringAsync($"/ops/audit.csv?from={tomorrow}")).TrimStart('﻿');
        Assert.Single(none.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Server_side_backups_are_created_listed_and_downloadable_by_admins_only()
    {
        var store = app.Services.GetRequiredService<OpsBackupStore>();
        Assert.False(store.Directory.StartsWith(Path.Combine(app.ContentRoot, "App_Data"), StringComparison.OrdinalIgnoreCase),
            "Backups must live outside App_Data, or every backup would contain all the earlier ones.");

        var created = await store.CreateAsync();
        Assert.Contains(store.List(), b => b.Name == created.Name && b.Bytes > 0);

        var url = "/ops/backups/" + created.Name;
        Assert.NotEqual(HttpStatusCode.OK, (await (await app.SignedInAs(AiPulseAppFactory.ReaderUser)).GetAsync(url)).StatusCode);

        var admin = await app.SignedInAs(AiPulseAppFactory.AdminUser);
        var response = await admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using (var zip = new ZipArchive(await response.Content.ReadAsStreamAsync()))
            Assert.Contains(zip.Entries, e => e.FullName == "aipulse.db");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/ops/backups/..%2FApp_Data%2Faipulse.db")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/ops/backups/aipulse-backup-20000101-000000.zip")).StatusCode);
        app.AssertNoServerErrors();
    }
}
