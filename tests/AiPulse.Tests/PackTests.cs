using System.Text.Json;
using AiPulse.Models;
using AiPulse.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiPulse.Tests;

/// <summary>Every shipped pack, read straight from the repository: the data a release is made of must be well formed.</summary>
public class PackContentTests
{
    private static readonly string PacksDir = Path.Combine(TestData.RepoRoot, "Data", "packs");
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static IEnumerable<object[]> PackIds => Directory.GetDirectories(PacksDir).Select(d => new object[] { Path.GetFileName(d) });

    private static List<T> Read<T>(string pack, string file)
    {
        var path = Path.Combine(PacksDir, pack, file);
        return File.Exists(path) ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), Json)! : new List<T>();
    }

    [Fact]
    public void The_starter_packs_are_present()
    {
        var ids = Directory.GetDirectories(PacksDir).Select(Path.GetFileName).ToHashSet();
        Assert.Superset(new HashSet<string?> { "web", "dotnet", "python", "devops", "security" }, ids);
    }

    [Theory, MemberData(nameof(PackIds))]
    public void The_pack_id_is_safe_and_never_the_built_in_one(string id)
    {
        Assert.True(PackService.IsValidId(id));
        Assert.False(id.Equals(PackService.AiPackId, StringComparison.OrdinalIgnoreCase));
    }

    [Theory, MemberData(nameof(PackIds))]
    public void The_pack_describes_itself(string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PacksDir, id, "pack.json")));
        foreach (var field in new[] { "name", "topic", "description" })
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty(field).GetString()), $"{id}: pack.json has no {field}");
        Assert.True(doc.RootElement.GetProperty("topic").GetString()!.Length <= SiteSettings.MaxTopic);
    }

    [Theory, MemberData(nameof(PackIds))]
    public void The_feeds_are_well_formed_and_unique(string id)
    {
        var sources = Read<FeedSource>(id, "sources.json");
        Assert.True(sources.Count >= 10, $"{id}: a pack should follow at least 10 feeds");
        Assert.Equal(sources.Count, sources.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(sources.Count, sources.Select(s => s.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var s in sources)
        {
            Assert.True(Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps, $"{id}/{s.Name}: feed address must be https");
            Assert.Contains(s.Category, new[] { "News", "Research", "Tools", "Community" });
            Assert.Contains(s.ContentType, new[] { "News", "Tutorial", "Release", "Paper", "Discussion", "Video" });
            Assert.Contains(s.Level, new[] { "Beginner", "Intermediate", "Advanced" });
            Assert.False(string.IsNullOrWhiteSpace(s.Note), $"{id}/{s.Name}: describe the feed in a sentence");
            Assert.True(s.Enabled);
        }
        Assert.Contains(sources, s => s.ContentType == "Release");   // every pack should surface new versions too
    }

    [Theory, MemberData(nameof(PackIds))]
    public void The_glossary_is_unique_complete_and_every_related_term_resolves(string id)
    {
        var terms = Read<GlossaryTerm>(id, "glossary.json");
        Assert.True(terms.Count >= 12, $"{id}: a glossary this small is not worth switching on");
        var names = new HashSet<string>(terms.Select(t => t.Term), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(terms.Count, names.Count);

        var seen = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var t in terms)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.ShortDef), $"{id}/{t.Term}: no short definition");
            Assert.False(string.IsNullOrWhiteSpace(t.Details), $"{id}/{t.Term}: no details");
            Assert.False(string.IsNullOrWhiteSpace(t.WhenToUse), $"{id}/{t.Term}: no 'when to use'");
            foreach (var related in t.Related) Assert.True(names.Contains(related), $"{id}/{t.Term}: related term \"{related}\" is not in the pack's glossary");
            Assert.DoesNotContain(t.Term, t.Related, StringComparer.OrdinalIgnoreCase);
            if (t.LearnMoreUrl is not null) Assert.True(Uri.TryCreate(t.LearnMoreUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps, $"{id}/{t.Term}: bad link");
            // An alias must not be another term's name (it would tag items with the wrong canonical term).
            foreach (var alias in t.Aliases) Assert.False(names.Contains(alias) && !alias.Equals(t.Term, StringComparison.OrdinalIgnoreCase), $"{id}/{t.Term}: alias \"{alias}\" is also a term");
        }
    }

    [Theory, MemberData(nameof(PackIds))]
    public void The_learning_path_has_levels_steps_and_resolvable_terms(string id)
    {
        var modules = Read<LearningModule>(id, "learning.json");
        Assert.Equal(3, modules.Count);
        Assert.Equal(new[] { "Beginner", "Intermediate", "Advanced" }, modules.Select(m => m.Level));
        var terms = Read<GlossaryTerm>(id, "glossary.json").Select(t => t.Term).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules)
        {
            Assert.True(m.Steps.Length >= 3, $"{id}/{m.Title}: needs at least three steps");
            foreach (var step in m.Steps)
            {
                Assert.False(string.IsNullOrWhiteSpace(step.Text));
                Assert.True(Uri.TryCreate(step.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps, $"{id}/{m.Title}: step needs an https link");
            }
            foreach (var t in m.RelatedTerms) Assert.True(terms.Contains(t), $"{id}/{m.Title}: related term \"{t}\" is not in the glossary");
        }
    }

    [Theory, MemberData(nameof(PackIds))]
    public void The_tools_and_tips_are_filled_in_and_unique(string id)
    {
        var tools = Read<ToolEntry>(id, "tools.json");
        Assert.True(tools.Count >= 5);
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var t in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.OneLiner));
            Assert.NotEmpty(t.BestFor);
            Assert.True(Uri.TryCreate(t.Url, UriKind.Absolute, out _), $"{id}/{t.Name}: bad link");
        }
        var tips = Read<PracticeTip>(id, "practices.json");
        Assert.True(tips.Count >= 4);
        Assert.Equal(tips.Count, tips.Select(t => t.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

public class PackServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "packs-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "Data");
    private string State => Path.Combine(_root, "App_Data", "packs.json");

    public PackServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(Data, "packs", "demo"));
        Directory.CreateDirectory(Path.Combine(Data, "packs", "extra"));
        File.WriteAllText(Path.Combine(Data, "glossary.json"), """[{"Term":"RAG","Category":"c","ShortDef":"s"}]""");
        File.WriteAllText(Path.Combine(Data, "packs", "demo", "pack.json"), """{"name":"Demo","topic":"Demo","description":"d"}""");
        File.WriteAllText(Path.Combine(Data, "packs", "demo", "glossary.json"), """[{"Term":"Alpha","Category":"c","ShortDef":"s"}]""");
        File.WriteAllText(Path.Combine(Data, "packs", "extra", "glossary.json"), """[{"Term":"Beta","Category":"c","ShortDef":"s"}]""");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private PackService Make() => new(Data, State);

    [Fact]
    public void A_new_install_has_only_the_built_in_AI_pack_on()
    {
        var packs = Make();
        Assert.Equal(new[] { "ai" }, packs.EnabledIds);
        Assert.True(packs.AiEnabled);
        Assert.Equal(new[] { "ai", "demo", "extra" }, packs.All().Select(p => p.Id));
        Assert.True(packs.All()[0].Builtin);
    }

    [Fact]
    public void Packs_report_their_size_and_name()
    {
        var demo = Make().Find("demo")!;
        Assert.Equal("Demo", demo.Name);
        Assert.Equal(1, demo.GlossaryTerms);
        Assert.False(demo.Enabled);
        Assert.Equal("extra", Make().Find("EXTRA")!.Id);   // ids are matched without regard to case, the folder name is kept
    }

    [Fact]
    public void Turning_a_pack_on_and_off_is_remembered_and_announced()
    {
        var packs = Make();
        var announced = 0;
        packs.Changed += () => announced++;

        Assert.Null(packs.SetEnabled("demo", true));
        Assert.True(packs.IsEnabled("demo"));
        Assert.Equal(1, announced);
        Assert.True(Make().IsEnabled("demo"));            // survives a restart

        Assert.Null(packs.SetEnabled("demo", true));       // already on: nothing changes, nothing announced
        Assert.Equal(1, announced);
        Assert.Null(packs.SetEnabled("demo", false));
        Assert.False(Make().IsEnabled("demo"));
    }

    [Fact]
    public void At_least_one_pack_stays_on_and_unknown_packs_are_refused()
    {
        var packs = Make();
        Assert.Contains("one pack", packs.SetEnabled("ai", false)!);
        Assert.True(packs.AiEnabled);

        Assert.NotNull(packs.SetEnabled("nope", true));
        Assert.NotNull(packs.SetEnabled("../etc", true));
        Assert.Null(packs.DirectoryOf("../Data"));
        Assert.Null(packs.DirectoryOf("a/b"));
    }

    [Fact]
    public void The_AI_pack_can_be_switched_off_once_another_is_on()
    {
        var packs = Make();
        packs.SetEnabled("demo", true);
        Assert.Null(packs.SetEnabled("ai", false));
        Assert.False(packs.AiEnabled);
        Assert.Equal(new[] { "demo" }, packs.EnabledIds);
    }

    [Fact]
    public void Files_come_from_every_enabled_pack_with_the_AI_pack_first()
    {
        var packs = Make();
        packs.SetEnabled("extra", true);
        packs.SetEnabled("demo", true);
        var files = packs.FilesFor("glossary.json").Select(f => Path.GetFileName(Path.GetDirectoryName(f))).ToList();
        Assert.Equal(new[] { "Data", "demo", "extra" }, files);
        Assert.Empty(packs.FilesFor("sources.json"));      // no pack in this fixture has one
    }

    [Fact]
    public void Approved_entries_go_to_the_AI_files_while_AI_is_on_otherwise_to_the_first_enabled_pack()
    {
        var packs = Make();
        Assert.Equal(Path.Combine(Data, "glossary.json"), packs.WriteTargetFor("glossary.json"));
        packs.SetEnabled("extra", true);
        packs.SetEnabled("ai", false);
        Assert.Equal(Path.Combine(Data, "packs", "extra", "glossary.json"), packs.WriteTargetFor("glossary.json"));
    }

    [Fact]
    public void A_corrupt_or_missing_state_file_falls_back_to_AI_only()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(State)!);
        File.WriteAllText(State, "{ nope");
        Assert.Equal(new[] { "ai" }, Make().EnabledIds);
        File.WriteAllText(State, """{"Enabled":[]}""");
        Assert.Equal(new[] { "ai" }, Make().EnabledIds);
    }

    [Theory]
    [InlineData("web", true)]
    [InlineData("dot-net_2", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("with space", false)]
    public void Ids_are_a_safe_alphabet(string? id, bool valid) => Assert.Equal(valid, PackService.IsValidId(id));

    [Fact]
    public void Applied_marks_are_kept()
    {
        var packs = Make();
        Assert.False(packs.WasApplied("demo"));
        packs.MarkApplied("demo");
        Assert.True(Make().WasApplied("demo"));
    }
}

public class PackKnowledgeTests : IDisposable
{
    private readonly TempEnvironment _env = new();
    private string Data => Path.Combine(_env.ContentRootPath, "Data");

    public PackKnowledgeTests()
    {
        Directory.CreateDirectory(Path.Combine(Data, "packs", "demo"));
        File.WriteAllText(Path.Combine(Data, "glossary.json"), """[{"Term":"RAG","Category":"c","ShortDef":"s"},{"Term":"Shared","Category":"c","ShortDef":"from ai"}]""");
        File.WriteAllText(Path.Combine(Data, "tools.json"), """[{"Name":"Claude Code","Category":"c","OneLiner":"o"}]""");
        File.WriteAllText(Path.Combine(Data, "benchmarks.json"), """[{"Name":"B","Category":"c","Description":"d","Url":"https://x.test"}]""");
        File.WriteAllText(Path.Combine(Data, "packs", "demo", "pack.json"), """{"name":"Demo","topic":"Demo"}""");
        File.WriteAllText(Path.Combine(Data, "packs", "demo", "glossary.json"), """[{"Term":"Alpha","Category":"c","ShortDef":"s"},{"Term":"shared","Category":"c","ShortDef":"from demo"}]""");
        File.WriteAllText(Path.Combine(Data, "packs", "demo", "tools.json"), """[{"Name":"Demo Tool","Category":"c","OneLiner":"o"}]""");
    }

    public void Dispose() => _env.Dispose();

    private (KnowledgeBaseService Kb, PackService Packs) Make()
    {
        var packs = new PackService(Data, Path.Combine(_env.ContentRootPath, "App_Data", "packs.json"));
        return (new KnowledgeBaseService(_env, null!, packs), packs);
    }

    [Fact]
    public void With_only_AI_on_the_knowledge_is_exactly_the_original_files()
    {
        var (kb, _) = Make();
        Assert.Equal(new[] { "RAG", "Shared" }, kb.Glossary.Select(t => t.Term));
        Assert.Equal(new[] { "Claude Code" }, kb.Tools.Select(t => t.Name));
        Assert.Single(kb.Benchmarks);
    }

    [Fact]
    public async Task Enabling_a_pack_adds_its_entries_and_the_first_pack_wins_a_duplicate_key()
    {
        var (kb, packs) = Make();
        _ = kb.Glossary;                                   // fill the cache first: it must be rebuilt
        packs.SetEnabled("demo", true);
        await Task.Run(() => kb.GetType().GetMethod("ResetKnowledgeCaches", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(kb, null));

        Assert.Equal(new[] { "RAG", "Shared", "Alpha" }, kb.Glossary.Select(t => t.Term));
        Assert.Equal("from ai", kb.Glossary.Single(t => t.Term.Equals("shared", StringComparison.OrdinalIgnoreCase)).ShortDef);
        Assert.Equal(new[] { "Claude Code", "Demo Tool" }, kb.Tools.Select(t => t.Name));
    }

    [Fact]
    public void Without_the_AI_pack_its_knowledge_and_AI_only_lists_are_gone()
    {
        var (kb, packs) = Make();
        packs.SetEnabled("demo", true);
        packs.SetEnabled("ai", false);

        Assert.Equal(new[] { "Alpha", "shared" }, kb.Glossary.Select(t => t.Term));
        Assert.Empty(kb.Benchmarks);
        Assert.Empty(kb.ModelDirectory);
        Assert.Empty(kb.Courses);
    }

    [Fact]
    public async Task An_approved_entry_lands_in_the_right_file_and_never_copies_other_packs_into_it()
    {
        var (kb, packs) = Make();
        packs.SetEnabled("demo", true);
        _ = kb.Glossary;

        // Candidate bookkeeping needs a database; the file behaviour is what is under test here.
        var append = kb.GetType().GetMethod("AppendToDataFile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(typeof(GlossaryTerm));
        append.Invoke(kb, new object[] { "glossary.json", new GlossaryTerm { Term = "Gamma", Category = "c", ShortDef = "s" } });
        await Task.CompletedTask;

        var aiFile = File.ReadAllText(Path.Combine(Data, "glossary.json"));
        Assert.Contains("Gamma", aiFile);
        Assert.DoesNotContain("Alpha", aiFile);            // the demo pack's term was not copied into the AI file

        packs.SetEnabled("ai", false);
        append.Invoke(kb, new object[] { "glossary.json", new GlossaryTerm { Term = "Delta", Category = "c", ShortDef = "s" } });
        Assert.Contains("Delta", File.ReadAllText(Path.Combine(Data, "packs", "demo", "glossary.json")));
        Assert.DoesNotContain("Delta", File.ReadAllText(Path.Combine(Data, "glossary.json")));
    }
}

public class PackSourcesTests : IDisposable
{
    private readonly TempEnvironment _env = new();
    private IDbContextFactory<AiPulseDbContext> _db = null!;
    private string Data => Path.Combine(_env.ContentRootPath, "Data");

    public PackSourcesTests()
    {
        Directory.CreateDirectory(Path.Combine(Data, "packs", "demo"));
        Directory.CreateDirectory(Path.Combine(_env.ContentRootPath, "App_Data"));
        File.WriteAllText(Path.Combine(Data, "sources.json"), """[{"Name":"AI One","Url":"https://ai.example/feed","Category":"News"}]""");
        File.WriteAllText(Path.Combine(Data, "packs", "demo", "pack.json"), """{"name":"Demo","topic":"Demo"}""");
        File.WriteAllText(Path.Combine(Data, "packs", "demo", "sources.json"),
            """[{"Name":"Demo One","Url":"https://demo.example/a","Category":"News"},{"Name":"Demo Two","Url":"https://demo.example/b","Category":"Tools","ContentType":"Release"}]""");
        var options = new DbContextOptionsBuilder<AiPulseDbContext>().UseSqlite($"Data Source={Path.Combine(_env.ContentRootPath, "App_Data", "t.db")}").Options;
        _db = new SimpleFactory(options);
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); _env.Dispose(); }

    private sealed class SimpleFactory(DbContextOptions<AiPulseDbContext> options) : IDbContextFactory<AiPulseDbContext>
    {
        public AiPulseDbContext CreateDbContext() => new(options);
    }

    private async Task<KnowledgeBaseService> StartAsync()
    {
        var packs = new PackService(Data, Path.Combine(_env.ContentRootPath, "App_Data", "packs.json"));
        var kb = new KnowledgeBaseService(_env, _db, packs);
        await kb.InitializeSourcesAsync();
        return kb;
    }

    [Fact]
    public async Task A_fresh_install_follows_only_the_AI_feeds()
    {
        var kb = await StartAsync();
        Assert.Equal(new[] { "AI One" }, kb.Sources.Select(s => s.Name));
    }

    [Fact]
    public async Task Enabling_a_pack_adds_its_feeds_and_disabling_switches_them_off_without_deleting()
    {
        var kb = await StartAsync();

        Assert.Null(await kb.SetPackEnabledAsync("demo", true));
        Assert.Equal(new[] { "AI One", "Demo One", "Demo Two" }, kb.Sources.Select(s => s.Name));
        Assert.All(kb.Sources, s => Assert.True(s.Enabled));

        Assert.Null(await kb.SetPackEnabledAsync("demo", false));
        Assert.Equal(3, kb.Sources.Count);                 // still listed
        Assert.Equal(new[] { "AI One" }, kb.Sources.Where(s => s.Enabled).Select(s => s.Name));

        Assert.Null(await kb.SetPackEnabledAsync("demo", true));
        Assert.Equal(3, kb.Sources.Count);                 // switched back on, not duplicated
        Assert.All(kb.Sources, s => Assert.True(s.Enabled));
    }

    [Fact]
    public async Task A_feed_the_admin_deleted_is_not_brought_back_by_a_restart()
    {
        var kb = await StartAsync();
        await kb.SetPackEnabledAsync("demo", true);
        var two = kb.SourceRecords.Single(s => s.Name == "Demo Two");
        await using (var db = await _db.CreateDbContextAsync())
        {
            db.Sources.Remove(await db.Sources.FindAsync(two.Id) ?? throw new InvalidOperationException());
            await db.SaveChangesAsync();
        }

        var restarted = await StartAsync();                // same database, same packs.json
        Assert.DoesNotContain(restarted.Sources, s => s.Name == "Demo Two");
        Assert.Contains(restarted.Sources, s => s.Name == "Demo One");
    }

    [Fact]
    public async Task The_AI_pack_can_be_switched_off_and_its_feeds_stop_polling()
    {
        var kb = await StartAsync();
        await kb.SetPackEnabledAsync("demo", true);

        Assert.Null(await kb.SetPackEnabledAsync("ai", false));
        Assert.Equal(new[] { "Demo One", "Demo Two" }, kb.Sources.Where(s => s.Enabled).Select(s => s.Name));
        Assert.Contains("one pack", await kb.SetPackEnabledAsync("demo", false) ?? "");   // the last pack stays on
    }

    [Fact]
    public async Task Turning_a_pack_on_tells_listeners_so_the_tag_vocabulary_is_rebuilt()
    {
        var kb = await StartAsync();
        var raised = 0;
        kb.PacksChanged += () => raised++;
        await kb.SetPackEnabledAsync("demo", true);
        Assert.Equal(1, raised);
        Assert.NotNull(await kb.SetPackEnabledAsync("missing", true));
        Assert.Equal(1, raised);
    }
}
