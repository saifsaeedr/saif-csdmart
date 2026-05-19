using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pins the narrow HtmlToPlainText helper used as the deepest fallback for
// the text part of the multipart activation email. The helper is NOT a
// general-purpose HTML parser — it assumes input is the activation HTML
// template (or an operator's override) — so these tests pin the small set
// of rules it actually implements rather than the long tail of HTML.
public class HtmlToPlainTextTests
{
    [Theory]
    [InlineData("<br>")]
    [InlineData("<br/>")]
    [InlineData("<br />")]
    [InlineData("<BR>")]
    [InlineData("<Br/>")]
    public void Br_Tag_Becomes_Newline_CaseInsensitive(string br)
    {
        HtmlToPlainText.Convert($"line1{br}line2").ShouldBe("line1\nline2");
    }

    [Theory]
    [InlineData("</p>")]
    [InlineData("</div>")]
    [InlineData("</li>")]
    [InlineData("</h1>")]
    [InlineData("</h6>")]
    [InlineData("</H3>")]
    public void Block_Closers_Become_Newlines(string close)
    {
        HtmlToPlainText.Convert($"first{close}second").ShouldBe("first\nsecond");
    }

    [Fact]
    public void Anchor_Text_Survives_With_Tags_Stripped()
    {
        HtmlToPlainText.Convert("Click <a href=\"https://x\">here</a> please")
            .ShouldBe("Click here please");
    }

    [Fact]
    public void Html_Entities_Are_Decoded()
    {
        HtmlToPlainText.Convert("Tom &amp; Jerry &lt;3 &quot;cheese&quot; &#39;crackers&#39;")
            .ShouldBe("Tom & Jerry <3 \"cheese\" 'crackers'");
    }

    [Fact]
    public void Three_Plus_Blank_Lines_Collapse_To_Two()
    {
        HtmlToPlainText.Convert("a\n\n\n\n\nb").ShouldBe("a\n\nb");
    }

    [Fact]
    public void Trailing_Whitespace_Per_Line_Is_Trimmed()
    {
        HtmlToPlainText.Convert("a   \nb\t\nc").ShouldBe("a\nb\nc");
    }

    [Fact]
    public void Overall_Output_Is_Trimmed()
    {
        HtmlToPlainText.Convert("\n\n<p>hello</p>\n\n").ShouldBe("hello");
    }

    [Fact]
    public void Empty_Input_Returns_Empty()
    {
        HtmlToPlainText.Convert("").ShouldBe(string.Empty);
    }

    [Fact]
    public void Full_Activation_Template_Renders_To_Readable_Text()
    {
        // Pin the end-to-end behavior on a representative input — the
        // structure of the embedded HTML template. Each <p> becomes its
        // own block, the link's anchor text survives, and the result has
        // no markup or entities.
        const string html =
            "<!DOCTYPE html><html><body>" +
            "<p>Hi Alice</p>" +
            "<p>MSISDN: +123</p>" +
            "<p>Username: alice</p>" +
            "<p>Welcome, we&#39;re happy to see you on board!</p>" +
            "<p>Activation Link:</p>" +
            "<a href=\"https://app/x\">https://app/x</a>" +
            "<p>Regards,</p>" +
            "</body></html>";

        var text = HtmlToPlainText.Convert(html);

        text.ShouldContain("Hi Alice");
        text.ShouldContain("MSISDN: +123");
        text.ShouldContain("Username: alice");
        text.ShouldContain("Welcome, we're happy to see you on board!");
        text.ShouldContain("https://app/x");
        text.ShouldContain("Regards,");
        text.ShouldNotContain("<");
        text.ShouldNotContain("&amp;");
        text.ShouldNotContain("&#39;");
    }
}
