# Own questions and voice lines

Everything lives in `BepInEx/wwds` inside the game folder (each game has its own). The mod menu page
**Inhalte** shows what was found, every problem with file and line, and buttons to reload and to open
the folder.

## Question packs

`BepInEx/wwds/questions/<pack>/` is one pack. Folders starting with `_` are ignored; `_vorlage` is
created on the first start with an example `questions.csv` and a `LIESMICH.txt`. Copy it, rename the
copy and edit it.

A show needs **12 questions, 1 final and 1 estimate**. Every further 12 questions make another show of
the same pack; the last one is filled up from the start, finals and estimates are used in turn. Pack
shows appear in the Mediathek from number **101** on and can be set as `[Quickplay] ShowId`. A show
keeps its number when you add packs (`BepInEx/wwds/question-packs.txt` remembers them).

### questions.csv

Excel, LibreOffice or any text editor. Separator `;` (or `,`, taken from the first line); UTF-8 or
Excel's default encoding both work. The first line names the columns, in any order:

| Column | Meaning |
|---|---|
| `Typ` | `Frage` (board question, also when empty), `Finale`, `Schätzfrage` |
| `Kategorie` | Category tile text, up to about 24 characters; a line break in the cell (Alt+Enter) or `\n` splits it |
| `Thema` | Optional: how the captains react, one of `Allgemein Alltag Brisant Clever Damals Essen Fragezeichen Genial Gesellschaft Natur Recht Sport Tiere Technik Weltweit Wissenschaft` |
| `Frage` | The question, up to about 130 characters, line breaks like the category |
| `A`, `B`, `C` | The three answers, up to about 65 characters, one line each |
| `Richtig` | `A`, `B` or `C` (or 1-3); for the estimate the number |
| `Video` | Optional: video file in the pack folder, e.g. `videos/q1.mp4` |
| `Reaktion` | Optional: second of the video at which the studio audience reacts |

### pack.json

The same as JSON; a pack may have both files (they add up).

```json
{
  "title": "Familienabend",
  "author": "Lukas",
  "questions": [
    { "category": "Tierisch", "topic": "Tiere", "question": "Wie viele Herzen hat ein Oktopus?",
      "answers": ["Eins", "Zwei", "Drei"], "correct": "C", "video": "videos/oktopus.mp4", "reaction": 6 }
  ],
  "master": [
    { "category": "Geografie", "question": "Welches Land hat die meisten Zeitzonen?",
      "answers": ["Russland", "USA", "Frankreich"], "correct": "C" }
  ],
  "estimate": [ { "question": "Wie viele Stufen hat der Eiffelturm?", "answer": 1665 } ]
}
```

### Videos

H.264 `.mp4` (as the game's own, 1280x720) works best; `.webm` (VP8), `.mov` and `.m4v` are accepted.
A question without a video, or with a file that is missing or does not start within a few seconds,
simply skips the video. The game plays answer videos at a fixed level; `Antwortvideos` in the mod menu
(page Spiel) changes it.

### How pack shows play

Studio, characters, rules and the generic host lines are the game's. The host has no recorded lines
for your questions, so the question and answers appear with short reading pauses instead; own voice
lines (below) can fill in. The game's AI answers pack questions at random (about one in three right).
Progress is saved in its own file (`Sessions_Custom.bin` next to the game's saves), statistics count,
Steam achievements are not unlocked in pack shows.

## Own voice lines

`BepInEx/wwds/voices/` takes `.ogg` or `.wav` files (no mp3). The file name is the name of the line it
replaces, for example `speech_kp_funfact.ogg`. **Inhalte > Satzliste exportieren** writes every line
name to `voices/_alle-saetze.txt` (during a show also that show's lines).

- Lines of a show (`speech_kp_question_...`, `speech_kp_category_...`, captains, contestants) have the
  same names in every show: put them in a folder `show12` (any depth) to replace them only in show 12;
  outside such a folder they apply to every show. Other subfolders are only for sorting.
- Generic lines (welcome, transitions, delay comments, the director in the tutorial) have unique names
  and apply everywhere.
- In pack shows a file named after the pack line (`speech_kp_question_custom_101_0`, `_a`, `_b`, `_c`
  for the answers, `speech_kp_category_custom_101_0`) voices your own questions: show 101, question 1.
- Files are loaded at start and with **Eigene Sätze neu laden**. The game waits for a line of any length
  and moves the mouth with it.
