using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RestSharp;

namespace Todo2GhIssue
{
    [DataContract]
    public class GhEvent
    {
        [DataMember(Name = "after")] public string After;

        [DataMember(Name = "before")] public string Before;

        [DataMember(Name = "forced")] public bool Forced;

        [DataMember(Name = "pusher")] public Pusher Pusher;

        [DataMember(Name = "repository")] public Repository Repository;
    }

    [DataContract]
    public class Pusher
    {
        [DataMember(Name = "email")] public string Email;

        [DataMember(Name = "name")] public string Name;
    }

    [DataContract]
    public class Repository
    {
        [DataMember(Name = "full_name")] public string FullName;
    }

    [DataContract]
    internal class Issue
    {
        [DataMember(Name = "number")] public long Number;
        [DataMember(Name = "title")] public string Title;
    }

    internal class TodoItem
    {
        private readonly string _body;
        private readonly IList<string> _labels;
        private readonly int _line;
        public readonly TodoDiffType DiffType;
        public readonly string File;
        public readonly string Title;

        public TodoItem(string title, int line, string file, int startLines, int endLine, TodoDiffType type,
            string repo, string sha, IList<string> labels)
        {
            Title = title;
            _line = line;
            File = file;
            DiffType = type;
            _labels = labels;
            _body =
                $"**{Title}**\n\nLine: {_line}\nhttps://github.com/{repo}/blob/{sha}{File}#L{startLines}-L{endLine}";
        }

        public object RequestBody(string pusher = "")
        {
            return new {title = Title, body = _body + "\n\n" + pusher, labels = _labels.ToArray()};
        }

        public override string ToString()
        {
            return $"{Title} @ {File}:{_line} (Labels: {string.Join(", ", _labels)})";
        }
    }

    internal enum TodoDiffType
    {
        None,
        Addition,
        Deletion
    }


    internal static class Program
    {
        private const string ConsoleSeparator = "------------------------------------------";
        private const string ApiBase = @"https://api.github.com/repos/";
        private const string DiffHeaderPattern = @"(?<=diff\s--git\sa.*b.*).+";
        private const string BlockStartPattern = @"((?<=^@@\s).+(?=\s@@))";
        private const string LineNumPattern = @"(?<=\+).+";

        private static IEnumerable<Issue> GetActiveItems(Parameters parameters)
        {
            var client = new RestClient($"{ApiBase}{parameters.Repository}/issues") {Timeout = -1};
            var request = new RestRequest(Method.GET);
            request.AddHeader("Accept", "application/json");
            request.AddHeader("Authorization", $"token {parameters.GithubToken}");
            var ser = new DataContractJsonSerializer(typeof(List<Issue>));
            var response = client.Execute(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Console.WriteLine(
                    $"Failed to get active items.\n{response.Content}\n{response.StatusCode}\n{response.StatusDescription}");
                Environment.Exit(1);
            }

            using var sr = new MemoryStream(Encoding.UTF8.GetBytes(response.Content));
            var result = (List<Issue>) ser.ReadObject(sr);
            return result;
        }

        private static IEnumerable<string> GetDiff(Parameters parameters)
        {
            var client =
                new RestClient($"{ApiBase}{parameters.Repository}/compare/{parameters.OldSha}...{parameters.NewSha}")
                    {Timeout = -1};
            var request = new RestRequest(Method.GET);
            request.AddHeader("Authorization", $"token {parameters.GithubToken}");
            request.AddHeader("Accept", "application/vnd.github.v3.diff");
            var response = client.Execute(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Console.WriteLine(
                    $"Failed to get diff: {response.Content} - {response.StatusDescription} ({response.StatusCode})");
                Environment.Exit(1);
            }

            return response.Content.Split('\n');
        }

        private static IEnumerable<string> GetLabels(string line, string pattern)
        {
            var labels = new List<string>();
            var labelsMatches = Regex.Matches(line, pattern);
            labels.AddRange(labelsMatches.Select(cap => cap.Value));
            return labels;
        }

        private static IList<TodoItem> GetTodoItems(Parameters parameters, IEnumerable<string> diff)
        {
            var parseLabels = !string.IsNullOrWhiteSpace(parameters.InlineLabelRegex);
            var trimSeparators = parameters.TrimmedCharacters?.Length == 0
                ? new[] {' ', ':', ' ', '"'}
                : parameters.TrimmedCharacters;
            var todos = new List<TodoItem>();
            var lineNumber = 0;
            var currFile = "";

            var excludedPathsCount = parameters.ExcludedPaths?.Length ?? 0;
            var includedPathsCount = parameters.IncludedPaths?.Length ?? 0;

            foreach (var line in diff)
            {
                if (parameters.MaxDiffLineLength > 0 && line.Length > parameters.MaxDiffLineLength)
                {
                    if (!line.StartsWith('-')) lineNumber++;
                    continue;
                }

                var headerMatch = Regex.Match(line, DiffHeaderPattern, RegexOptions.IgnoreCase);
                if (headerMatch.Success)
                {
                    currFile = Regex.Matches(headerMatch.Value, @"(?<=)\/.+ b(\/.*)$")[0].Groups[1].Value;
                }
                else if (!string.IsNullOrWhiteSpace(currFile))
                {
                    if (excludedPathsCount > 0
                        && includedPathsCount == 0
                        && parameters.ExcludedPaths.ToList().Any(excl => currFile.StartsWith(value:excl, StringComparison.OrdinalIgnoreCase))
                        || excludedPathsCount == 0
                        && includedPathsCount > 0
                        && !parameters.IncludedPaths.ToList().Any(incl => currFile.StartsWith(incl, StringComparison.OrdinalIgnoreCase))
                        || excludedPathsCount > 0
                        && includedPathsCount > 0
                        && parameters.ExcludedPaths.ToList().Any(excl => currFile.StartsWith(excl, StringComparison.OrdinalIgnoreCase))
                        && !parameters.IncludedPaths.ToList()
                            .Any(incl => currFile.StartsWith(incl, StringComparison.OrdinalIgnoreCase)))
                    {
                        currFile = "";
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(parameters.FileRegex) &&
                        !Regex.Match(currFile, parameters.FileRegex, RegexOptions.IgnoreCase).Success)
                        continue;

                    var blockStartMatch = Regex.Match(line, BlockStartPattern, RegexOptions.IgnoreCase);
                    if (blockStartMatch.Success)
                    {
                        var lineNumsMatch = Regex.Match(blockStartMatch.Value, LineNumPattern);
                        if (lineNumsMatch.Success) lineNumber = int.Parse(lineNumsMatch.Groups[0].Value.Split(',')[0]);
                    }
                    else
                    {
                        var todoMatch = Regex.Match(line, parameters.TodoRegex);
                        if (todoMatch.Success)
                        {
                            var todoType = LineDiffType(line);
                            if (todoType == TodoDiffType.None) continue;
                            var labels = new List<string> {parameters.LabelToAdd};
                            var title = todoMatch.Value.Trim(trimSeparators);
                            if (parseLabels)
                            {
                                var inlineLabels = GetLabels(line, parameters.InlineLabelRegex);
                                title = Regex.Replace(title, parameters.InlineLabelReplaceRegex, "");
                                labels.AddRange(inlineLabels);
                            }

                            todos.Add(new TodoItem(title.Trim(), lineNumber, currFile,
                                Math.Max(lineNumber - parameters.LinesBefore, 0), lineNumber + parameters.LinesAfter,
                                todoType, parameters.Repository,
                                parameters.NewSha, labels));
                        }

                        if (!line.StartsWith('-')) lineNumber++;
                    }
                }
            }

            return todos;
        }

        private static void HandleTodos(Parameters parameters, IList<TodoItem> todos)
        {
            var activeIssues = GetActiveItems(parameters);
            var deletions = todos.Where(t => t.DiffType == TodoDiffType.Deletion).ToList();
            var additions = todos.Where(t => t.DiffType == TodoDiffType.Addition).ToList();
            foreach (var number in activeIssues.Where(i => deletions.Select(d => d.Title).Contains(i.Title))
                .Select(i => i.Number))
            {
                var client = new RestClient($"{ApiBase}{parameters.Repository}/issues/{number}") {Timeout = -1};
                var request = new RestRequest(Method.PATCH);
                request.AddHeader("Authorization", $"token {parameters.GithubToken}");
                request.AddHeader("Accept", "application/json");
                request.AddJsonBody(new {state = "closed"});
                var response = client.Execute(request);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    Console.WriteLine(
                        $"Failed to close GH issue #{number}.\n{response.Content}\n{response.StatusCode}\n{response.StatusDescription}");
                    Environment.Exit(1);
                }

                client = new RestClient(
                        $"{ApiBase}{parameters.Repository}/issues/{number}/comments")
                    {Timeout = -1};
                request = new RestRequest(Method.POST);
                request.AddHeader("Accept", "application/json");
                request.AddHeader("Authorization", $"token {parameters.GithubToken}");
                request.AddJsonBody(
                    new {body = $"Closed automatically with {parameters.NewSha} by {parameters.Author}"});
                response = client.Execute(request);
                if (response.StatusCode != HttpStatusCode.Created)
                {
                    Console.WriteLine(
                        $"Failed to post GH comment for issue #{number}.\n{response.Content}\n{response.StatusCode}\n{response.StatusDescription}");
                    Environment.Exit(1);
                }
                else
                {
                    Console.WriteLine($"Closed issue #{number}");
                }

                Thread.Sleep(parameters.Timeout);
            }

            foreach (var todoItem in additions)
            {
                Thread.Sleep(parameters.Timeout);
                var client =
                    new RestClient($"{ApiBase}{parameters.Repository}/issues")
                        {Timeout = -1};
                var request = new RestRequest(Method.POST);
                request.AddHeader("Authorization", $"token {parameters.GithubToken}");
                request.AddHeader("Accept", "application/json");
                request.AddJsonBody(
                    todoItem.RequestBody($"{(parameters.Forced ? "Force-pushed" : "Pushed")} by {parameters.Author}"));
                var response = client.Execute(request);
                if (response.StatusCode != HttpStatusCode.Created)
                {
                    Console.WriteLine(
                        $"Failed to create GH issue for {todoItem}.\n{response.Content}\n{response.StatusCode}\n{response.StatusDescription}\nRequest:{request.Body.Value}");
                    Environment.Exit(1);
                }
                else
                {
                    Console.WriteLine($"Created new issue #{todoItem.Title}");
                }
            }
        }

        private static TodoDiffType LineDiffType(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return TodoDiffType.None;
            return line[0] switch
            {
                '+' => TodoDiffType.Addition,
                '-' => TodoDiffType.Deletion,
                _ => TodoDiffType.None
            };
        }

        private static Parameters ParseParameters()
        {
            var parameters = new Parameters();

            parameters.GithubEventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
            if (!string.IsNullOrWhiteSpace(parameters.GithubEventPath))
            {
                parameters.NoGithubEventData = true;
                var eventData = File.ReadAllText(parameters.GithubEventPath);
                var ser = new DataContractJsonSerializer(typeof(GhEvent));
                using var sr = new MemoryStream(Encoding.UTF8.GetBytes(eventData));
                var githubEvent = (GhEvent) ser.ReadObject(sr);
                parameters.OldSha = githubEvent.Before;
                parameters.NewSha = githubEvent.After;
                parameters.Repository = githubEvent.Repository.FullName;
                parameters.Author = $"{githubEvent.Pusher.Name} <{githubEvent.Pusher.Email}>";
                parameters.Forced = githubEvent.Forced;
            }

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INPUT_REPOSITORY")))
                parameters.Repository = Environment.GetEnvironmentVariable("INPUT_REPOSITORY");

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INPUT_SHA")))
                parameters.NewSha = Environment.GetEnvironmentVariable("INPUT_SHA");
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INPUT_BASE_SHA")))
                parameters.OldSha = Environment.GetEnvironmentVariable("INPUT_BASE_SHA");

            parameters.GithubToken = Environment.GetEnvironmentVariable("INPUT_TOKEN");
            parameters.TodoRegex = Environment.GetEnvironmentVariable("INPUT_TODO_PATTERN") ?? @"\/\/ TODO";
            parameters.IgnoredRegex = Environment.GetEnvironmentVariable("INPUT_IGNORE_PATH_PATTERN");
            parameters.InlineLabelRegex = Environment.GetEnvironmentVariable("INPUT_LABELS_PATTERN");
            parameters.InlineLabelReplaceRegex = Environment.GetEnvironmentVariable("INPUT_LABELS_REPLACE_PATTERN");
            parameters.LabelToAdd = Environment.GetEnvironmentVariable("INPUT_GH_LABEL");
            parameters.FileRegex = Environment.GetEnvironmentVariable("INPUT_FILE_PATTERN");
            parameters.TrimmedCharacters = Environment.GetEnvironmentVariable("INPUT_TRIM")?.ToCharArray();

            if (!bool.TryParse(Environment.GetEnvironmentVariable("INPUT_NOPUBLISH"), out parameters.NoPublish))
                parameters.NoPublish = false;

            parameters.Timeout =
                !int.TryParse(Environment.GetEnvironmentVariable("INPUT_TIMEOUT"), out parameters.Timeout)
                    ? 1000
                    : Math.Clamp(parameters.Timeout, 1, 3000);

            parameters.MaxDiffLineLength =
                !int.TryParse(Environment.GetEnvironmentVariable("INPUT_IGNORED_LINES_LENGTH"),
                    out parameters.MaxDiffLineLength)
                    ? 255
                    : Math.Max(parameters.MaxDiffLineLength, 1);

            parameters.LinesBefore =
                !int.TryParse(Environment.GetEnvironmentVariable("INPUT_LINES_BEFORE"), out parameters.LinesBefore)
                    ? 3
                    : Math.Clamp(parameters.LinesBefore, 0, 15);

            parameters.LinesAfter =
                !int.TryParse(Environment.GetEnvironmentVariable("INPUT_LINES_AFTER"), out parameters.LinesAfter)
                    ? 7
                    : Math.Clamp(parameters.LinesAfter, 0, 15);

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INPUT_INCLUDED_PATHS")))
                parameters.IncludedPaths = Environment.GetEnvironmentVariable("INPUT_INCLUDED_PATHS")
                    ?.Split('|', StringSplitOptions.RemoveEmptyEntries);

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INPUT_EXCLUDED_PATHS")))
                parameters.ExcludedPaths = Environment.GetEnvironmentVariable("INPUT_EXCLUDED_PATHS")
                    ?.Split('|', StringSplitOptions.RemoveEmptyEntries);


            return parameters;
        }

        private static void PrintParameters(Parameters parameters)
        {
            Console.WriteLine(ConsoleSeparator);
            Console.WriteLine("Repository:\t{0}", parameters.Repository);
            Console.WriteLine("Old SHA:\t{0}", parameters.OldSha);
            Console.WriteLine("New SHA:\t{0}", parameters.NewSha);
            Console.WriteLine("Token:\t{0}",
                parameters.GithubToken?[0] +
                string.Join("", Enumerable.Repeat('*', parameters.GithubToken?.Length ?? 0 - 2)) +
                parameters.GithubToken?[^1]);
            Console.WriteLine("TODO regular expression:\t{0}", parameters.TodoRegex);
            Console.WriteLine("Ignore path regular expression:\t{0}", parameters.IgnoredRegex);
            Console.WriteLine("Inline label regular expression:\t{0}", parameters.InlineLabelRegex);
            Console.WriteLine("Inline label replace regular expression:\t{0}", parameters.InlineLabelReplaceRegex);
            Console.WriteLine("GH Label:\t{0}", parameters.LabelToAdd);
            Console.WriteLine("Trimmed Characters:\t{0}", $"{{{string.Join("", parameters.TrimmedCharacters)}}}");
            Console.WriteLine("Timeout:\t{0}", parameters.Timeout);
            Console.WriteLine("Snippet size:");
            Console.WriteLine("Lines before todo:\t{0}", parameters.LinesBefore);
            Console.WriteLine("Lines after todo:\t{0}", parameters.LinesAfter);
            Console.WriteLine("Maximum processed line length:\t{0}", parameters.MaxDiffLineLength);
            Console.WriteLine("Regex to filter files:\t{0}", parameters.FileRegex);

            var excludedPathsCount = parameters.ExcludedPaths?.Length ?? 0;
            var includedPathsCount = parameters.IncludedPaths?.Length ?? 0;

            Console.WriteLine("List of included paths:");
            for (var i = 0; i < includedPathsCount; i++)
                Console.WriteLine($"{i + 1}:\t{parameters.IncludedPaths[i]}");
            Console.WriteLine("List of excluded paths:");
            for (var i = 0; i < excludedPathsCount; i++)
                Console.WriteLine($"{i + 1}:\t{parameters.ExcludedPaths[i]}");

            if (excludedPathsCount > 0 && includedPathsCount == 0)
                Console.WriteLine(
                    "All found TODOs excluding TODOs in files, which are in a list of excluded paths are handled.");
            else if (excludedPathsCount == 0 && includedPathsCount > 0)
                Console.WriteLine("Only TODOs in files which are under paths in a list of included paths are handled.");
            else if (excludedPathsCount > 0 && includedPathsCount > 0)
                Console.WriteLine(
                    "All found TODOs excluding TODOs in files, which are in a list of excluded paths are handled (list of included paths is a list of exceptions).");
        }

        private static bool CheckParameters(Parameters parameters)
        {
            return !(string.IsNullOrWhiteSpace(parameters.Repository)
                     || string.IsNullOrWhiteSpace(parameters.TodoRegex)
                     || string.IsNullOrWhiteSpace(parameters.OldSha)
                     || string.IsNullOrWhiteSpace(parameters.NewSha)
                     || string.IsNullOrWhiteSpace(parameters.GithubToken));
        }

        private static void PrintTodos(IList<TodoItem> todos)
        {
            Console.WriteLine($"Parsed new TODOs ({todos.Count}):");
            foreach (var todoItem in todos.Where(t => t.DiffType == TodoDiffType.Addition))
                Console.WriteLine($"+\t{todoItem}");
            Console.WriteLine("Parsed removed TODOs:");
            foreach (var todoItem in todos.Where(t => t.DiffType == TodoDiffType.Deletion))
                Console.WriteLine($"-\t{todoItem}");
        }

        private static void Main()
        {
            Console.WriteLine("Parsing parameters.");
            var parameters = ParseParameters();
            PrintParameters(parameters);
            Console.WriteLine(ConsoleSeparator);
            if (!CheckParameters(parameters))
            {
                Console.WriteLine("Failed to read some of the mandatory parameters. Aborting.");
                Environment.Exit(1);
            }

            var diff = GetDiff(parameters);
            var todos = GetTodoItems(parameters, diff);
            PrintTodos(todos);
            if (!parameters.NoPublish) HandleTodos(parameters, todos);
            Console.WriteLine(ConsoleSeparator);
            Console.WriteLine("Finished updating issues. Thanks for using this tool :)");
        }

        private class Parameters
        {
            public string Author;
            public string[] ExcludedPaths;
            public string FileRegex;
            public bool Forced;
            public string GithubEventPath;
            public string GithubToken;
            public string IgnoredRegex;
            public string[] IncludedPaths;
            public string InlineLabelRegex;
            public string InlineLabelReplaceRegex;
            public string LabelToAdd;
            public int LinesAfter;
            public int LinesBefore;
            public int MaxDiffLineLength;
            public string NewSha;
            public bool NoGithubEventData;
            public bool NoPublish;
            public string OldSha;
            public string Repository;
            public int Timeout;
            public string TodoRegex;
            public char[] TrimmedCharacters;
        }
    }
}