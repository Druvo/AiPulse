using System.Text.Json;

namespace AiPulse.Services;

/// <summary>A topic bundle: feeds to follow plus the glossary, tools, tips and learning path that go with them.</summary>
public sealed record PackInfo(
    string Id, string Name, string Description, string Topic, bool Builtin,
    int Sources, int GlossaryTerms, int Tools, int LearningModules, bool Enabled);

/// <summary>
/// Content packs make AiPulse a hub for any developer topic, not just AI. The original AI content stays where it always was
/// (<c>Data/*.json</c>) and is the built-in pack "ai"; every other pack is a folder <c>Data/packs/{id}/</c> with a <c>pack.json</c>
/// (name, description, topic) and any of <c>sources.json glossary.json tools.json practices.json learning.json</c>, in the same formats.
/// Which packs are on is an admin choice kept in <c>App_Data/packs.json</c> (default: only "ai", so existing installs do not change).
/// Turning a pack on adds its feeds and shows its glossary, tools and learning path; turning it off hides them and switches its feeds
/// off. Nothing is ever deleted.
/// </summary>
public sealed class PackService
{
    public const string AiPackId = "ai";
    public static readonly string[] KnowledgeFiles = { "glossary.json", "tools.json", "practices.json", "learning.json" };

    private readonly string _dataDir;
    private readonly string _statePath;
    private readonly object _gate = new();
    private State _state;

    private sealed class State
    {
        public List<string> Enabled { get; set; } = new() { AiPackId };
        /// <summary>Packs whose feeds have been added to the database at least once, so a feed an admin later deleted is not re-added on every start.</summary>
        public List<string> Applied { get; set; } = new();
    }

    /// <summary>Raised after the set of enabled packs changes (data caches and tag vocabulary should be rebuilt).</summary>
    public event Action? Changed;

    public PackService(IWebHostEnvironment env) : this(Path.Combine(env.ContentRootPath, "Data"), Path.Combine(env.ContentRootPath, "App_Data", "packs.json")) { }

    public PackService(string dataDir, string statePath)
    {
        _dataDir = dataDir;
        _statePath = statePath;
        _state = LoadState();
    }

    public string DataDirectory => _dataDir;

    /// <summary>
    /// Where entries an admin approves are kept: <c>App_Data/content/{pack}/</c>. They used to be appended to the shipped
    /// <c>Data/*.json</c> files, but a deploy replaces <c>Data/</c> with the release's copy and quietly discarded them; App_Data is
    /// never touched by a deploy and is included in backups. These files are read after the pack's own, in the same formats.
    /// </summary>
    public string ContentDirectory => Path.Combine(Path.GetDirectoryName(_statePath)!, "content");

    private string LocalFile(string packId, string file) => Path.Combine(ContentDirectory, packId, file);
    private string PacksDirectory => Path.Combine(_dataDir, "packs");

    public bool AiEnabled => IsEnabled(AiPackId);

    /// <summary>Pages that only make sense for the AI pack (a directory of free LLM APIs, a chat playground for local models).</summary>
    public static readonly string[] AiOnlyPaths = { "/free-apis", "/playground" };

    public static bool IsAiOnlyPath(string? path) =>
        !string.IsNullOrEmpty(path) && AiOnlyPaths.Any(p => path.TrimEnd('/').Equals(p, StringComparison.OrdinalIgnoreCase));

    public bool IsEnabled(string id) { lock (_gate) return _state.Enabled.Contains(id, StringComparer.OrdinalIgnoreCase); }

    public IReadOnlyList<string> EnabledIds { get { lock (_gate) return _state.Enabled.ToList(); } }

    public bool WasApplied(string id) { lock (_gate) return _state.Applied.Contains(id, StringComparer.OrdinalIgnoreCase); }

    /// <summary>All packs found on disk (the built-in AI pack first), with their sizes and whether they are on.</summary>
    public IReadOnlyList<PackInfo> All()
    {
        var list = new List<PackInfo>
        {
            Describe(AiPackId, "AI", "News, research, tools, glossary and a learning path for working with AI and LLMs. The original AiPulse content.", "AI", _dataDir, builtin: true)
        };
        if (Directory.Exists(PacksDirectory))
        {
            foreach (var dir in Directory.GetDirectories(PacksDirectory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var id = Path.GetFileName(dir);
                if (!IsValidId(id) || id.Equals(AiPackId, StringComparison.OrdinalIgnoreCase)) continue;
                var meta = ReadMeta(dir);
                list.Add(Describe(id, meta.Name ?? id, meta.Description ?? "", meta.Topic ?? meta.Name ?? id, dir, builtin: false));
            }
        }
        return list;
    }

    public PackInfo? Find(string id) => All().FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The folder holding a pack's files, or null if there is no such pack.</summary>
    public string? DirectoryOf(string id)
    {
        if (id.Equals(AiPackId, StringComparison.OrdinalIgnoreCase)) return _dataDir;
        if (!IsValidId(id)) return null;
        var dir = Path.Combine(PacksDirectory, id);
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>The files for one kind of content, from every enabled pack, built-in AI pack first; each pack's shipped file, then the entries admins added to it.</summary>
    public IReadOnlyList<string> FilesFor(string file)
    {
        var result = new List<string>();
        foreach (var id in EnabledIds.OrderBy(i => i.Equals(AiPackId, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(i => i, StringComparer.OrdinalIgnoreCase))
        {
            var dir = DirectoryOf(id);
            if (dir is null) continue;
            var path = Path.Combine(dir, file);
            if (File.Exists(path)) result.Add(path);
            var local = LocalFile(id, file);
            if (File.Exists(local)) result.Add(local);
        }
        return result;
    }

    /// <summary>Where an entry approved by an admin is written: the AI pack's add-on file while that pack is on, otherwise the first enabled pack's. Always under App_Data (see <see cref="ContentDirectory"/>).</summary>
    public string WriteTargetFor(string file)
    {
        var first = EnabledIds.OrderBy(i => i.Equals(AiPackId, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(i => i, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(i => DirectoryOf(i) is not null) ?? AiPackId;
        return LocalFile(first, file);
    }

    /// <summary>Turns a pack on or off. Returns an error text, or null on success. At least one pack must stay on.</summary>
    public string? SetEnabled(string id, bool enabled)
    {
        var pack = Find(id);
        if (pack is null) return $"There is no pack called \"{id}\".";
        lock (_gate)
        {
            var on = _state.Enabled.Contains(pack.Id, StringComparer.OrdinalIgnoreCase);
            if (on == enabled) return null;
            if (enabled) _state.Enabled.Add(pack.Id);
            else
            {
                if (_state.Enabled.Count <= 1) return "At least one pack has to stay on.";
                _state.Enabled.RemoveAll(e => e.Equals(pack.Id, StringComparison.OrdinalIgnoreCase));
            }
            SaveState();
        }
        Changed?.Invoke();
        return null;
    }

    public void MarkApplied(string id)
    {
        lock (_gate)
        {
            if (_state.Applied.Contains(id, StringComparer.OrdinalIgnoreCase)) return;
            _state.Applied.Add(id);
            SaveState();
        }
    }

    /// <summary>Pack ids are folder names; keep them to a safe alphabet so an id can never address anything outside Data/packs.</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 40 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // ---- internals ----

    private PackInfo Describe(string id, string name, string description, string topic, string dir, bool builtin)
    {
        int Count(string file)
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) return 0;
            try { using var doc = JsonDocument.Parse(File.ReadAllText(path)); return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0; }
            catch (JsonException) { return 0; }
        }
        return new PackInfo(id, name, description, topic, builtin, Count("sources.json"), Count("glossary.json"), Count("tools.json"), Count("learning.json"), IsEnabled(id));
    }

    private static (string? Name, string? Description, string? Topic) ReadMeta(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "pack.json");
            if (!File.Exists(path)) return (null, null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string? S(string n) => doc.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (S("name"), S("description"), S("topic"));
        }
        catch (Exception e) when (e is JsonException or IOException) { return (null, null, null); }
    }

    private State LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var s = JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (s is { Enabled.Count: > 0 }) return s;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { /* fall back to the default below */ }
        return new State();
    }

    private void SaveState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
    }
}
