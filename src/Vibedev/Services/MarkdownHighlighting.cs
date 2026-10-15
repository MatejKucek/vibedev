using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Vibedev.Services;

internal static class MarkdownHighlighting
{
    internal static IHighlightingDefinition Definition { get; } = Create();
    private static IHighlightingDefinition Create()
    {
        using var reader = XmlReader.Create(new StringReader("""
            <SyntaxDefinition name="Markdown" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Heading" foreground="#DDD6FE" fontWeight="bold" />
              <Color name="Code" foreground="#CEB5E8" />
              <Color name="Quote" foreground="#9898A6" />
              <Color name="Link" foreground="#8AB4F8" />
              <Color name="Strong" foreground="#E4E4E7" fontWeight="bold" />
              <Color name="Task" foreground="#A78BFA" />
              <RuleSet>
                <Span color="Code" multiline="true"><Begin>^\s*```.*$</Begin><End>^\s*```\s*$</End></Span>
                <Span color="Code" multiline="true"><Begin>^\s*~~~.*$</Begin><End>^\s*~~~\s*$</End></Span>
                <Rule color="Heading">^\s{0,3}\#{1,6}\s.*$</Rule>
                <Rule color="Quote">^\s{0,3}&gt;.*$</Rule>
                <Rule color="Link">\[[^\]\r\n]+\]\([^\)\r\n]+\)</Rule>
                <Rule color="Task">\[[ xX]\]</Rule>
                <Span color="Code" begin="`" end="`" />
                <Span color="Strong" begin="\*\*" end="\*\*" />
              </RuleSet>
            </SyntaxDefinition>
            """));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
