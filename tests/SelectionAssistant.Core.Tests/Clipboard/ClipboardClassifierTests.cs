using SelectionAssistant.Core.Clipboard;
using Xunit;

namespace SelectionAssistant.Core.Tests.Clipboard;

public sealed class ClipboardClassifierTests
{
    [Theory]
    [InlineData("https://example.com/path?q=1", ClipboardGroup.Link)]
    [InlineData("http://localhost:8080", ClipboardGroup.Link)]
    [InlineData("ftp://server/file", ClipboardGroup.Link)]
    [InlineData("www.example.com", ClipboardGroup.Link)]
    public void Classify_Links(string text, ClipboardGroup expected)
    {
        Assert.Equal(expected, ClipboardClassifier.Classify(text));
    }

    [Theory]
    [InlineData("{\"name\": \"value\", \"n\": 1}", ClipboardGroup.Json)]
    [InlineData("[1, 2, 3]", ClipboardGroup.Json)]
    [InlineData("{\"a\": {\"b\": [1, 2]}}", ClipboardGroup.Json)]
    public void Classify_Json(string text, ClipboardGroup expected)
    {
        Assert.Equal(expected, ClipboardClassifier.Classify(text));
    }

    [Theory]
    [InlineData("function foo() { return 1; }", ClipboardGroup.Code)]
    [InlineData("public class Bar { }", ClipboardGroup.Code)]
    [InlineData("import os", ClipboardGroup.Code)]
    [InlineData("namespace MyApp", ClipboardGroup.Code)]
    [InlineData("def hello():", ClipboardGroup.Code)]
    public void Classify_Code(string text, ClipboardGroup expected)
    {
        Assert.Equal(expected, ClipboardClassifier.Classify(text));
    }

    [Theory]
    [InlineData("sudo apt update", ClipboardGroup.Shell)]
    [InlineData("git commit -m \"fix\"", ClipboardGroup.Shell)]
    [InlineData("chmod +x script.sh", ClipboardGroup.Shell)]
    [InlineData("mkdir newdir", ClipboardGroup.Shell)]
    public void Classify_Shell(string text, ClipboardGroup expected)
    {
        Assert.Equal(expected, ClipboardClassifier.Classify(text));
    }

    [Theory]
    [InlineData("user@example.com", ClipboardGroup.Contact)]
    [InlineData("name.surname@company.co.uk", ClipboardGroup.Contact)]
    [InlineData("13912345678", ClipboardGroup.Contact)]
    public void Classify_Contact(string text, ClipboardGroup expected)
    {
        Assert.Equal(expected, ClipboardClassifier.Classify(text));
    }

    [Theory]
    [InlineData("12345", ClipboardGroup.Number)]
    [InlineData("3.14", ClipboardGroup.Number)]
    [InlineData("1,234.56", ClipboardGroup.Number)]
    [InlineData("-42", ClipboardGroup.Number)]
    public void Classify_Number(string text, ClipboardGroup expected)
    {
        Assert.Equal(expected, ClipboardClassifier.Classify(text));
    }

    [Theory]
    [InlineData("Hello world", ClipboardGroup.Text)]
    [InlineData("这是一个普通句子", ClipboardGroup.Text)]
    [InlineData("", ClipboardGroup.Text)]
    [InlineData("   ", ClipboardGroup.Text)]
    public void Classify_Text(string text, ClipboardGroup expected)
    {
        Assert.Equal(expected, ClipboardClassifier.Classify(text));
    }

    [Theory]
    [InlineData("api_key=abc123")]
    [InlineData("my secret is hidden")]
    [InlineData("password: hunter2")]
    [InlineData("token=eyJhbGci...")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("private_key-----BEGIN")]
    [InlineData("Bearer abc123")]
    public void Classify_Sensitive_HighestPriority(string text)
    {
        // Sensitive must win over everything — a token that also looks like a
        // link or contains code keywords must still be filed Sensitive.
        Assert.Equal(ClipboardGroup.Sensitive, ClipboardClassifier.Classify(text));
        Assert.True(ClipboardClassifier.IsSensitive(text));
    }

    [Fact]
    public void Classify_Sensitive_BeatsLinkShape()
    {
        // Looks like a link but contains "token" — must be Sensitive, not Link.
        Assert.Equal(ClipboardGroup.Sensitive,
            ClipboardClassifier.Classify("https://api.example.com/token=secret"));
    }

    [Fact]
    public void IsSensitive_PlainText_False()
    {
        Assert.False(ClipboardClassifier.IsSensitive("just some text"));
        Assert.False(ClipboardClassifier.IsSensitive(""));
        Assert.False(ClipboardClassifier.IsSensitive(null));
    }

    [Fact]
    public void Classify_MalformedBraces_NotJson()
    {
        // Starts with { but doesn't parse → falls through to Text.
        Assert.Equal(ClipboardGroup.Text, ClipboardClassifier.Classify("{ not json"));
    }
}
