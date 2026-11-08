using AiPulse.Services.Productivity;
using Xunit;

namespace AiPulse.Tests;

public class ReaderSanitizerTests
{
    private static string S(string html, string? baseUrl = "https://blog.example.test/posts/1") => ReaderSanitizer.Sanitize(html, baseUrl);

    [Fact]
    public void Scripts_styles_and_embeds_are_removed_with_their_contents()
    {
        var html = S("<p>Hi</p><script>alert(1)</script><style>p{}</style><iframe src='https://evil.test'></iframe><object>x</object><svg><script>1</script></svg><noscript>n</noscript>");
        Assert.Equal("<p>Hi</p>", html);
    }

    [Fact]
    public void Event_handlers_style_class_and_id_attributes_are_dropped()
    {
        var html = S("<p onclick=\"steal()\" style=\"color:red\" class=\"x\" id=\"y\">Text</p><img src=\"/a.png\" onerror=\"steal()\">");
        Assert.Equal("<p>Text</p><img src=\"https://blog.example.test/a.png\" alt=\"\" loading=\"lazy\" referrerpolicy=\"no-referrer\">", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("jav\tascript:alert(1)")]
    [InlineData("&#106;avascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vbscript:msgbox")]
    [InlineData("#top")]
    public void Unsafe_or_useless_links_lose_the_anchor_but_keep_the_text(string href)
    {
        Assert.Equal("<p>click me</p>", S($"<p><a href=\"{href}\">click me</a></p>"));
    }

    [Fact]
    public void Links_are_resolved_and_get_noopener_nofollow_in_a_new_tab()
    {
        var html = S("<a href=\"../about\">About</a>");
        Assert.Equal("<a href=\"https://blog.example.test/about\" target=\"_blank\" rel=\"noopener nofollow noreferrer\">About</a>", html);
    }

    [Fact]
    public void Mailto_links_are_allowed_but_not_for_images()
    {
        Assert.Contains("href=\"mailto:a@b.test\"", S("<a href=\"mailto:a@b.test\">mail</a>"));
        Assert.Equal("", S("<img src=\"mailto:a@b.test\">"));
        Assert.Equal("", S("<img src=\"data:image/png;base64,AAAA\">"));
    }

    [Fact]
    public void Unknown_tags_are_unwrapped_and_h1_becomes_h2()
    {
        Assert.Equal("a\nb", S("<div>a</div><div>b</div>"));
        Assert.Equal("<h2>Title</h2><p>a <strong>b</strong></p>", S("<h1>Title</h1><div><p>a <custom-el><strong>b</strong></custom-el></p></div>"));
    }

    [Fact]
    public void Text_is_re_encoded_so_nothing_can_break_out()
    {
        var html = S("<p>&lt;script&gt;alert(1)&lt;/script&gt; &amp; 5 &gt; 3</p>");
        Assert.Equal("<p>&lt;script&gt;alert(1)&lt;/script&gt; &amp; 5 &gt; 3</p>", html);
        Assert.DoesNotContain("<script", S("<p>x</p><<script>script>alert(1)<</script>/script>"));
    }

    [Fact]
    public void Comments_and_form_controls_are_removed()
    {
        Assert.Equal("<p>a</p>", S("<p>a<!-- secret --></p><form action=\"/x\"><input name=\"q\"><button>Go</button></form>"));
    }

    [Fact]
    public void Table_cells_keep_sane_spans_only()
    {
        Assert.Equal("<table><tr><td colspan=\"2\">x</td><td>y</td></tr></table>", S("<table><tr><td colspan=\"2\" rowspan=\"9999\">x</td><td colspan=\"-1\">y</td></tr></table>"));
    }

    [Fact]
    public void Attribute_values_are_encoded()
    {
        var html = S("<img src=\"https://x.test/a.png\" alt='\" onload=\"x'>");
        Assert.Contains("alt=\"&quot; onload=&quot;x\"", html);
    }

    [Fact]
    public void Protocol_relative_and_relative_without_base_are_handled()
    {
        Assert.Contains("href=\"https://cdn.example.test/x\"", S("<a href=\"//cdn.example.test/x\">x</a>"));
        Assert.Equal("x", ReaderSanitizer.Sanitize("<a href=\"/relative\">x</a>", null));
    }

    [Fact]
    public void Empty_input_gives_empty_output()
    {
        Assert.Equal("", ReaderSanitizer.Sanitize(null));
        Assert.Equal("", ReaderSanitizer.Sanitize("   "));
    }

    [Fact]
    public void Plain_text_becomes_encoded_paragraphs()
    {
        Assert.Equal("<p>One &lt;b&gt;</p><p>Two<br>lines</p>", ReaderSanitizer.ParagraphsFromText("One <b>\n\nTwo\nlines"));
    }

    [Fact]
    public void Word_count_and_reading_time()
    {
        Assert.Equal(3, ReaderSanitizer.WordCount("<p>one <b>two</b></p><p>three</p>"));
        Assert.Equal(0, ReaderSanitizer.ReadingMinutes(0));
        Assert.Equal(1, ReaderSanitizer.ReadingMinutes(5));
        Assert.Equal(2, ReaderSanitizer.ReadingMinutes(231));
    }

    [Fact]
    public void Main_content_extraction_prefers_the_article_element()
    {
        var page = "<html><head><title>t</title><script>x()</script></head><body><nav>Menu</nav><article><h1>Head</h1><p>Body text</p></article><footer>f</footer></body></html>";
        Assert.Equal("<h2>Head</h2><p>Body text</p>", ReaderService.ExtractMainHtml(page, "https://x.test/a"));
    }

    [Theory]
    [InlineData("http://localhost/x", true)]
    [InlineData("http://127.0.0.1/x", true)]
    [InlineData("http://10.1.2.3/x", true)]
    [InlineData("http://192.168.0.5/x", true)]
    [InlineData("http://172.20.0.1/x", true)]
    [InlineData("http://169.254.169.254/latest", true)]
    [InlineData("http://[::1]/x", true)]
    [InlineData("http://app.localhost/x", true)]
    [InlineData("https://example.com/x", false)]
    [InlineData("http://8.8.8.8/x", false)]
    public void Local_targets_are_never_fetched(string url, bool local)
    {
        Assert.Equal(local, ReaderService.IsLocalTarget(new Uri(url)));
    }

    [Fact]
    public void Feed_full_text_is_preferred_over_the_summary()
    {
        var item = Items.Make("t", summary: "<p>short</p>", fullText: "A much longer body of full text.\n\nSecond paragraph here.");
        var article = ReaderService.BuildFromFeed(item);
        Assert.Equal(ReaderBodySource.FeedFullText, article.Source);
        Assert.StartsWith("<p>A much longer", article.BodyHtml);
    }

    [Fact]
    public void Html_summary_is_sanitised_when_there_is_no_full_text()
    {
        var article = ReaderService.BuildFromFeed(Items.Make("t", summary: "<p onclick='x()'>Hello <script>bad()</script>world</p>"));
        Assert.Equal(ReaderBodySource.Summary, article.Source);
        Assert.Equal("<p>Hello world</p>", article.BodyHtml);
        Assert.Equal(2, article.Words);
        Assert.Equal(1, article.Minutes);
    }
}
