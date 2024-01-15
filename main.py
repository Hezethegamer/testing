using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using Octokit;

namespace ChessBot
{
    public enum Action
    {
        UNKNOWN = 0,
        MOVE = 1,
        NEW_GAME = 2
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--self-test")
            {
                SelfTest.Run(MainLogic);
                Environment.Exit(0);
            }
            else
            {
                var github = new GitHubClient(new ProductHeaderValue("ChessBot"));
                var repo = github.Repository.Get(args[2], args[3]).Result;
                var issue = github.Issue.Get(args[2], args[3], int.Parse(args[4])).Result;

                var issueAuthor = "@" + issue.User.Login;
                var repoOwner = "@" + args[2];

                var result = MainLogic(issue, issueAuthor, repoOwner);

                if (!result.Item1)
                {
                    Console.Error.WriteLine(result.Item2);
                    Environment.Exit(1);
                }
            }
        }

        public static Tuple<bool, string> MainLogic(Issue issue, string issueAuthor, string repoOwner)
        {
            var action = ParseIssue(issue.Title);
            var gameboard = new ChessBoard();

            using (var settingsFile = new StreamReader("data/settings.yaml"))
            {
                var deserializer = new Deserializer();
                var settings = deserializer.Deserialize<Dictionary<string, dynamic>>(settingsFile);
            }

            if (action.Item1 == Action.NEW_GAME)
            {
                if (File.Exists("games/current.pgn") && issueAuthor != repoOwner)
                {
                    issue.Comment.Create(settings["comments"]["invalid_new_game"].Replace("{author}", issueAuthor));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed });
                    return new Tuple<bool, string>(false, "ERROR: A current game is in progress. Only the repo owner can start a new game");
                }

                issue.Comment.Create(settings["comments"]["successful_new_game"].Replace("{author}", issueAuthor));
                issue.Edit(new IssueUpdate { State = ItemState.Closed });

                using (var lastMoves = new StreamWriter("data/last_moves.txt"))
                {
                    lastMoves.WriteLine("Start game: " + issueAuthor);
                }

                // Create new game
                var game = new PgnGame();
                game.Headers["Event"] = repoOwner + "'s Online Open Chess Tournament";
                game.Headers["Site"] = "https://github.com/" + Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
                game.Headers["Date"] = DateTime.Now.ToString("yyyy.MM.dd");
                game.Headers["Round"] = "1";
            }
            else if (action.Item1 == Action.MOVE)
            {
                if (!File.Exists("games/current.pgn"))
                {
                    return new Tuple<bool, string>(false, "ERROR: There is no game in progress! Start a new game first");
                }

                // Load game from "games/current.pgn"
                using (var pgnFile = new StreamReader("games/current.pgn"))
                {
                    var game = Chess.Pgn.PgnReader.ReadGame(pgnFile);
                    gameboard = game.Board;
                }

                using (var moves = new StreamReader("data/last_moves.txt"))
                {
                    var line = moves.ReadLine();
                    var lastPlayer = line.Split(':')[1].Trim();
                    var lastMove = line.Split(':')[0].Trim();
                }

                foreach (var move in game.MainlineMoves())
                {
                    gameboard.Push(move);
                }

                if (action.Item2?.Substring(0, 2) == action.Item2?.Substring(2))
                {
                    issue.Comment.Create(settings["comments"]["invalid_move"].Replace("{author}", issueAuthor).Replace("{move}", action.Item2));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = { "Invalid" } });
                    return new Tuple<bool, string>(false, "ERROR: Move is invalid!");
                }

                // Try to move with promotion to queen
                if (Chess.Move.FromUci(action.Item2 + "q").IsValid(gameboard.LegalMoves))
                {
                    action = new Tuple<Action, string>(action.Item1, action.Item2 + "q");
                }

                var move = Chess.Move.FromUci(action.Item2);

                // Check if player is moving twice in a row
                if (lastPlayer == issueAuthor && !lastMove.Contains("Start game"))
                {
                    issue.Comment.Create(settings["comments"]["consecutive_moves"].Replace("{author}", issueAuthor));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = { "Invalid" } });
                    return new Tuple<bool, string>(false, "ERROR: Two moves in a row!");
                }

                // Check if move is valid
                if (!move.IsValid(gameboard.LegalMoves))
                {
                    issue.Comment.Create(settings["comments"]["invalid_move"].Replace("{author}", issueAuthor).Replace("{move}", action.Item2));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = { "Invalid" } });
                    return new Tuple<bool, string>(false, "ERROR: Move is invalid!");
                }

                // Check if board is valid
                if (!gameboard.IsValid())
                {
                    issue.Comment.Create(settings["comments"]["invalid_board"].Replace("{author}", issueAuthor));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = { "Invalid" } });
                    return new Tuple<bool, string>(false, "ERROR: Board is invalid!");
                }

                var issueLabels = new List<string> { "⚔️ Capture!" };
                issueLabels.Add(gameboard.Turn == Chess.Color.White ? "White" : "Black");

                issue.Comment.Create(settings["comments"]["successful_move"].Replace("{author}", issueAuthor).Replace("{move}", action.Item2));
                issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = issueLabels });

                UpdateLastMoves(action.Item2 + ": " + issueAuthor);
                UpdateTopMoves(issueAuthor);

                // Perform move
                gameboard.Push(move);
                game.End().AddMainVariation(move, comment: issueAuthor);
                game.Headers["Result"] = gameboard.Result;
            }
            else if (action.Item1 == Action.UNKNOWN)
            {
                issue.Comment.Create(settings["comments"]["unknown_command"].Replace("{author}", issueAuthor));
                issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = { "Invalid" } });
                return new Tuple<bool, string>(false, "ERROR: Unknown action");
            }

            // Save game to "games/current.pgn"
            File.WriteAllText("games/current.pgn", game.ToString() + Environment.NewLine);

            var lastMoves = Markdown.GenerateLastMoves();

            // If it is a game over, archive current game
            if (gameboard.IsGameOver())
            {
                var winMsg = new Dictionary<string, string>
                {
                    { "1/2-1/2", "It's a draw" },
                    { "1-0", "White wins" },
                    { "0-1", "Black wins" }
                };

                using (var lastMovesFile = new StreamReader("data/last_moves.txt"))
                {
                    var lines = new List<string>();
                    var pattern = new Regex(@".*: (@[a-z\d](?:[a-z\d]|-(?=[a-z\d])){0,38})", RegexOptions.IgnoreCase);
                    var playerList = new HashSet<string>(lines.FindAll(line => pattern.IsMatch(line)).ConvertAll(line => pattern.Match(line).Groups[1].Value));
                }

                if (gameboard.Result == "1/2-1/2")
                {
                    issue.Labels.Add("👑 Draw!");
                }
                else
                {
                    issue.Labels.Add("👑 Winner!");
                }

                issue.Comment.Create(settings["comments"]["game_over"].Replace("{outcome}", winMsg.GetValueOrDefault(gameboard.Result, "UNKNOWN"))
                    .Replace("{players}", string.Join(", ", playerList))
                    .Replace("{num_moves}", (lines.Count - 1).ToString())
                    .Replace("{num_players}", playerList.Count.ToString()));

                File.Move("games/current.pgn", $"games/game-{DateTime.Now:yyyyMMdd-HHmmss}.pgn");
                File.Delete("data/last_moves.txt");
            }

            using (var file = new StreamReader("README.md"))
            {
                var readme = file.ReadToEnd();
                readme = ReplaceTextBetween(readme, settings["markers"]["board"], "{chess_board}");
                readme = ReplaceTextBetween(readme, settings["markers"]["moves"], "{moves_list}");
                readme = ReplaceTextBetween(readme, settings["markers"]["turn"], "{turn}");
                readme = ReplaceTextBetween(readme, settings["markers"]["last_moves"], "{last_moves}");
                readme = ReplaceTextBetween(readme, settings["markers"]["top_moves"], "{top_moves}");

                using (var outFile = new StreamWriter("README.md"))
                {
                    outFile.Write(readme.Replace("{chess_board}", Markdown.BoardToMarkdown(gameboard))
                        .Replace("{moves_list}", Markdown.GenerateMovesList(gameboard))
                        .Replace("{turn}", gameboard.Turn == Chess.Color.White ? "white" : "black")
                        .Replace("{last_moves}", lastMoves)
                        .Replace("{top_moves}", Markdown.GenerateTopMoves()));
                }
            }

            return new Tuple<bool, string>(true, "");
        }

        public static Tuple<Action, string> ParseIssue(string title)
        {
            if (title.ToLower() == "chess: start new game")
            {
                return new Tuple<Action, string>(Action.NEW_GAME, null);
            }

            if (title.ToLower().Contains("chess: move"))
            {
                var matchObj = Regex.Match(title, @"Chess: Move ([A-H][1-8]) to ([A-H][1-8])", RegexOptions.IgnoreCase);

                var source = matchObj.Groups[1].Value;
                var dest = matchObj.Groups[2].Value;
                return new Tuple<Action, string>(Action.MOVE, (source + dest).ToLower());
            }

            return new Tuple<Action, string>(Action.UNKNOWN, null);
        }

        public static string ReplaceTextBetween(string originalText, string marker, string replacementText)
        {
            var delimiterA = marker["begin"];
            var delimiterB = marker["end"];

            if (!originalText.Contains(delimiterA) || !originalText.Contains(delimiterB))
            {
                return originalText;
            }

            var leadingText = originalText.Split(delimiterA)[0];
            var trailingText = originalText.Split(delimiterB)[1];

            return leadingText + delimiterA + replacementText + delimiterB + trailingText;
        }

        public static void UpdateTopMoves(string user)
        {
            var contents = File.ReadAllText("data/top_moves.txt");
            var dictionary = new Dictionary<string, int>(contents);

            if (!dictionary.ContainsKey(user))
            {
                dictionary[user] = 1;
            }
            else
            {
                dictionary[user]++;
            }

            File.WriteAllText("data/top_moves.txt", dictionary.ToString());
        }

        public static void UpdateLastMoves(string line)
        {
            using (var lastMoves = new StreamWriter("data/last_moves.txt", append: true))
            {
                lastMoves.WriteLine(line);
            }
        }
    }
}
