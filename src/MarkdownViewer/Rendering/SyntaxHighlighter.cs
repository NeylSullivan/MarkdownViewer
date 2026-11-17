using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;

namespace MarkdownViewer.Rendering;

internal static class SyntaxHighlighter
{
    private static readonly Regex CodeTokens = new(
        "(?<comment>//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/)|(?<string>@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|`(?:\\\\.|[^`\\\\])*`)|(?<number>\\b(?:0x[0-9a-fA-F]+|[0-9]+(?:\\.[0-9]+)?)\\b)|(?<keyword>\\b(?:abstract|as|async|await|base|bool|break|case|catch|char|class|const|continue|decimal|default|delete|do|double|else|enum|export|extends|false|final|finally|float|for|foreach|from|function|if|implements|import|in|int|interface|internal|is|let|long|namespace|new|null|object|out|override|package|private|protected|public|readonly|record|return|sealed|short|static|string|struct|super|switch|this|throw|true|try|typeof|using|var|virtual|void|while|yield)\\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));

    private static readonly Regex ScriptTokens = new(
        "(?<comment>#[^\\r\\n]*)|(?<string>\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*')|(?<number>\\b[0-9]+(?:\\.[0-9]+)?\\b)|(?<keyword>\\b(?:and|as|assert|async|await|break|case|class|continue|def|do|done|elif|else|esac|except|False|fi|finally|for|from|function|if|import|in|is|lambda|local|match|None|not|or|pass|raise|return|then|True|try|while|with|yield|param|foreach|Write-Host|Write-Output)\\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));

    private static readonly Regex DataTokens = new(
        "(?<string>\"(?:\\\\.|[^\"\\\\])*\")|(?<number>\\b-?[0-9]+(?:\\.[0-9]+)?\\b)|(?<keyword>\\b(?:true|false|null)\\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));

    public static IReadOnlyList<Inline> CreateInlines(string text, string language, bool dark)
    {
        var regex = language.ToLowerInvariant() switch
        {
            "cs" or "csharp" or "c#" or "js" or "javascript" or "ts" or "typescript" or "java" or "c" or "cpp" or "c++" or "go" or "rust" or "rs" => CodeTokens,
            "python" or "py" or "bash" or "sh" or "shell" or "powershell" or "ps1" => ScriptTokens,
            "json" => DataTokens,
            _ => null
        };
        if (regex is null || text.Length > 250_000)
            return [new Run(text)];

        var result = new List<Inline>();
        try
        {
            var offset = 0;
            foreach (Match match in regex.Matches(text))
            {
                if (match.Index > offset)
                    result.Add(new Run(text[offset..match.Index]));
                var color = match.Groups["comment"].Success ? (dark ? "#90A87D" : "#52723D")
                    : match.Groups["string"].Success ? (dark ? "#DCA88C" : "#A63E24")
                    : match.Groups["number"].Success ? (dark ? "#B2CDA7" : "#246C4F")
                    : (dark ? "#90BEF0" : "#245BA9");
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                brush.Freeze();
                result.Add(new Run(match.Value) { Foreground = brush });
                offset = match.Index + match.Length;
            }
            if (offset < text.Length)
                result.Add(new Run(text[offset..]));
            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            return [new Run(text)];
        }
    }
}
