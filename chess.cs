using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

using ChessDotNet;
using ChessDotNet.Pieces;
using ChessDotNet.Variants.Antichess;
using ChessDotNet.Variants.Atomic;
using ChessDotNet.Variants.Crazyhouse;
using ChessDotNet.Variants.KingOfTheHill;
using ChessDotNet.Variants.ThreeCheck;
using ChessDotNet.Variants;

namespace ChessGame
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
            if (args.Length >= 1 && args[0] == "--self-test")
            {
                SelfTest.Run(MainMethod);
                Environment.Exit(0);
            }
            else
            {
                var repo = new GitHubClient(new ProductHeaderValue("ChessGame"));
                var issue = repo.Issue.Get("owner", "repo", int.Parse(Environment.GetEnvironmentVariable("ISSUE_NUMBER")));
                var issueAuthor = "@" + issue.User.Login;
                var repoOwner = "@" + Environment.GetEnvironmentVariable("REPOSITORY_OWNER");

                var (ret, reason) = MainMethod(issue, issueAuthor, repoOwner);

                if (!ret)
                {
                    Console.WriteLine(reason);
                    Environment.Exit(1);
                }
            }
        }

        public static (bool, string) MainMethod(Issue issue, string issueAuthor, string repoOwner)
        {
            var action = ParseIssue(issue.Title);
            var gameboard = new ChessGame();

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            var settings = deserializer.Deserialize<Settings>(File.ReadAllText("data/settings.yaml"));

            if (action.Item1 == Action.NEW_GAME)
            {
                if (File.Exists("games/current.pgn") && issueAuthor != repoOwner)
                {
                    issue.CreateComment(string.Format(settings.Comments.InvalidNewGame, issueAuthor));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed });
                    return (false, "ERROR: A current game is in progress. Only the repo owner can start a new game");
                }

                issue.CreateComment(string.Format(settings.Comments.SuccessfulNewGame, issueAuthor));
                issue.Edit(new IssueUpdate { State = ItemState.Closed });

                File.WriteAllText("data/last_moves.txt", "Start game: " + issueAuthor);

                // Create new game
                var game = new ChessGame();
                game.Event = repoOwner + "'s Online Open Chess Tournament";
                game.Site = "https://github.com/" + Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
                game.Date = DateTime.Now.ToString("yyyy.MM.dd");
                game.Round = "1";
            }
            else if (action.Item1 == Action.MOVE)
            {
                if (!File.Exists("games/current.pgn"))
                {
                    return (false, "ERROR: There is no game in progress! Start a new game first");
                }

                // Load game from "games/current.pgn"
                var game = new ChessGame();
                game.LoadFromFile("games/current.pgn");
                gameboard = game;

                var lines = File.ReadAllLines("data/last_moves.txt");
                var lastPlayer = lines[0].Split(':')[1].Trim();
                var lastMove = lines[0].Split(':')[0].Trim();

                foreach (var move in game.Moves)
                {
                    gameboard.MakeMove(move);
                }

                if (action.Item2.Substring(0, 2) == action.Item2.Substring(2))
                {
                    issue.CreateComment(string.Format(settings.Comments.InvalidMove, issueAuthor, action.Item2));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = new[] { "Invalid" } });
                    return (false, "ERROR: Move is invalid!");
                }

                // Try to move with promotion to queen
                if (ChessGame.MoveFromUCI(action.Item2 + "q") is ChessMove move && gameboard.IsValidMove(move))
                {
                    action = (action.Item1, action.Item2 + "q");
                }

                move = ChessGame.MoveFromUCI(action.Item2);

                // Check if player is moving twice in a row
                if (lastPlayer == issueAuthor && !lastMove.Contains("Start game"))
                {
                    issue.CreateComment(string.Format(settings.Comments.ConsecutiveMoves, issueAuthor));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = new[] { "Invalid" } });
                    return (false, "ERROR: Two moves in a row!");
                }

                // Check if move is valid
                if (!gameboard.IsValidMove(move))
                {
                    issue.CreateComment(string.Format(settings.Comments.InvalidMove, issueAuthor, action.Item2));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = new[] { "Invalid" } });
                    return (false, "ERROR: Move is invalid!");
                }

                // Check if board is valid
                if (!gameboard.IsValid())
                {
                    issue.CreateComment(string.Format(settings.Comments.InvalidBoard, issueAuthor));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = new[] { "Invalid" } });
                    return (false, "ERROR: Board is invalid!");
                }

                var issueLabels = new List<string>();
                if (gameboard.IsCapture(move))
                {
                    issueLabels.Add("⚔️ Capture!");
                }
                issueLabels.Add(gameboard.WhoseTurn == Player.White ? "White" : "Black");

                issue.CreateComment(string.Format(settings.Comments.SuccessfulMove, issueAuthor, action.Item2));
                issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = issueLabels });

                File.WriteAllText("data/last_moves.txt", action.Item2 + ": " + issueAuthor);
                UpdateTopMoves(issueAuthor);

                // Perform move
                gameboard.MakeMove(move);
                game.Moves.Add(move);
                game.Result = gameboard.GetResult();
            }
            else if (action.Item1 == Action.UNKNOWN)
            {
                issue.CreateComment(string.Format(settings.Comments.UnknownCommand, issueAuthor));
                issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = new[] { "Invalid" } });
                return (false, "ERROR: Unknown action");
            }

            // Save game to "games/current.pgn"
            gameboard.SaveToFile("games/current.pgn");

            var lastMoves = GenerateLastMoves();

            // If it is a game over, archive current game
            if (gameboard.IsCheckmate || gameboard.IsStalemate || gameboard.IsInsufficientMaterial || gameboard.IsDraw || gameboard.IsRepetition || gameboard.IsSeventyFiveMoveRule)
            {
                var winMsg = new Dictionary<string, string>
                {
                    { "1/2-1/2", "It's a draw" },
                    { "1-0", "White wins" },
                    { "0-1", "Black wins" }
                };

                var lines = File.ReadAllLines("data/last_moves.txt");
                var pattern = new Regex(@".*: (@[a-z\d](?:[a-z\d]|-(?=[a-z\d])){0,38})", RegexOptions.IgnoreCase);
                var playerList = new HashSet<string>(lines.Select(line => pattern.Match(line).Groups[1].Value));

                if (gameboard.Result == "1/2-1/2")
                {
                    issue.AddLabels("👑 Draw!");
                }
                else
                {
                    issue.AddLabels("👑 Winner!");
                }

                issue.CreateComment(string.Format(settings.Comments.GameOver, winMsg.GetValueOrDefault(gameboard.Result, "UNKNOWN"), string.Join(", ", playerList), lines.Length - 1, playerList.Count));

                File.Move("games/current.pgn", "games/game-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".pgn");
                File.Delete("data/last_moves.txt");
            }

            var readme = File.ReadAllText("README.md");
            readme = ReplaceTextBetween(readme, settings.Markers.Board, GenerateChessBoard(gameboard));
            readme = ReplaceTextBetween(readme, settings.Markers.Moves, GenerateMovesList(gameboard));
            readme = ReplaceTextBetween(readme, settings.Markers.Turn, gameboard.WhoseTurn == Player.White ? "white" : "black");
            readme = ReplaceTextBetween(readme, settings.Markers.LastMoves, lastMoves);
            readme = ReplaceTextBetween(readme, settings.Markers.TopMoves, GenerateTopMoves());

            File.WriteAllText("README.md", readme);

            return (true, "");
        }

        public static (Action, string) ParseIssue(string title)
        {
            if (title.ToLower() == "chess: start new game")
            {
                return (Action.NEW_GAME, null);
            }

            if (title.ToLower().Contains("chess: move"))
            {
                var match = Regex.Match(title, @"Chess: Move ([A-H][1-8]) to ([A-H][1-8])", RegexOptions.IgnoreCase);

                var source = match.Groups[1].Value;
                var dest = match.Groups[2].Value;
                return (Action.MOVE, (source + dest).ToLower());
            }

            return (Action.UNKNOWN, null);
        }

        public static string ReplaceTextBetween(string originalText, string beginMarker, string replacementText)
        {
            var delimiterA = beginMarker;
            var delimiterB = beginMarker.Replace("begin", "end");

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
            var dictionary = new Dictionary<string, int>();

            if (!string.IsNullOrEmpty(contents))
            {
                dictionary = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .Build()
                    .Deserialize<Dictionary<string, int>>(contents);
            }

            if (!dictionary.ContainsKey(user))
            {
                dictionary[user] = 1; // First move
            }
            else
            {
                dictionary[user] += 1;
            }

            File.WriteAllText("data/top_moves.txt", new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build()
                .Serialize(dictionary));
        }

        public static string GenerateChessBoard(ChessGame gameboard)
        {
            var board = gameboard.GetBoard();
            var chessBoard = new ChessBoard();

            for (var rank = 0; rank < 8; rank++)
            {
                for (var file = 0; file < 8; file++)
                {
                    var piece = board[rank, file];
                    var position = new Position(rank, file);

                    if (piece != null)
                    {
                        chessBoard.AddPiece(piece, position);
                    }
                }
            }

            return chessBoard.ToString();
        }

        public static string GenerateMovesList(ChessGame gameboard)
        {
            var movesList = gameboard.Moves.Select(move => move.ToString()).ToList();
            return string.Join("\n", movesList);
        }

        public static string GenerateLastMoves()
        {
            var lines = File.ReadAllLines("data/last_moves.txt");
            return string.Join("\n", lines);
        }

        public static string GenerateTopMoves()
        {
            var contents = File.ReadAllText("data/top_moves.txt");
            var dictionary = new Dictionary<string, int>();

            if (!string.IsNullOrEmpty(contents))
            {
                dictionary = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .Build()
                    .Deserialize<Dictionary<string, int>>(contents);
            }

            var topMoves = dictionary.OrderByDescending(pair => pair.Value).Take(10).Select(pair => $"{pair.Key}: {pair.Value}").ToList();
            return string.Join("\n", topMoves);
        }
    }

    public class Settings
    {
        public Comments Comments { get; set; }
        public Markers Markers { get; set; }
    }

    public class Comments
    {
        public string InvalidNewGame { get; set; }
        public string SuccessfulNewGame { get; set; }
        public string InvalidMove { get; set; }
        public string ConsecutiveMoves { get; set; }
        public string InvalidBoard { get; set; }
        public string SuccessfulMove { get; set; }
        public string UnknownCommand { get; set; }
        public string GameOver { get; set; }
    }

    public class Markers
    {
        public string Board { get; set; }
        public string Moves { get; set; }
        public string Turn { get; set; }
        public string LastMoves { get; set; }
        public string TopMoves { get; set; }
    }
}

/* Chess game in C#

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ChessGame
{
    public enum Action
    {
        UNKNOWN = 0,
        MOVE = 1,
        NEW_GAME = 2
    }

    public class ChessGame
    {
        private static Dictionary<string, string> images = new Dictionary<string, string>()
        {
            {"r", "img/black/rook.png"},
            {"n", "img/black/knight.png"},
            {"b", "img/black/bishop.png"},
            {"q", "img/black/queen.png"},
            {"k", "img/black/king.png"},
            {"p", "img/black/pawn.png"},
            {"R", "img/white/rook.png"},
            {"N", "img/white/knight.png"},
            {"B", "img/white/bishop.png"},
            {"Q", "img/white/queen.png"},
            {"K", "img/white/king.png"},
            {"P", "img/white/pawn.png"},
            {".", "img/blank.png"}
        };

        private static Dictionary<string, string> squareNames = new Dictionary<string, string>()
        {
            {"a", "A"},
            {"b", "B"},
            {"c", "C"},
            {"d", "D"},
            {"e", "E"},
            {"f", "F"},
            {"g", "G"},
            {"h", "H"}
        };

        private static Dictionary<string, string> reverseSquareNames = squareNames.ToDictionary(x => x.Value, x => x.Key);

        private static Dictionary<string, string> markers = new Dictionary<string, string>()
        {
            {"board", "<!-- BOARD -->"},
            {"moves", "<!-- MOVES -->"},
            {"turn", "<!-- TURN -->"},
            {"last_moves", "<!-- LAST_MOVES -->"},
            {"top_moves", "<!-- TOP_MOVES -->"}
        };

        private static Dictionary<string, string> comments = new Dictionary<string, string>()
        {
            {"successful_new_game", "New game started by {author}"},
            {"invalid_new_game", "Invalid new game request by {author}"},
            {"successful_move", "{author} moved {move}"},
            {"invalid_move", "Invalid move {move} by {author}"},
            {"consecutive_moves", "Two moves in a row by {author}"},
            {"unknown_command", "Unknown command by {author}"},
            {"game_over", "{outcome}! Players: {players}. Total moves: {num_moves}. Total players: {num_players}"}
        };

        private static Dictionary<string, string> issues = new Dictionary<string, string>()
        {
            {"link", "https://github.com/{repo}/issues/new?{params}"},
            {"move", "title=Chess: Move {source} to {dest}"},
            {"new_game", "title=Chess: Start new game"}
        };

        private static Dictionary<string, string> settings = new Dictionary<string, string>()
        {
            {"max_top_moves", "5"},
            {"max_last_moves", "10"}
        };

        private static Dictionary<string, string> data = new Dictionary<string, string>()
        {
            {"top_moves", "data/top_moves.txt"},
            {"last_moves", "data/last_moves.txt"},
            {"settings", "data/settings.yaml"}
        };

        private static Dictionary<string, string> games = new Dictionary<string, string>()
        {
            {"current", "games/current.pgn"}
        };

        private static Dictionary<string, string> images = new Dictionary<string, string>()
        {
            {"r", "img/black/rook.png"},
            {"n", "img/black/knight.png"},
            {"b", "img/black/bishop.png"},
            {"q", "img/black/queen.png"},
            {"k", "img/black/king.png"},
            {"p", "img/black/pawn.png"},
            {"R", "img/white/rook.png"},
            {"N", "img/white/knight.png"},
            {"B", "img/white/bishop.png"},
            {"Q", "img/white/queen.png"},
            {"K", "img/white/king.png"},
            {"P", "img/white/pawn.png"},
            {".", "img/blank.png"}
        };

        private static Dictionary<string, string> squareNames = new Dictionary<string, string>()
        {
            {"a", "A"},
            {"b", "B"},
            {"c", "C"},
            {"d", "D"},
            {"e", "E"},
            {"f", "F"},
            {"g", "G"},
            {"h", "H"}
        };

        private static Dictionary<string, string> reverseSquareNames = squareNames.ToDictionary(x => x.Value, x => x.Key);

        private static Dictionary<string, string> markers = new Dictionary<string, string>()
        {
            {"board", "<!-- BOARD -->"},
            {"moves", "<!-- MOVES -->"},
            {"turn", "<!-- TURN -->"},
            {"last_moves", "<!-- LAST_MOVES -->"},
            {"top_moves", "<!-- TOP_MOVES -->"}
        };

        private static Dictionary<string, string> comments = new Dictionary<string, string>()
        {
            {"successful_new_game", "New game started by {author}"},
            {"invalid_new_game", "Invalid new game request by {author}"},
            {"successful_move", "{author} moved {move}"},
            {"invalid_move", "Invalid move {move} by {author}"},
            {"consecutive_moves", "Two moves in a row by {author}"},
            {"unknown_command", "Unknown command by {author}"},
            {"game_over", "{outcome}! Players: {players}. Total moves: {num_moves}. Total players: {num_players}"}
        };

        private static Dictionary<string, string> issues = new Dictionary<string, string>()
        {
            {"link", "https://github.com/{repo}/issues/new?{params}"},
            {"move", "title=Chess: Move {source} to {dest}"},
            {"new_game", "title=Chess: Start new game"}
        };

        private static Dictionary<string, string> settings = new Dictionary<string, string>()
        {
            {"max_top_moves", "5"},
            {"max_last_moves", "10"}
        };

        private static Dictionary<string, string> data = new Dictionary<string, string>()
        {
            {"top_moves", "data/top_moves.txt"},
            {"last_moves", "data/last_moves.txt"},
            {"settings", "data/settings.yaml"}
        };

        private static Dictionary<string, string> games = new Dictionary<string, string>()
        {
            {"current", "games/current.pgn"}
        };

        private static Dictionary<string, string> images = new Dictionary<string, string>()
        {
            {"r", "img/black/rook.png"},
            {"n", "img/black/knight.png"},
            {"b", "img/black/bishop.png"},
            {"q", "img/black/queen.png"},
            {"k", "img/black/king.png"},
            {"p", "img/black/pawn.png"},
            {"R", "img/white/rook.png"},
            {"N", "img/white/knight.png"},
            {"B", "img/white/bishop.png"},
            {"Q", "img/white/queen.png"},
            {"K", "img/white/king.png"},
            {"P", "img/white/pawn.png"},
            {".", "img/blank.png"}
        };

        private static Dictionary<string, string> squareNames = new Dictionary<string, string>()
        {
            {"a", "A"},
            {"b", "B"},
            {"c", "C"},
            {"d", "D"},
            {"e", "E"},
            {"f", "F"},
            {"g", "G"},
            {"h", "H"}
        };

        private static Dictionary<string, string> reverseSquareNames = squareNames.ToDictionary(x => x.Value, x => x.Key);

        private static Dictionary<string, string> markers = new Dictionary<string, string>()
        {
            {"board", "<!-- BOARD -->"},
            {"moves", "<!-- MOVES -->"},
            {"turn", "<!-- TURN -->"},
            {"last_moves", "<!-- LAST_MOVES -->"},
            {"top_moves", "<!-- TOP_MOVES -->"}
        };

        private static Dictionary<string, string> comments = new Dictionary<string, string>()
        {
            {"successful_new_game", "New game started by {author}"},
            {"invalid_new_game", "Invalid new game request by {author}"},
            {"successful_move", "{author} moved {move}"},
            {"invalid_move", "Invalid move {move} by {author}"},
            {"consecutive_moves", "Two moves in a row by {author}"},
            {"unknown_command", "Unknown command by {author}"},
            {"game_over", "{outcome}! Players: {players}. Total moves: {num_moves}. Total players: {num_players}"}
        };

        private static Dictionary<string, string> issues = new Dictionary<string, string>()
        {
            {"link", "https://github.com/{repo}/issues/new?{params}"},
            {"move", "title=Chess: Move {source} to {dest}"},
            {"new_game", "title=Chess: Start new game"}
        };

        private static Dictionary<string, string> settings = new Dictionary<string, string>()
        {
            {"max_top_moves", "5"},
            {"max_last_moves", "10"}
        };

        private static Dictionary<string, string> data = new Dictionary<string, string>()
        {
            {"top_moves", "data/top_moves.txt"},
            {"last_moves", "data/last_moves.txt"},
            {"settings", "data/settings.yaml"}
        };

        private static Dictionary<string, string> games = new Dictionary<string, string>()
        {
            {"current", "games/current.pgn"}
        };

        private static Dictionary<string, string> images = new Dictionary<string, string>()
        {
            {"r", "img/black/rook.png"},
            {"n", "img/black/knight.png"},
            {"b", "img/black/bishop.png"},
            {"q", "img/black/queen.png"},
            {"k", "img/black/king.png"},
            {"p", "img/black/pawn.png"},
            {"R", "img/white/rook.png"},
            {"N", "img/white/knight.png"},
            {"B", "img/white/bishop.png"},
            {"Q", "img/white/queen.png"},
            {"K", "img/white/king.png"},
            {"P", "img/white/pawn.png"},
            {".", "img/blank.png"}
        };

        private static Dictionary<string, string> squareNames = new Dictionary<string, string>()
        {
            {"a", "A"},
            {"b", "B"},
            {"c", "C"},
            {"d", "D"},
            {"e", "E"},
            {"f", "F"},
            {"g", "G"},
            {"h", "H"}
        };

        private static Dictionary<string, string> reverseSquareNames = squareNames.ToDictionary(x => x.Value, x => x.Key);

        private static Dictionary<string, string> markers = new Dictionary<string, string>()
        {
            {"board", "<!-- BOARD -->"},
            {"moves", "<!-- MOVES -->"},
            {"turn", "<!-- TURN -->"},
            {"last_moves", "<!-- LAST_MOVES -->"},
            {"top_moves", "<!-- TOP_MOVES -->"}
        };

        private static Dictionary<string, string> comments = new Dictionary<string, string>()
        {
            {"successful_new_game", "New game started by {author}"},
            {"invalid_new_game", "Invalid new game request by {author}"},
            {"successful_move", "{author} moved {move}"},
            {"invalid_move", "Invalid move {move} by {author}"},
            {"consecutive_moves", "Two moves in a row by {author}"},
            {"unknown_command", "Unknown command by {author}"},
            {"game_over", "{outcome}! Players: {players}. Total moves: {num_moves}. Total players: {num_players}"}
        };

        private static Dictionary<string, string> issues = new Dictionary<string, string>()
        {
            {"link", "https://github.com/{repo}/issues/new?{params}"},
            {"move", "title=Chess: Move {source} to {dest}"},
            {"new_game", "title=Chess: Start new game"}
        };

        private static Dictionary<string, string> settings = new Dictionary<string, string>()
        {
            {"max_top_moves", "5"},
            {"max_last_moves", "10"}
        };

        private static Dictionary<string, string> data = new Dictionary<string, string>()
        {
            {"top_moves", "data/top_moves.txt"},
            {"last_moves", "data/last_moves.txt"},
            {"settings", "data/settings.yaml"}
        };

        private static Dictionary<string, string> games = new Dictionary<string, string>()
        {
            {"current", "games/current.pgn"}
        };

        private static Dictionary<string, string> images = new Dictionary<string, string>()
        {
            {"r", "img/black/rook.png"},
            {"n", "img/black/knight.png"},
            {"b", "img/black/bishop.png"},
            {"q", "img/black/queen.png"},
            {"k", "img/black/king.png"},
            {"p", "img/black/pawn.png"},
            {"R", "img/white/rook.png"},
            {"N", "img/white/knight.png"},
            {"B", "img/white/bishop.png"},
            {"Q", "img/white/queen.png"},
            {"K", "img/white/king.png"},
            {"P", "img/white/pawn.png"},
            {".", "img/blank.png"}
        };

        private static Dictionary<string, string> squareNames = new Dictionary<string, string>()
        {
            {"a", "A"},
            {"b", "B"},
            {"c", "C"},
            {"d", "D"},
            {"e", "E"},
            {"f", "F"},
            {"g", "G"},
            {"h", "H"}
        };

        private static Dictionary<string, string> reverseSquareNames = squareNames.ToDictionary(x => x.Value, x => x.Key);

        private static Dictionary<string, string> markers = new Dictionary<string, string>()
        {
            {"board", "<!-- BOARD -->"},
            {"moves", "<!-- MOVES -->"},
            {"turn", "<!-- TURN -->"},
            {"last_moves", "<!-- LAST_MOVES -->"},
            {"top_moves", "<!-- TOP_MOVES -->"}
        };

        private static Dictionary<string, string> comments = new Dictionary<string, string>()
        {
            {"successful_new_game", "New game started by {author}"},
            {"invalid_new_game", "Invalid new game request by {author}"},
            {"successful_move", "{author} moved {move}"},
            {"invalid_move", "Invalid move {move} by {author}"},
            {"consecutive_moves", "Two moves in a row by {author}"},
            {"unknown_command", "Unknown command by {author}"},
            {"game_over", "{outcome}! Players: {players}. Total moves: {num_moves}. Total players: {num_players}"}
        };

        private static Dictionary<string, string> issues = new Dictionary<string, string>()
        {
            {"link", "https://github.com/{repo}/issues/new?{params}"},
            {"move", "title=Chess: Move {source} to {dest}"},
            {"new_game", "title=Chess: Start new game"}
        };

        private static Dictionary<string, string> settings = new Dictionary<string, string>()
        {
            {"max_top_moves", "5"},
            {"max_last_moves", "10"}
        };

        private static Dictionary<string, string> data = new Dictionary<string, string>()
        {
            {"top_moves", "data/top_moves.txt"},
            {"last_moves", "data/last_moves.txt"},
            {"settings", "data/settings.yaml"}
        };

        private static Dictionary<string, string> games = new Dictionary<string, string>()
        {
            {"current", "games/current.pgn"}
        };

        private static Dictionary<string, string> images = new Dictionary<string, string>()
        {
            {"r", "img/black/rook.png"},
            {"n", "img/black/knight.png"},
            {"b", "img/black/bishop.png"},
            {"q", "img/black/queen.png"},
        }
    }
}
*/

/*using System;
using System.Collections.Generic;

namespace ChessGame
{
    public class ChessBoard
    {
        private Piece[,] board;

        public ChessBoard()
        {
            board = new Piece[8, 8];
            InitializeBoard();
        }

        private void InitializeBoard()
        {
            // Initialize the board with pieces
        }

        public void MovePiece(int sourceX, int sourceY, int destX, int destY)
        {
            // Move the piece on the board
        }

        public void PrintBoard()
        {
            // Print the current state of the board
        }
    }

    public abstract class Piece
    {
        public abstract bool IsValidMove(int sourceX, int sourceY, int destX, int destY);
    }

    public class Pawn : Piece
    {
        public override bool IsValidMove(int sourceX, int sourceY, int destX, int destY)
        {
            // Check if the move is valid for a pawn
            return false;
        }
    }

    // Implement other chess pieces (Rook, Knight, Bishop, Queen, King)
}

public class Program
{
    public static void Main(string[] args)
    {
        ChessBoard chessBoard = new ChessBoard();
        chessBoard.PrintBoard();

        // Game logic goes here
    }
}
*/
/*using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using Octokit;
using yml;

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
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .Build();
                var settings = deserializer.Deserialize<Settings>(settingsFile);
            }

            if (action.Item1 == Action.NEW_GAME)
            {
                if (File.Exists("games/current.pgn") && issueAuthor != repoOwner)
                {
                    issue.Comment.Create(settings.Comments.InvalidNewGame.Replace("{author}", issueAuthor));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed });
                    return new Tuple<bool, string>(false, "ERROR: A current game is in progress. Only the repo owner can start a new game");
                }

                issue.Comment.Create(settings.Comments.SuccessfulNewGame.Replace("{author}", issueAuthor));
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
                    issue.Comment.Create(settings.Comments.InvalidMove.Replace("{author}", issueAuthor).Replace("{move}", action.Item2));
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
                    issue.Comment.Create(settings.Comments.ConsecutiveMoves.Replace("{author}", issueAuthor));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = { "Invalid" } });
                    return new Tuple<bool, string>(false, "ERROR: Two moves in a row!");
                }

                // Check if move is valid
                if (!move.IsValid(gameboard.LegalMoves))
                {
                    issue.Comment.Create(settings.Comments.InvalidMove.Replace("{author}", issueAuthor).Replace("{move}", action.Item2));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = { "Invalid" } });
                    return new Tuple<bool, string>(false, "ERROR: Move is invalid!");
                }

                // Check if board is valid
                if (!gameboard.IsValid())
                {
                    issue.Comment.Create(settings.Comments.InvalidBoard.Replace("{author}", issueAuthor));
                    issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = { "Invalid" } });
                    return new Tuple<bool, string>(false, "ERROR: Board is invalid!");
                }

                var issueLabels = new List<string> { "⚔️ Capture!" };
                issueLabels.Add(gameboard.Turn == Chess.Color.White ? "White" : "Black");

                issue.Comment.Create(settings.Comments.SuccessfulMove.Replace("{author}", issueAuthor).Replace("{move}", action.Item2));
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
                issue.Comment.Create(settings.Comments.UnknownCommand.Replace("{author}", issueAuthor));
                issue.Edit(new IssueUpdate { State = ItemState.Closed, Labels = { "Invalid" } });
                return new Tuple<bool, string>(false, "ERROR: Unknown action");
            }

            // Save game to "games/current.pgn"
            File.WriteAllText("games/current.pgn", game.ToString() + Environment.NewLine);

            var lastMoves = Markdown.GenerateLastMoves(settings);

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

                issue.Comment.Create(settings.Comments.GameOver.Replace("{outcome}", winMsg.GetValueOrDefault(gameboard.Result, "UNKNOWN"))
                    .Replace("{players}", string.Join(", ", playerList))
                    .Replace("{num_moves}", (lines.Count - 1).ToString())
                    .Replace("{num_players}", playerList.Count.ToString()));

                File.Move("games/current.pgn", $"games/game-{DateTime.Now:yyyyMMdd-HHmmss}.pgn");
                File.Delete("data/last_moves.txt");
            }

            using (var file = new StreamReader("README.md"))
            {
                var readme = file.ReadToEnd();
                readme = ReplaceTextBetween(readme, settings.Markers.Board, "{chess_board}");
                readme = ReplaceTextBetween(readme, settings.Markers.Moves, "{moves_list}");
                readme = ReplaceTextBetween(readme, settings.Markers.Turn, "{turn}");
                readme = ReplaceTextBetween(readme, settings.Markers.LastMoves, "{last_moves}");
                readme = ReplaceTextBetween(readme, settings.Markers.TopMoves, "{top_moves}");

                using (var outFile = new StreamWriter("README.md"))
                {
                    outFile.Write(readme.Replace("{chess_board}", Markdown.BoardToMarkdown(gameboard))
                        .Replace("{moves_list}", Markdown.GenerateMovesList(gameboard, settings))
                        .Replace("{turn}", gameboard.Turn == Chess.Color.White ? "white" : "black")
                        .Replace("{last_moves}", lastMoves)
                        .Replace("{top_moves}", Markdown.GenerateTopMoves(settings)));
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

        public static string ReplaceTextBetween(string originalText, Marker marker, string replacementText)
        {
            var delimiterA = marker.Begin;
            var delimiterB = marker.End;

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
*/
