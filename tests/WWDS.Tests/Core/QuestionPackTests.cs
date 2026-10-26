using System.Linq;
using System.Text;
using WWDS.ModCore.Content;
using Xunit;

namespace WWDS.Tests.Core;

public class QuestionPackTests
{
    private static string Csv(int boards, string extra = "")
    {
        var sb = new StringBuilder("Typ;Kategorie;Frage;A;B;C;Richtig;Video;Reaktion\n");
        for (var i = 1; i <= boards; i++) sb.Append("Frage;Kat ").Append(i).Append(";Frage ").Append(i).Append("?;a;b;c;B;;\n");
        sb.Append("Finale;Geo;Hauptstadt?;x;y;z;1;;\n");
        sb.Append("Finale;Geo;Fluss?;x;y;z;3;;\n");
        sb.Append("Schätzfrage;;Wie viele?;;;;1234;;\n");
        return sb + extra;
    }

    [Fact]
    public void ExcelCsv_InWindows1252_WithQuotedLineBreaks_ParsesIntoGameText()
    {
        var text = "Typ;Kategorie;Frage;A;B;C;Richtig\r\nFrage;\"Große\nTiere\";\"Wer sagt \"\"Muh\"\"?\";Kuh;Hund;Käse;a\r\n";
        var bytes = Encoding.Latin1.GetBytes(text);
        var pack = new QuestionPack();
        pack.ReadCsv(QuestionPack.Decode(bytes));
        var q = Assert.Single(pack.Questions);
        Assert.Equal("Große\\nTiere", q.Category);
        Assert.Equal("Wer sagt \"Muh\"?", q.Question);
        Assert.Equal(new[] { "Kuh", "Hund", "Käse" }, q.Answers);
        Assert.Equal(1, q.Correct);
    }

    [Fact]
    public void FourteenQuestions_GiveTwoShows_TheSecondFilledUpFromTheStart_FinalsInTurn()
    {
        var pack = new QuestionPack();
        pack.ReadCsv(Csv(14));
        pack.Finish();
        Assert.True(pack.Playable, string.Join("; ", pack.Errors));
        Assert.Equal(2, pack.ShowCount);
        var second = pack.Show(1);
        Assert.Equal(new[] { "Frage 13?", "Frage 14?", "Frage 1?" }, second.Board.Take(3).Select(q => q.Question));
        Assert.Equal("Fluss?", second.Master.Question);
        Assert.Equal(1234, second.Estimate.Correct);
        Assert.Contains(pack.Warnings, w => w.Contains("filled up"));
    }

    [Fact]
    public void BrokenPack_ListsEveryProblem_AndIsNotPlayable()
    {
        var pack = new QuestionPack();
        pack.ReadCsv("Typ;Kategorie;Frage;A;B;C;Richtig;Video\nFrage;Tiere;Was?;a;b;c;D;clip.avi\nQuiz;X;Y;a;b;c;A;\n");
        pack.Finish();
        Assert.False(pack.Playable);
        Assert.Contains(pack.Errors, e => e.Contains("line 2") && e.Contains("\"D\""));
        Assert.Contains(pack.Errors, e => e.Contains("line 2") && e.Contains(".mp4"));
        Assert.Contains(pack.Errors, e => e.Contains("line 3") && e.Contains("Quiz"));
        Assert.Contains(pack.Errors, e => e.Contains("Schaetzfrage"));
    }

    [Fact]
    public void Json_AcceptsLetterOrNumber_AndRealLineBreaks()
    {
        var pack = new QuestionPack();
        pack.ReadJson("{\"title\":\"Abend\",\"questions\":[{\"category\":\"Sport\",\"topic\":\"sport\",\"question\":\"Zeile 1\\nZeile 2\",\"answers\":[\"a\",\"b\",\"c\"],\"correct\":\"C\",\"video\":\"videos\\\\q1.mp4\",\"reaction\":8}],"
            + "\"master\":[{\"category\":\"M\",\"question\":\"Q\",\"answers\":[\"a\",\"b\",\"c\"],\"correct\":2}],\"estimate\":[{\"question\":\"E\",\"answer\":42.6}]}");
        Assert.Empty(pack.Errors);
        var q = pack.Questions[0];
        Assert.Equal(("Abend", "Zeile 1\\nZeile 2", 3d, "Sport", "videos/q1.mp4", 8), (pack.Title, q.Question, q.Correct, q.Topic, q.Video, q.Reaction));
        Assert.Equal(2d, pack.Questions[1].Correct);
        Assert.Equal(43d, pack.Questions[2].Correct);
    }
}
