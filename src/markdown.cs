using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/*using ChessDotNet;*/

public class Program
{
    public static void Main()
    {
        var settings = new Dictionary<string, string>();
        settings.Add("repo", Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"));
        settings.Add("params", Uri.EscapeDataString("{}"));

        string createLink(string text, string link)
        {
            return $"[{text}]({link})";
        }

        string createIssueLink(string source, List<string> destList)
        {
            string issueLink = settings["issues"]["link"].Replace("{repo}", settings["repo"]).Replace("{params}", settings["params"]);

            var ret = destList.Select(dest => createLink(dest, issueLink.Replace("{source}", source).Replace("{dest}", dest))).ToList();
            return string.Join(", ", ret);
        }

        string generateTopMoves()
        {
            var dictionary = new Dictionary<string, int>();
            using (var file = new System.IO.StreamReader("data/top_moves.txt"))
            {
                dictionary = file.ReadToEnd().DeserializeObject<Dictionary<string, int>>();
            }

            string markdown = "\n";
            markdown += "| Total moves |  User  |\n";
            markdown += "| :---------: | :----- |\n";

            int maxEntries = int.Parse(settings["misc"]["max_top_moves"]);
            foreach (var entry in dictionary.OrderByDescending(x => x.Value).Take(maxEntries))
            {
                markdown += $"| {entry.Value} | {createLink(entry.Key, "https://github.com/" + entry.Key.Substring(1))} |\n";
            }

            return markdown + "\n";
        }

        string generateLastMoves()
        {
            string markdown = "\n";
            markdown += "| Move | Author |\n";
            markdown += "| :--: | :----- |\n";

            int counter = 0;

            using (var file = new System.IO.StreamReader("data/last_moves.txt"))
            {
                string line;
                while ((line = file.ReadLine()) != null)
                {
                    string[] parts = line.Split(':');

                    if (!line.Contains(":"))
                    {
                        continue;
                    }

                    if (counter >= int.Parse(settings["misc"]["max_last_moves"]))
                    {
                        break;
                    }

                    counter += 1;

                    Match matchObj = Regex.Match(line, @"([A-H][1-8])([A-H][1-8])", RegexOptions.IgnoreCase);
                    if (matchObj.Success)
                    {
                        string source = matchObj.Groups[1].Value.ToUpper();
                        string dest = matchObj.Groups[2].Value.ToUpper();

                        markdown += $"| `{source}` to `{dest}` | {createLink(parts[1], "https://github.com/" + parts[1].TrimStart()[1:])} |\n";
                    }
                    else
                    {
                        markdown += $"| `{parts[0]}` | {createLink(parts[1], "https://github.com/" + parts[1].TrimStart()[1:])} |\n";
                    }
                }
            }

            return markdown + "\n";
        }

        string generateMovesList(Board board)
        {
            var movesDict = new Dictionary<string, HashSet<string>>();

            foreach (var move in board.GetLegalMoves())
            {
                string source = move.OriginalPosition.ToString().ToUpper();
                string dest = move.NewPosition.ToString().ToUpper();

                if (!movesDict.ContainsKey(source))
                {
                    movesDict[source] = new HashSet<string>();
                }

                movesDict[source].Add(dest);
            }

            string markdown = "";

            if (board.IsCheckmate())
            {
                string issueLink = settings["issues"]["link"].Replace("{repo}", settings["repo"]).Replace("{params}", settings["issues"]["new_game"]);

                return $"**GAME IS OVER!** {createLink("Click here", issueLink)} to start a new game :D\n";
            }

            if (board.IsInCheck())
            {
                markdown += "**CHECK!** Choose your move wisely!\n";
            }

            markdown += "|  FROM  | TO (Just click a link!) |\n";
            markdown += "| :----: | :---------------------- |\n";

            foreach (var entry in movesDict.OrderBy(x => x.Key))
            {
                markdown += $"| **{entry.Key}** | {createIssueLink(entry.Key, entry.Value.ToList())} |\n";
            }

            return markdown;
        }

        string boardToMarkdown(Board board)
        {
            var boardList = str(board).Split('\n').Select(line => line.Split(' ')).ToList();
            string markdown = "";

            var images = new Dictionary<string, string>()
            {
                { "r", "img/black/rook.png" },
                { "n", "img/black/knight.png" },
                { "b", "img/black/bishop.png" },
                { "q", "img/black/queen.png" },
                { "k", "img/black/king.png" },
                { "p", "img/black/pawn.png" },
                { "R", "img/white/rook.png" },
                { "N", "img/white/knight.png" },
                { "B", "img/white/bishop.png" },
                { "Q", "img/white/queen.png" },
                { "K", "img/white/king.png" },
                { "P", "img/white/pawn.png" },
                { ".", "img/blank.png" }
            };

            if (board.WhoseTurn == Player.Black)
            {
                markdown += "|   | H | G | F | E | D | C | B | A |   |\n";
            }
            else
            {
                markdown += "|   | A | B | C | D | E | F | G | H |   |\n";
            }
            markdown += "|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|\n";

            var rows = Enumerable.Range(1, 8);
            if (board.WhoseTurn == Player.Black)
            {
                rows = rows.Reverse();
            }

            foreach (var row in rows)
            {
                markdown += $"| **{9 - row}** | ";
                var columns = boardList[row - 1];
                if (board.WhoseTurn == Player.Black)
                {
                    columns = columns.Reverse();
                }

                foreach (var elem in columns)
                {
                    markdown += $"<img src=\"{images.GetValueOrDefault(elem, "???")}\" width=50px> | ";
                }

                markdown += $"**{9 - row}** |\n";
            }

            if (board.WhoseTurn == Player.Black)
            {
                markdown += "|   | **H** | **G** | **F** | **E** | **D** | **C** | **B** | **A** |   |\n";
            }
            else
            {
                markdown += "|   | **A** | **B** | **C** | **D** | **E** | **F** | **G** | **H** |   |\n";
            }

            return markdown;
        }

        // Example usage
        var board = new Board();
        Console.WriteLine(boardToMarkdown(board));
        Console.WriteLine(generateMovesList(board));
    }
}
