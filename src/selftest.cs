using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
/*using YamlDotNet.Serialization;*/

namespace MockGithub.Self
{
    public class SelfProgram
    {
        public static void Main(string[] args)
        {
            int passed = 0;
            int failed = 0;

            foreach (string file in Directory.GetFiles("tests/", "*.yml"))
            {
                (int passedTmp, int failedTmp) = RunTestCase(file, MainFn);
                passed += passedTmp;
                failed += failedTmp;
            }

            int total = passed + failed;

            Console.WriteLine();
            Console.WriteLine($"    {total} total");
            Console.WriteLine($"   {passed} passed");
            Console.WriteLine($"   {failed} failed");
        }

        public static void MainFn(Issue issue, string issueAuthor, string repoOwner)
        {
            /*var moveData = new Dictionary<string, string>(); // Assuming you have a dictionary similar to move_data in Python
        
            var (result, reason) = issue.ExpectationsFulfilled();
            
            if (result)
            {
                Console.WriteLine($"\u001b[0m    \u001b[1m\u001b[32m✓ \u001b[0m\u001b[37m{moveData["move"]} by {moveData["author"]}\u001b[0m");
                passed++;
            }
            else
            {
                Console.WriteLine($"\u001b[0m    \u001b[1m\u001b[31m✗ \u001b[0m\u001b[37m{moveData["move"]} by {moveData["author"]} \u001b[1m→ \u001b[31m{reason}\u001b[0m");
                failed++;
            }*/
        }

        public static (int, int) RunTestCase(string filename, Action<Issue, string, string> mainFn)
        {
            int passed = 0;
            int failed = 0;

            using (StreamReader file = new StreamReader(filename))
            {
                var deserializer = new DeserializerBuilder().Build();
                var test_data = deserializer.Deserialize<Dictionary<string, object>>(file);

                using (StreamReader settingsFile = new StreamReader("data/settings.yaml"))
                {
                    var settings = deserializer.Deserialize<Dictionary<string, object>>(settingsFile);

                    Console.WriteLine($"{test_data["name"]}");

                    for (int i = 0; i < ((List<object>)test_data["moves"]).Count; i++)
                    {
                        var move_data = (Dictionary<string, object>)((List<object>)test_data["moves"])[i];
                        var (labels, comments) = GetTestData(settings, move_data, (string)test_data["owner"], i);

                        Issue issue = new Issue((string)move_data["move"]);
                        issue.ExpectLabels(labels);
                        issue.ExpectComments(comments);

                        string issueAuthor = (string)move_data["author"];
                        string repoOwner = (string)test_data["owner"];
                        Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", repoOwner.Substring(1) + "/" + repoOwner.Substring(1));

                        mainFn(issue, issueAuthor, repoOwner);

                        var (result, reason) = issue.ExpectationsFulfilled();
                        if (result)
                        {
                            Console.WriteLine($"    ✓ {move_data["move"]} by {move_data["author"]}");
                            passed++;
                        }
                        else
                        {
                            Console.WriteLine($"    ✓ {move_data["move"]} by {move_data["author"]} → {reason}");
                            failed++;
                        }
                    }
                }
            }

            return (passed, failed);
        }

        public static (List<string>, List<string>) GetTestData(Dictionary<string, object> settings, Dictionary<string, object> move_data, string owner, int i)
        {
            List<string> labels = new List<string>();
            List<string> comments = new List<string>();

            if (move_data["move"].ToString().Contains("Start new game"))
            {
                if (move_data["author"].ToString() == owner)
                {
                    comments.Add(settings["comments"]["successful_new_game"].ToString().Replace("{author}", "@.+"));
                }
                else
                {
                    comments.Add(settings["comments"]["invalid_new_game"].ToString().Replace("{author}", "@.+"));
                }
            }
            else if ((!move_data.ContainsKey("is_consecutive") || (bool)move_data["is_consecutive"] == false) && (!move_data.ContainsKey("is_invalid") || (bool)move_data["is_invalid"] == false))
            {
                labels.Add(i % 2 == 1 ? "White" : "Black");
                comments.Add(settings["comments"]["successful_move"].ToString().Replace("{author}", "@.+").Replace("{move}", ".....?"));
            }

            if (move_data.ContainsKey("is_winner") && (bool)move_data["is_winner"] == true)
            {
                labels.Add("👑 Winner!");
                comments.Add(settings["comments"]["game_over"].ToString().Replace("{outcome}", ".+").Replace("{num_moves}", "\\d+").Replace("{num_players}", "\\d+").Replace("{players}", "(@.+,)* @.+"));
            }

            if (move_data.ContainsKey("is_draw") && (bool)move_data["is_draw"] == true)
            {
                labels.Add("👑 Draw!");
                comments.Add(settings["comments"]["game_over"].ToString().Replace("{outcome}", ".+").Replace("{num_moves}", "\\d+").Replace("{num_players}", "\\d+").Replace("{players}", "(@.+,)* @.+"));
            }

            if (move_data.ContainsKey("is_capture") && (bool)move_data["is_capture"] == true)
            {
                labels.Add("⚔️ Capture!");
            }

            if (move_data.ContainsKey("is_consecutive") && (bool)move_data["is_consecutive"] == true)
            {
                labels.Add("Invalid");
                comments.Add(settings["comments"]["consecutive_moves"].ToString().Replace("{author}", "@.+"));
            }

            if (move_data.ContainsKey("is_invalid") && (bool)move_data["is_invalid"] == true)
            {
                labels.Add("Invalid");
                string comment = Regex.Escape(settings["comments"]["invalid_move"].ToString()).Replace("\\{", "{").Replace("\\}", "}");
                comments.Add(comment.Replace("{author}", "@.+").Replace("{move}", ".+"));
            }

            return (labels, comments);
        }
    }

    public class Issue
    {
        public string Move { get; set; }
        public List<string> Labels { get; set; }
        public List<string> Comments { get; set; }

        public Issue(string move)
        {
            Move = move;
            Labels = new List<string>();
            Comments = new List<string>();
        }

        public void ExpectLabels(List<string> labels)
        {
            Labels.AddRange(labels);
        }

        public void ExpectComments(List<string> comments)
        {
            Comments.AddRange(comments);
        }

        public (bool, string) ExpectationsFulfilled()
        {
            // Your implementation here
            return (true, "");
        }
    }
}
