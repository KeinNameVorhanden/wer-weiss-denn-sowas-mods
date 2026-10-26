using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using WWDS.ModCore.Serialization;

namespace WWDS.ModCore.Content;

/// <summary>Kind of a pack question: one of the 12 board questions, the final (master) or the estimate.</summary>
public enum PackQuestionKind
{
    Board,
    Master,
    Estimate,
}

/// <summary>One question of a pack, already in the game's text conventions (line breaks as the "\n" token).</summary>
public sealed class PackQuestion
{
    public PackQuestionKind Kind { get; set; }

    public string Category { get; set; } = string.Empty;

    /// <summary>Topic the captains react to (EInternalCategories name), "Allgemein" when not given.</summary>
    public string Topic { get; set; } = "Allgemein";

    public string Question { get; set; } = string.Empty;

    public string[] Answers { get; set; } = new string[3];

    /// <summary>Correct answer 1..3 (A..C), or the number for an estimate.</summary>
    public double Correct { get; set; }

    /// <summary>Video file relative to the pack folder, empty = none.</summary>
    public string Video { get; set; } = string.Empty;

    /// <summary>Second of the video at which the studio reacts (0 = no reaction).</summary>
    public int Reaction { get; set; }

    /// <summary>Where the question was defined ("questions.csv line 7"), for messages.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>One playable show of a pack: 12 board questions, a final and an estimate.</summary>
public sealed class PackShow
{
    public PackShow(int part, PackQuestion[] board, PackQuestion master, PackQuestion estimate)
    {
        Part = part;
        Board = board;
        Master = master;
        Estimate = estimate;
    }

    /// <summary>0-based part of the pack.</summary>
    public int Part { get; }

    public PackQuestion[] Board { get; }

    public PackQuestion Master { get; }

    public PackQuestion Estimate { get; }
}

/// <summary>
/// A folder of own questions: pack.json and/or questions.csv plus optional videos. A show needs 12 board
/// questions, one final and one estimate; a bigger pack becomes several shows of 12 (the last one filled
/// up from the start), using the finals and estimates in turn.
/// </summary>
public sealed class QuestionPack
{
    public const int BoardSize = 12;
    public const int MaxCategory = 24;
    public const int MaxQuestion = 134;
    public const int MaxAnswer = 69;

    public static readonly string[] Topics =
    {
        "Allgemein", "Alltag", "Brisant", "Clever", "Damals", "Essen", "Fragezeichen", "Genial",
        "Gesellschaft", "Natur", "Recht", "Sport", "Tiere", "Technik", "Weltweit", "Wissenschaft",
    };

    public static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".webm" };

    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public List<PackQuestion> Questions { get; } = new();

    /// <summary>Problems that make the pack unplayable.</summary>
    public List<string> Errors { get; } = new();

    /// <summary>Hints (text likely too long for the screen, missing video, ...); the pack still plays.</summary>
    public List<string> Warnings { get; } = new();

    public bool Playable => Errors.Count == 0;

    public IEnumerable<PackQuestion> Of(PackQuestionKind kind) => Questions.Where(q => q.Kind == kind);

    /// <summary>Number of shows the pack yields.</summary>
    public int ShowCount => Playable ? (Of(PackQuestionKind.Board).Count() + BoardSize - 1) / BoardSize : 0;

    /// <summary>Questions of show <paramref name="part"/> (0-based); wraps around the pool.</summary>
    public PackShow Show(int part)
    {
        var board = Of(PackQuestionKind.Board).ToList();
        var master = Of(PackQuestionKind.Master).ToList();
        var estimate = Of(PackQuestionKind.Estimate).ToList();
        var rows = new PackQuestion[BoardSize];
        for (var i = 0; i < BoardSize; i++) rows[i] = board[((part * BoardSize) + i) % board.Count];
        return new PackShow(part, rows, master[part % master.Count], estimate[part % estimate.Count]);
    }

    // ---- reading ------------------------------------------------------------------------------------

    /// <summary>Reads pack.json text into <paramref name="pack"/> (errors are collected, never thrown).</summary>
    public void ReadJson(string text, string file = "pack.json")
    {
        Dictionary<string, object?> root;
        try
        {
            root = Json.Parse(text) as Dictionary<string, object?> ?? throw new FormatException("the file must contain one { ... } object");
        }
        catch (FormatException ex)
        {
            Errors.Add(file + ": " + ex.Message);
            return;
        }

        if (Str(root, "title") is { Length: > 0 } title) Title = title;
        if (Str(root, "author") is { Length: > 0 } author) Author = author;
        ReadList(root, "questions", PackQuestionKind.Board, file);
        ReadList(root, "master", PackQuestionKind.Master, file);
        ReadList(root, "estimate", PackQuestionKind.Estimate, file);
    }

    private void ReadList(Dictionary<string, object?> root, string key, PackQuestionKind kind, string file)
    {
        if (!root.TryGetValue(key, out var value) || value == null) return;
        if (value is not List<object?> list)
        {
            Errors.Add(file + ": \"" + key + "\" must be a list [ ... ]");
            return;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var where = file + " " + key + " #" + (i + 1);
            if (list[i] is not Dictionary<string, object?> o)
            {
                Errors.Add(where + ": must be an object { ... }");
                continue;
            }

            var answers = o.TryGetValue("answers", out var a) && a is List<object?> al ? al.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture) ?? string.Empty).ToArray() : new string[0];
            var correct = o.TryGetValue(kind == PackQuestionKind.Estimate ? "answer" : "correct", out var c) ? c : null;
            Add(kind, Str(o, "category"), Str(o, "topic"), Str(o, "question"), answers,
                Convert.ToString(correct, CultureInfo.InvariantCulture) ?? string.Empty, Str(o, "video"), Str(o, "reaction"), where);
        }
    }

    private static string Str(Dictionary<string, object?> o, string key) =>
        o.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;

    /// <summary>
    /// Reads questions.csv (separator ; or , as in the header; Excel's quoting). Columns, by header name:
    /// Typ (Frage | Finale | Schaetzfrage), Kategorie, Thema, Frage, A, B, C, Richtig, Video, Reaktion.
    /// </summary>
    public void ReadCsv(string text, string file = "questions.csv")
    {
        var rows = Csv(text);
        if (rows.Count == 0)
        {
            Errors.Add(file + ": empty");
            return;
        }

        var header = rows[0].Select(h => Fold(h)).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        var typ = Col("typ", "type", "art");
        var cat = Col("kategorie", "category");
        var topic = Col("thema", "topic");
        var question = Col("frage", "question");
        var ca = Col("a", "antwort a");
        var cb = Col("b", "antwort b");
        var cc = Col("c", "antwort c");
        var right = Col("richtig", "correct", "loesung", "answer");
        var video = Col("video");
        var reaction = Col("reaktion", "reaction");
        if (question < 0 || right < 0)
        {
            Errors.Add(file + ": the first line needs the column names (at least Frage and Richtig)");
            return;
        }

        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.All(string.IsNullOrEmpty)) continue;
            string Cell(int i) => i >= 0 && i < row.Count ? row[i] : string.Empty;
            var kind = ParseKind(Cell(typ));
            var where = file + " line " + (r + 1);
            if (kind == null)
            {
                Errors.Add(where + ": Typ \"" + Cell(typ) + "\" unknown (Frage, Finale or Schaetzfrage)");
                continue;
            }

            Add(kind.Value, Cell(cat), Cell(topic), Cell(question), new[] { Cell(ca), Cell(cb), Cell(cc) }, Cell(right), Cell(video), Cell(reaction), where);
        }
    }

    private static PackQuestionKind? ParseKind(string text) => Fold(text) switch
    {
        "" or "frage" or "question" or "normal" => PackQuestionKind.Board,
        "finale" or "finalfrage" or "master" or "masterfrage" => PackQuestionKind.Master,
        "schaetzfrage" or "schaetzen" or "estimate" or "bonus" => PackQuestionKind.Estimate,
        _ => null,
    };

    /// <summary>Lower case, umlauts spelled out, trimmed ("Schätzfrage" == "schaetzfrage").</summary>
    private static string Fold(string text) => (text ?? string.Empty).Trim().ToLowerInvariant()
        .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");

    private void Add(PackQuestionKind kind, string category, string topic, string question, string[] answers, string correct, string video, string reaction, string where)
    {
        var q = new PackQuestion { Kind = kind, Source = where, Video = (video ?? string.Empty).Trim().Replace('\\', '/') };
        q.Question = Lines(question);
        q.Category = Lines(category);
        if (q.Question.Length == 0) Errors.Add(where + ": the question text is missing");
        if (kind != PackQuestionKind.Estimate)
        {
            if (q.Category.Length == 0) Errors.Add(where + ": the category is missing");
            q.Answers = Enumerable.Range(0, 3).Select(i => i < answers.Length ? OneLine(answers[i]) : string.Empty).ToArray();
            if (q.Answers.Any(x => x.Length == 0)) Errors.Add(where + ": three answers A, B and C are needed");
            var c = (correct ?? string.Empty).Trim().ToUpperInvariant();
            q.Correct = c switch { "A" or "1" => 1, "B" or "2" => 2, "C" or "3" => 3, _ => 0 };
            if (q.Correct == 0) Errors.Add(where + ": the correct answer must be A, B or C (or 1-3), not \"" + correct + "\"");
            if (q.Category.Replace("\\n", string.Empty).Length > MaxCategory) Warnings.Add(where + ": category longer than " + MaxCategory + " characters may not fit its tile");
            if (q.Answers.Any(x => x.Length > MaxAnswer)) Warnings.Add(where + ": answers longer than " + MaxAnswer + " characters may not fit");
        }
        else if (!double.TryParse((correct ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0 || number > 100000000)
        {
            Errors.Add(where + ": the estimate needs a number as answer, not \"" + correct + "\"");
        }
        else
        {
            q.Correct = Math.Round(number);
        }

        if (q.Question.Replace("\\n", string.Empty).Length > MaxQuestion) Warnings.Add(where + ": question longer than " + MaxQuestion + " characters may not fit");
        var t = Topics.FirstOrDefault(x => Fold(x) == Fold(topic ?? string.Empty));
        if (t != null) q.Topic = t;
        else if (!string.IsNullOrEmpty((topic ?? string.Empty).Trim())) Warnings.Add(where + ": Thema \"" + topic + "\" unknown, using Allgemein");
        if (q.Video.Length > 0 && !VideoExtensions.Any(e => q.Video.EndsWith(e, StringComparison.OrdinalIgnoreCase))) Errors.Add(where + ": video must be .mp4, .m4v, .mov or .webm (H.264 mp4 works best)");
        if (int.TryParse((reaction ?? string.Empty).Trim(), out var seconds) && seconds > 0) q.Reaction = seconds;
        Questions.Add(q);
    }

    /// <summary>Real line breaks (and a typed "\n") become the game's line break token.</summary>
    private static string Lines(string text) => string.Join("\\n", (text ?? string.Empty).Replace("\r\n", "\n").Replace("\\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray());

    /// <summary>Answers have no line breaks in the game.</summary>
    private static string OneLine(string text) => string.Join(" ", (text ?? string.Empty).Replace("\\n", " ").Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Checks the counts after all files were read.</summary>
    public void Finish()
    {
        var board = Of(PackQuestionKind.Board).Count();
        if (board < BoardSize) Errors.Add("needs at least " + BoardSize + " questions of type Frage, has " + board);
        if (!Of(PackQuestionKind.Master).Any()) Errors.Add("needs at least one Finale question");
        if (!Of(PackQuestionKind.Estimate).Any()) Errors.Add("needs at least one Schaetzfrage");
        if (board >= BoardSize && board % BoardSize != 0) Warnings.Add("the last show is filled up with questions from the start (" + board + " questions are not a multiple of 12)");
    }

    // ---- CSV ----------------------------------------------------------------------------------------

    /// <summary>Splits CSV text (separator from the header line; quotes with "" escapes; line breaks inside quotes).</summary>
    public static List<List<string>> Csv(string text)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrEmpty(text)) return rows;
        if (text[0] == (char)0xFEFF) text = text.Substring(1);
        var firstLine = text.Split('\n')[0];
        var sep = firstLine.Count(ch => ch == ';') >= firstLine.Count(ch => ch == ',') ? ';' : ',';
        if (firstLine.Count(ch => ch == '\t') > Math.Max(firstLine.Count(ch => ch == ';'), firstLine.Count(ch => ch == ','))) sep = '\t';
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(ch);
                }
            }
            else if (ch == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (ch == sep)
            {
                row.Add(cell.ToString().Trim());
                cell.Length = 0;
            }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString().Trim());
                cell.Length = 0;
                rows.Add(row);
                row = new List<string>();
            }
            else
            {
                cell.Append(ch);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString().Trim());
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>UTF-8 when the bytes are valid UTF-8, else Windows-1252/Latin-1 (Excel's default CSV).</summary>
    public static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (ArgumentException)
        {
            var chars = new char[bytes.Length];
            for (var i = 0; i < bytes.Length; i++) chars[i] = bytes[i] == 0x80 ? (char)0x20AC : (char)bytes[i];
            return new string(chars);
        }
    }
}
