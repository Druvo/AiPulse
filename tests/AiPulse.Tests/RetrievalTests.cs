using AiPulse.Models;
using AiPulse.Services.Ai.Rag;
using Xunit;

namespace AiPulse.Tests;

public class TokenizerTests
{
    [Fact]
    public void Lowercases_drops_stop_words_and_stems_plurals()
    {
        Assert.Equal(new[] { "agent", "tool" }, Tokenizer.Tokens("What are the Agents and Tools?"));
    }

    [Theory]
    [InlineData("C# generics", "csharp")]
    [InlineData("the .NET runtime", "dotnet")]
    [InlineData("C++ templates", "cpp")]
    [InlineData("Node.js servers", "nodejs")]
    public void Keeps_language_names_that_punctuation_would_destroy(string text, string expected) =>
        Assert.Contains(expected, Tokenizer.Tokens(text));

    [Theory]
    [InlineData("embeddings", "embedd")]
    [InlineData("queries", "query")]
    [InlineData("cached", "cach")]
    [InlineData("rag", "rag")]
    [InlineData("class", "class")]
    public void Stem_is_light_and_consistent(string word, string stem) => Assert.Equal(stem, Tokenizer.Stem(word));

    [Fact]
    public void Query_and_document_forms_of_a_word_meet()
    {
        Assert.Equal(Tokenizer.Tokens("caching")[0], Tokenizer.Tokens("cached")[0]);
        Assert.Equal(Tokenizer.Tokens("embedding")[0], Tokenizer.Tokens("embeddings")[0]);
    }

    [Fact]
    public void Non_latin_text_is_tokenised_not_discarded() =>
        Assert.NotEmpty(Tokenizer.Tokens("ঢাকা বাংলাদেশ"));

    [Fact]
    public void Empty_input_gives_no_tokens() => Assert.Empty(Tokenizer.Tokens("  "));
}

public class Bm25IndexTests
{
    private static Bm25Index Index(params KnowledgeDocument[] docs) => Bm25Index.Build(docs);

    [Fact]
    public void Ranks_the_document_that_is_about_the_query_first()
    {
        var index = Index(
            TestData.Doc("a", "Vector database", "Stores embeddings and finds nearest neighbours."),
            TestData.Doc("b", "Quantization", "Shrinks model precision to run on less memory."),
            TestData.Doc("c", "Prompt caching", "Reuses a processed prompt prefix."));

        Assert.Equal("b", index.Search("how does quantization shrink precision", 3)[0].Doc.Id);
    }

    [Fact]
    public void A_title_match_outranks_a_body_mention()
    {
        var index = Index(
            TestData.Doc("body", "Something else", "Aider is mentioned once in passing here."),
            TestData.Doc("title", "Aider", "A terminal coding agent."));

        Assert.Equal("title", index.Search("aider", 2)[0].Doc.Id);
    }

    [Fact]
    public void Keywords_such_as_aliases_make_a_document_findable()
    {
        var index = Index(
            TestData.Doc("q", "Quantization", "Shrinks precision.", keywords: "GGUF 4-bit"),
            TestData.Doc("x", "Other", "Unrelated text."));

        Assert.Equal("q", index.Search("gguf", 2)[0].Doc.Id);
    }

    [Fact]
    public void Coverage_is_the_share_of_query_words_matched()
    {
        var index = Index(TestData.Doc("a", "Token", "A unit of text a model reads."));

        var hit = Assert.Single(index.Search("token pineapple", 5));
        Assert.Equal(0.5, hit.Coverage, 3);
        Assert.Equal(new[] { "token" }, hit.MatchedTerms);
    }

    [Fact]
    public void A_query_of_only_stop_words_matches_nothing() =>
        Assert.Empty(Index(TestData.Doc("a", "The", "the and of")).Search("what is the", 5));

    [Fact]
    public void Recent_news_gets_a_small_lift_over_equally_relevant_old_news()
    {
        var now = DateTimeOffset.UtcNow;
        var index = Index(
            TestData.Doc("old", "Claude release", "Anthropic shipped a Claude release.", "News", published: now.AddDays(-200)),
            TestData.Doc("new", "Claude release", "Anthropic shipped a Claude release.", "News", published: now.AddDays(-1)));

        Assert.Equal("new", index.Search("claude release", 2, now)[0].Doc.Id);
    }

    [Fact]
    public void Empty_index_returns_nothing() => Assert.Empty(Index().Search("anything", 5));
}

public class KnowledgeCorpusTests
{
    [Fact]
    public void Search_drops_passages_that_match_too_little_of_the_question()
    {
        var corpus = TestData.CorpusOf(TestData.Doc("a", "Token", "A unit of text."));

        Assert.Empty(corpus.Search("token pineapple orchard weather", 5, minCoverage: 0.5));
        Assert.Single(corpus.Search("token", 5, minCoverage: 0.5));
    }

    [Fact]
    public void Index_is_built_once_and_refreshed_in_the_background_after_it_expires()
    {
        var builds = 0;
        var corpus = new KnowledgeCorpus(() =>
        {
            builds++;
            return new[] { TestData.Doc("a", "Token", $"build {builds}") };
        }, TimeSpan.FromMilliseconds(400));

        _ = corpus.Stats;
        _ = corpus.Search("token", 3, 0);
        Assert.Equal(1, builds);

        Thread.Sleep(600);
        _ = corpus.Stats; // stale: answers from the old index and triggers a rebuild
        SpinWait.SpinUntil(() => builds >= 2, TimeSpan.FromSeconds(5));
        Assert.True(builds >= 2);
    }

    [Fact]
    public void A_failing_refresh_keeps_serving_the_previous_index_and_reports_the_error()
    {
        var fail = false;
        Exception? reported = null;
        var corpus = new KnowledgeCorpus(() =>
        {
            if (fail) throw new InvalidOperationException("db down");
            return new[] { TestData.Doc("a", "Token", "A unit of text.") };
        }, TimeSpan.FromMilliseconds(150), ex => reported = ex);

        Assert.Equal(1, corpus.Stats.Documents);
        fail = true;
        Thread.Sleep(300);
        _ = corpus.Stats;

        SpinWait.SpinUntil(() => reported is not null, TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(reported);
        Assert.Single(corpus.Search("token", 3, 0));
    }
}

public class KnowledgeCorpusBuilderTests
{
    private static readonly KnowledgeInputs Empty = new(
        Array.Empty<GlossaryTerm>(), Array.Empty<ToolEntry>(), Array.Empty<PracticeTip>(), Array.Empty<LearningModule>(),
        Array.Empty<BenchmarkEntry>(), Array.Empty<CourseEntry>(), Array.Empty<ModelDirectoryEntry>(), Array.Empty<FeedItem>());

    [Fact]
    public void Builds_stable_ids_for_the_real_knowledge_base()
    {
        var docs = KnowledgeCorpusBuilder.Build(TestData.RealInputs());

        Assert.Contains(docs, d => d.Id == "glossary:rag");
        Assert.Contains(docs, d => d.Id == "tool:ollama");
        Assert.Equal(docs.Count, docs.Select(d => d.Id).Distinct().Count());
        Assert.All(docs, d => Assert.False(string.IsNullOrWhiteSpace(d.Text)));
    }

    [Fact]
    public void Glossary_aliases_become_searchable_keywords()
    {
        var term = new GlossaryTerm { Term = "Quantization", Category = "Concept", ShortDef = "Shrinks precision.", Aliases = new[] { "GGUF" } };
        var doc = Assert.Single(KnowledgeCorpusBuilder.Build(Empty with { Glossary = new[] { term } }));

        Assert.Equal("GGUF", doc.Keywords);
        Assert.Equal("/glossary?q=Quantization", doc.Url);
    }

    [Fact]
    public void News_text_is_cleaned_and_carries_the_date_in_both_forms_the_answer_may_use()
    {
        var item = new FeedItem
        {
            Title = "Model launches",
            Link = "https://example.com/post?a=1",
            Summary = "<p>It costs <b>$5</b> &amp; ships soon.</p>",
            Published = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero),
            SourceName = "Example",
            Category = "News"
        };

        var doc = Assert.Single(KnowledgeCorpusBuilder.Build(Empty with { News = new[] { item } }));

        Assert.Equal("News", doc.Kind);
        Assert.DoesNotContain("<", doc.Text);
        Assert.Contains("& ships soon", doc.Text);
        Assert.Contains("7 October 2026", doc.Text);
        Assert.Contains("2026-10-07", doc.Text);
        Assert.Equal(item.Link, doc.Url);
    }

    [Fact]
    public void News_items_without_a_link_are_skipped()
    {
        var item = new FeedItem { Title = "x", Link = "", SourceName = "s", Category = "News" };
        Assert.Empty(KnowledgeCorpusBuilder.Build(Empty with { News = new[] { item } }));
    }

    [Fact]
    public void Long_summaries_are_clipped()
    {
        var item = new FeedItem { Title = "Long", Link = "https://e.com/1", Summary = new string('x', 5000), SourceName = "s", Category = "News" };
        var doc = Assert.Single(KnowledgeCorpusBuilder.Build(Empty with { News = new[] { item } }));
        Assert.True(doc.Text.Length < 1500);
    }
}
