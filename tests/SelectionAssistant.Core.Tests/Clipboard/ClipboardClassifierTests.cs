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
    // Original structured-code fixtures — all still hit the new structured rules.
    // `import os` was updated to `import os;` because the tightened rule needs
    // a separator after the dotted name (otherwise "import from overseas"
    // prose would also match).
    [InlineData("function foo() { return 1; }", ClipboardGroup.Code)]
    [InlineData("public class Bar { }", ClipboardGroup.Code)]
    [InlineData("import os;", ClipboardGroup.Code)]
    [InlineData("namespace MyApp", ClipboardGroup.Code)]
    [InlineData("def hello():", ClipboardGroup.Code)]
    public void Classify_Code(string text, ClipboardGroup expected)
    {
        Assert.Equal(expected, ClipboardClassifier.Classify(text));
    }

    /// <summary>
    /// Locks in the new structured Code rules added in batch 112 — each of
    /// these was NOT matched by the old bare-keyword regex either, or matched
    /// only incidentally. They exercise the new follower alternatives:
    /// PascalCase identifier, paren, terminator, assignment, modifier chain.
    /// </summary>
    [Theory]
    [InlineData("interface IComparable { }", ClipboardGroup.Code)]        // PascalCase identifier
    [InlineData("using System;", ClipboardGroup.Code)]                     // dotted name + ;
    [InlineData("using System.Collections.Generic;", ClipboardGroup.Code)] // dotted name + ;
    [InlineData("import java.util.Map;", ClipboardGroup.Code)]             // dotted name + ;
    [InlineData("package main;", ClipboardGroup.Code)]                     // name + ;
    [InlineData("var count = 0;", ClipboardGroup.Code)]                    // declaration + =
    [InlineData("let x = 5", ClipboardGroup.Code)]                         // declaration + =
    [InlineData("const PI = 3.14", ClipboardGroup.Code)]                   // declaration + =
    [InlineData("function add(a, b) {", ClipboardGroup.Code)]              // function + ( … )
    [InlineData("def greet(name):", ClipboardGroup.Code)]                  // def + name + (
    [InlineData("public static void Main()", ClipboardGroup.Code)]         // modifier chain
    [InlineData("private readonly Field _x;", ClipboardGroup.Code)]        // modifier chain
    [InlineData("protected override void Dispose()", ClipboardGroup.Code)] // modifier chain
    public void Classify_Code_StructuredRealCode(string text, ClipboardGroup expected)
    {
        Assert.Equal(expected, ClipboardClassifier.Classify(text));
    }

    /// <summary>
    /// The regression this batch fixes. Bare keywords are common English
    /// words, so noun-heavy prose (image-generation prompts being the
    /// canonical victim) was misclassified as Code. Each case below must now
    /// fall through to Text.
    /// </summary>
    [Theory]
    [InlineData("a private garden with roses")]                  // "private" alone
    [InlineData("students return home from class")]              // "return" + "class" alone
    [InlineData("let me think about it")]                        // "let" alone
    [InlineData("a quiet public park")]                          // "public" alone
    [InlineData("painting done by using a brush")]               // "using" alone
    [InlineData("import from overseas suppliers")]               // "import" + lowercase prose
    [InlineData("the package arrived damaged")]                  // "package" + lowercase prose
    [InlineData("first-class ticket to paris")]                  // "class" inside hyphenated word
    [InlineData("a private garden, students return home from class")] // combined: the user's actual case
    public void Classify_Code_NaturalLanguage_NotCode(string text)
    {
        Assert.Equal(ClipboardGroup.Text, ClipboardClassifier.Classify(text));
    }

    [Theory]
    // Original shell fixtures, kept. The first case was updated from
    // "sudo apt update" to "sudo rm -rf /tmp/old" because the tightened rule
    // no longer matches a bare two-token form (apt + update) — it needs a
    // flag, path, quote, or shell punctuation somewhere in the line.
    [InlineData("sudo rm -rf /tmp/old", ClipboardGroup.Shell)]
    [InlineData("git commit -m \"fix\"", ClipboardGroup.Shell)]
    [InlineData("chmod +x script.sh", ClipboardGroup.Shell)]
    [InlineData("mkdir -p newdir", ClipboardGroup.Shell)]
    public void Classify_Shell(string text, ClipboardGroup expected)
    {
        Assert.Equal(expected, ClipboardClassifier.Classify(text));
    }

    /// <summary>
    /// More shell fixtures covering the new lookahead alternatives: flag,
    /// path separator, quote, shell punctuation, and multi-token form.
    /// </summary>
    [Theory]
    [InlineData("curl \"https://example.com\"", ClipboardGroup.Shell)]   // quote
    [InlineData("cd /tmp/build", ClipboardGroup.Shell)]                   // path separator
    [InlineData("ls ./src", ClipboardGroup.Shell)]                        // path separator
    [InlineData("rm -rf node_modules", ClipboardGroup.Shell)]             // flag
    [InlineData("npm install --save-dev jest", ClipboardGroup.Shell)]     // flag + multi-token
    [InlineData("echo $PATH | grep bin", ClipboardGroup.Shell)]           // pipe
    [InlineData("dotnet publish -c Release", ClipboardGroup.Shell)]       // flag + multi-token
    public void Classify_Shell_StructuredCommands(string text, ClipboardGroup expected)
    {
        Assert.Equal(expected, ClipboardClassifier.Classify(text));
    }

    /// <summary>
    /// Bare command words are also common English words. The tightened
    /// lookahead requires a shell-shaped follower, so these prose fragments
    /// must now fall through to Text.
    /// </summary>
    [Theory]
    [InlineData("git workflow diagram")]            // "git" + single lowercase word
    [InlineData("cd changer for the car")]          // "cd" + single lowercase word
    [InlineData("mv award nominees announced")]     // "mv" + single lowercase word
    [InlineData("the echo chamber effect")]         // "echo" + single lowercase word
    public void Classify_Shell_NaturalLanguage_NotShell(string text)
    {
        Assert.Equal(ClipboardGroup.Text, ClipboardClassifier.Classify(text));
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

    /// <summary>
    /// Email addresses used to file as Contact. Contact was removed in batch
    /// 112 — emails now fall through to Text (they aren't links, JSON, code,
    /// shell, or pure numbers). This test pins the new behavior so a future
    /// re-add of Contact-aware classification has to consciously update it.
    /// (Pure-digit phone numbers like "13912345678" fall through to Number,
    /// which is correct and not Contact-specific — covered by Classify_Number.)
    /// </summary>
    [Theory]
    [InlineData("user@example.com")]
    [InlineData("name.surname@company.co.uk")]
    public void Classify_ContactRemoved_EmailsFallToText(string text)
    {
        Assert.Equal(ClipboardGroup.Text, ClipboardClassifier.Classify(text));
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
