using WWDS.ModCore.Serialization;
using Xunit;

namespace WWDS.Tests.Core;

public class JsonTests
{
    private const char Bs = '\\';

    [Fact]
    public void Parse_DecodesEscapesIncludingSurrogatePairs()
    {
        // "Größe 😀 \"q\" a\\b \/" built without escape sequences in source.
        var json = "\"Gr" + Bs + "u00f6" + Bs + "u00dfe " + Bs + "ud83d" + Bs + "ude00 " + Bs + "\"q" + Bs + "\" a" + Bs + Bs + "b " + Bs + "/\"";
        var s = (string)Json.Parse(json)!;
        Assert.Equal("Gr" + (char)0xF6 + (char)0xDF + "e " + char.ConvertFromUtf32(0x1F600) + " \"q\" a" + Bs + "b /", s);
    }

    [Fact]
    public void Write_EscapesControlCharacters_SoOutputStaysOneLine()
    {
        var text = Json.Write(new Dictionary<string, object?> { ["t"] = "line1\nline2\t" + (char)1 + "\"" });
        Assert.DoesNotContain("\n", text);
        Assert.Equal("{\"t\":\"line1" + Bs + "nline2" + Bs + "t" + Bs + "u0001" + Bs + "\"\"}", text);
        Assert.Equal("line1\nline2\t" + (char)1 + "\"", ((Dictionary<string, object?>)Json.Parse(text)!)["t"]);
    }

    [Theory]
    [InlineData("{\"a\":1,}")]
    [InlineData("{\"a\" 1}")]
    [InlineData("[1,2")]
    [InlineData("\"unterminated")]
    [InlineData("{\"a\":1} trailing")]
    [InlineData("tru")]
    [InlineData("01x")]
    [InlineData("1e999")]
    [InlineData("{'a':1}")]
    public void Parse_RejectsMalformedInput(string json) => Assert.Throws<FormatException>(() => Json.Parse(json));

    [Fact]
    public void Parse_LimitsNestingDepth()
    {
        var deep = new string('[', Json.MaxDepth + 2) + new string(']', Json.MaxDepth + 2);
        Assert.Throws<FormatException>(() => Json.Parse(deep));
        var ok = new string('[', 10) + new string(']', 10);
        Assert.NotNull(Json.Parse(ok));
    }

    private enum Color
    {
        Red,
        Green,
    }

    private sealed class Sample
    {
        public string Name { get; set; } = string.Empty;

        public int Count { get; set; }

        public Color Color { get; set; }

        public double? Ratio { get; set; }

        public List<Sample> Children { get; set; } = new();

        public Dictionary<string, int> Scores { get; set; } = new();

        public string ReadOnly => "ignored";
    }

    [Fact]
    public void Mapper_HandlesNestedListsDictionariesEnumsNullables_AndIgnoresUnknownFields()
    {
        var s = Json.Deserialize<Sample>("{\"name\":\"root\",\"count\":3,\"color\":\"green\",\"ratio\":null,\"extra\":[1,2],"
            + "\"children\":[{\"name\":\"kid\",\"color\":0}],\"scores\":{\"a\":5}}");

        Assert.Equal(("root", 3, Color.Green, (double?)null), (s.Name, s.Count, s.Color, s.Ratio));
        Assert.Equal(("kid", Color.Red), (s.Children[0].Name, s.Children[0].Color));
        Assert.Equal(5, s.Scores["a"]);
        Assert.DoesNotContain("readOnly", Json.Write(s));
    }

    [Theory]
    [InlineData("{\"count\":\"three\"}")]
    [InlineData("{\"count\":3000000000}")]
    [InlineData("{\"children\":{}}")]
    [InlineData("{\"name\":5}")]
    public void Mapper_RejectsWrongTypes(string json) => Assert.Throws<FormatException>(() => Json.Deserialize<Sample>(json));
}
