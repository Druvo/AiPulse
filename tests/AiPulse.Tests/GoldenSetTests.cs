using AiPulse.Services.Ai;
using AiPulse.Services.Ai.Evals;
using Xunit;
using Xunit.Abstractions;

namespace AiPulse.Tests;

/// <summary>Runs evals/golden.json through the real pipeline against the real knowledge base. A regression in retrieval, guardrails or verification fails here.</summary>
public class GoldenSetTests
{
    private readonly ITestOutputHelper _output;

    public GoldenSetTests(ITestOutputHelper output) => _output = output;

    private static string GoldenPath => Path.Combine(TestData.RepoRoot, "evals", "golden.json");

    [Fact]
    public void The_golden_file_is_well_formed()
    {
        var suite = EvalSuite.Load(GoldenPath);

        Assert.NotEmpty(suite.Cases);
        Assert.Equal(suite.Cases.Count, suite.Cases.Select(c => c.Id).Distinct().Count());
        Assert.All(suite.Cases, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Id));
            Assert.False(string.IsNullOrWhiteSpace(c.Category));
            Assert.True(
                c.ExpectSources is { Count: > 0 } || c.ExpectTop is not null || c.ExpectStatus is not null || c.ExpectNotStatus is not null
                || c.ExpectContains is not null || c.ExpectAbsent is not null || c.ExpectDroppedMin is not null || c.ExpectDroppedMax is not null,
                $"case {c.Id} asserts nothing");
        });
    }

    [Fact]
    public async Task Every_category_meets_its_threshold()
    {
        var suite = EvalSuite.Load(GoldenPath);
        var runner = new EvalRunner(TestData.RealCorpus(), new AiOptions());

        var report = await runner.RunAsync(suite);

        foreach (var c in report.Categories)
            _output.WriteLine($"{c.Category,-10} {c.Passed}/{c.Total}  ({c.Rate:P0}, needs {c.Threshold:P0})");

        var detail = string.Join(Environment.NewLine, report.Failures.Select(f => $"  [{f.Category}] {f.CaseId}: {f.Reason}"));
        Assert.True(report.Met, "Golden-set categories below threshold:" + Environment.NewLine + detail);
    }

    [Fact]
    public async Task The_runner_reports_a_deliberately_wrong_expectation_as_a_failure()
    {
        var suite = new EvalSuite
        {
            Cases =
            {
                new EvalCase { Id = "wrong", Category = "retrieval", Question = "What is RAG?", ExpectTop = "glossary:quantization" },
                new EvalCase { Id = "right", Category = "retrieval", Question = "What is RAG?", ExpectTop = "glossary:rag" }
            },
            Thresholds = { ["retrieval"] = 1.0 }
        };

        var report = await new EvalRunner(TestData.RealCorpus(), new AiOptions()).RunAsync(suite);

        Assert.False(report.Met);
        var failure = Assert.Single(report.Failures);
        Assert.Equal("wrong", failure.CaseId);
        Assert.Equal(1, report.Categories.Single().Passed);
    }
}
