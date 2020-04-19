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
	internal class Issue
	{
		[DataMember(Name = "number")]
		public long Number;

		[DataMember(Name = "title")]
		public string Title;
	}

	internal class TodoItem
	{
		public int Line;
		public string Body;
		public string File;
		public string Title;
		public TodoDiffType DiffType;
		public IList<string> Labels;

		public TodoItem(string title, int line, string file, int startLines, int endLine, TodoDiffType type, string repo, string sha, IList<string> labels)
		{
			Title = title;
			Line = line;
			File = file;
			DiffType = type;
			Labels = labels;
			if (DiffType == TodoDiffType.Deletion) return;
			Body = $"**{Title}**\n\nLine:{Line}\nhttps://github.com/{repo}/blob/{sha}{file}#L{startLines}-L{endLine}";
		}

		public object RequestBody(string ghIssueLabel = "TODO")
		{
			return new {title = Title, body = Body, labels = new[] {ghIssueLabel}};
		}

		public override string ToString()
		{
			return $"{Title} @ {File}:{Line}";
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
		private const string ApiBase = @"https://api.github.com/repos/";
		private const string CommentPatternToken = "{COMMENT_PATTERN}";
		private const string TodoSignatureToken = "{TODO_SIGNATURE}";
		private const string DiffHeaderPattern = @"(?<=diff\s--git\sa.*b.*).+";
		private const string BlockStartPattern = @"((?<=^@@\s).+(?=\s@@))";
		private const string LineNumPattern = @"(?<=\+).+";
		private const string TodoPatternStart = @"(?<=" + CommentPatternToken + " ?" + TodoSignatureToken + "[ :]).+";

		public static TodoDiffType LineDiffType(string line)
		{
			if (string.IsNullOrWhiteSpace(line)) return TodoDiffType.None;
			return line[0] switch
			{
				'+' => TodoDiffType.Addition,
				'-' => TodoDiffType.Deletion,
				_ => TodoDiffType.None
			};
		}

		private static IEnumerable<Issue> GetActiveItems(string repo, string token)
		{
			var client = new RestClient($"{ApiBase}{repo}/issues?access_token={token}") {Timeout = -1};
			var request = new RestRequest(Method.GET);
			request.AddHeader("Accept", "application/json");
			var ser = new DataContractJsonSerializer(typeof(List<Issue>));
			var response = client.Execute(request);
			if (response.StatusCode != HttpStatusCode.OK)
			{
				Console.WriteLine($"Failed to get active items.\n{response.Content}\n{response.StatusCode}\n{response.StatusDescription}");
				Environment.Exit(1);
			}
			using var sr = new MemoryStream(Encoding.UTF8.GetBytes(response.Content));
			var result = (List<Issue>) ser.ReadObject(sr);
			return result;
		}

		private static string[] GetDiff(string repo, string token, string oldSha, string newSha)
		{
			var client = new RestClient($"{ApiBase}{repo}/compare/{oldSha}...{newSha}?access_token={token}") {Timeout = -1};
			var request = new RestRequest(Method.GET);
			request.AddHeader("Accept", "application/vnd.github.v3.diff");
			var response = client.Execute(request);
			if (response.StatusCode != HttpStatusCode.OK)
			{
				Console.WriteLine($"Failed to get diff.\n{response.Content}\n{response.StatusCode}\n{response.StatusDescription}");
				Environment.Exit(1);
			}
			return response.Content.Split('\n');
		}

		private static IList<string> GetLabels(string line, string pattern)
		{
			var labels = new List<string>();
			var labelsMatch = Regex.Match(line, pattern);
			if (!labelsMatch.Success) return labels;
			foreach (Capture cap in labelsMatch.Captures)
			{
				labels.Add(cap.Value);
			}
			return labels;
		}
		
		private static IList<TodoItem> GetTodoItems(string[] diff, string repo, string sha, string inlineLabelPattern, string inlineLabelReplacePattern, string issueLabel, int linesBefore, int linesAfter, string commentPattern = @"\/\/",
			string todoSignature = "TODO", char[] trimSeparators = null)
		{
			var parseLabels = !string.IsNullOrWhiteSpace(inlineLabelPattern);
			trimSeparators ??= new[] {' ', ':', ' ', '"'};
			var todos = new List<TodoItem>();
			var lineNumber = 0;
			var currFile = "";
			foreach (var line in diff)
			{
				var headerMatch = Regex.Match(line, DiffHeaderPattern, RegexOptions.IgnoreCase);
				if (headerMatch.Success) { currFile = headerMatch.Value; }
				else
				{
					var blockStartMatch = Regex.Match(line, BlockStartPattern, RegexOptions.IgnoreCase);
					if (blockStartMatch.Success)
					{
						var lineNumsMatch = Regex.Match(blockStartMatch.Value, LineNumPattern);
						if (lineNumsMatch.Success) { lineNumber = int.Parse(lineNumsMatch.Groups[0].Value.Split(',')[0]); }
					}
					else
					{
						var todoMatch = Regex.Match(line, TodoPatternStart.Replace(CommentPatternToken, commentPattern).Replace(TodoSignatureToken, todoSignature));
						if (todoMatch.Success)
						{
							var todoType = LineDiffType(line);
							if (todoType == TodoDiffType.None) continue;
							var labels = new List<string> {issueLabel};
							var title = todoMatch.Value.Trim(trimSeparators).TrimEnd();
							if (parseLabels)
							{
								var inlineLabels = GetLabels(line, inlineLabelPattern);
								title = Regex.Replace(title, inlineLabelReplacePattern, "");
								labels.AddRange(inlineLabels);
							}
							todos.Add(new TodoItem(title, lineNumber, currFile, Math.Max(lineNumber - linesBefore, 0),
								lineNumber + linesAfter, todoType, repo, sha, labels));
						}
						lineNumber++;
					}
				}
			}
			return todos;
		}

		private static void HandleTodos(string repo, string token, string newSha, string ghIssueLabel, int timeout, IEnumerable<TodoItem> todos)
		{
			var activeIssues = GetActiveItems(repo, token);
			var deletions = todos.Where(t => t.DiffType == TodoDiffType.Deletion).ToList();
			var additions = todos.Where(t => t.DiffType == TodoDiffType.Addition).ToList();
			foreach (var number in activeIssues.Where(i => deletions.Select(d => d.Title).Contains(i.Title)).Select(i => i.Number))
			{
				var client = new RestClient($"{ApiBase}{repo}/issues/{number}?access_token={token}") {Timeout = -1};
				var request = new RestRequest(Method.PATCH);
				request.AddHeader("Accept", "application/json");
				request.AddJsonBody(new {state = "closed"});
				var response = client.Execute(request);
				if (response.StatusCode != HttpStatusCode.OK)
				{
					Console.WriteLine($"Failed to close GH issue #{number}.\n{response.Content}\n{response.StatusCode}\n{response.StatusDescription}");
					Environment.Exit(1);
				}
				request = new RestRequest(Method.POST);
				request.AddHeader("Accept", "application/json");
				request.AddJsonBody(new {body = $"Closed automatically with {newSha}"});
				client = new RestClient($"{ApiBase}{repo}/issues/{number}/comments?access_token={token}") {Timeout = -1};
				response = client.Execute(request);
				if (response.StatusCode != HttpStatusCode.Created)
				{
					Console.WriteLine($"Failed to post GH comment for issue #{number}.\n{response.Content}\n{response.StatusCode}\n{response.StatusDescription}");
					Environment.Exit(1);
				}
				Thread.Sleep(timeout);
			}
			foreach (var todoItem in additions)
			{
				Thread.Sleep(timeout);
				var client = new RestClient($"{ApiBase}{repo}/issues?access_token={token}") {Timeout = -1};
				var request = new RestRequest(Method.POST);
				request.AddHeader("Accept", "application/json");
				request.AddJsonBody(todoItem.RequestBody(ghIssueLabel));
				var response = client.Execute(request);
				if (response.StatusCode != HttpStatusCode.Created)
				{
					Console.WriteLine($"Failed to create GH issue for {todoItem}.\n{response.Content}\n{response.StatusCode}\n{response.StatusDescription}");
					Environment.Exit(1);
				}
			}
		}

		private static void Main(string[] args)
		{
			Console.WriteLine("Parsing parameters.");
			var repo = Environment.GetEnvironmentVariable("INPUT_REPOSITORY") ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
			var newSha = Environment.GetEnvironmentVariable("INPUT_SHA") ?? Environment.GetEnvironmentVariable("GITHUB_SHA");
			var oldSha = Environment.GetEnvironmentVariable("INPUT_BASE_SHA");
			var token = Environment.GetEnvironmentVariable("INPUT_TOKEN");
			var todoLabel = Environment.GetEnvironmentVariable("INPUT_TODO");
			var commentPattern = Environment.GetEnvironmentVariable("INPUT_COMMENT");
			var inlineLabelPattern = Environment.GetEnvironmentVariable("INPUT_LABELS_PATTERN");
			var inlineLabelReplacePattern = Environment.GetEnvironmentVariable("INPUT_LABELS_REPLACE_PATTERN");
			var ghIssueLabel = Environment.GetEnvironmentVariable("INPUT_GITHUB_LABEL");
			var symbolsToTrim = Environment.GetEnvironmentVariable("INPUT_TRIM");
			if (!bool.TryParse(Environment.GetEnvironmentVariable("INPUT_NOPUBLISH"), out var nopublish)) { nopublish = false; }
			else { Console.WriteLine("No publishing result mode."); }
			if (!int.TryParse(Environment.GetEnvironmentVariable("INPUT_TIMEOUT"), out var timeout)) { timeout = 1000; }
			if (!int.TryParse(Environment.GetEnvironmentVariable("INPUT_LINES_BEFORE"), out var linesBefore)) { linesBefore = 3; }
			linesBefore = Math.Clamp(linesBefore, 0, 15);
			if (!int.TryParse(Environment.GetEnvironmentVariable("INPUT_LINES_AFTER"), out var linesAfter)) { linesAfter = 7; }
			linesAfter = Math.Clamp(linesAfter, 0, 15);
			Console.WriteLine("Repository:\t{0}", repo);
			Console.WriteLine("Old SHA:\t{0}", oldSha);
			Console.WriteLine("New SHA:\t{0}", newSha);
			Console.WriteLine("Token:\t{0}", token?[0] + string.Join("", Enumerable.Repeat('*', token.Length - 2)) + token?[^1]);
			Console.WriteLine("TODO:\t{0}", todoLabel);
			Console.WriteLine("Comment regular expression:\t{0}", commentPattern);
			Console.WriteLine("Inline label regular expression:\t{0}", inlineLabelPattern);
			Console.WriteLine("Inline label replace regular expression:\t{0}", inlineLabelReplacePattern);
			Console.WriteLine("GH Label:\t{0}", ghIssueLabel);
			Console.WriteLine("Trim:\t{0}", symbolsToTrim);
			Console.WriteLine("Timeout:\t{0}", timeout);
			Console.WriteLine("Lines of code before todo to include to snippet:\t{0}", linesBefore);
			Console.WriteLine("Lines of code after todo to include to snippet:\t{0}", linesAfter);
			Console.WriteLine("Getting diff.");
			if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(oldSha) || string.IsNullOrWhiteSpace(newSha) || string.IsNullOrWhiteSpace(token))
			{
				Console.WriteLine("Failed to parse required parameters (repository, SHAs of commits, token).");
				Console.WriteLine("Aborting.");
				Environment.Exit(1);
			}
			var diff = GetDiff(repo, token, oldSha, newSha);
			var todos = GetTodoItems(diff, repo, newSha, inlineLabelPattern, inlineLabelReplacePattern, ghIssueLabel, linesBefore, linesAfter, commentPattern, todoLabel, symbolsToTrim?.ToCharArray());
			Console.WriteLine("Parsed new TODOs:");
			foreach (var todoItem in todos.Where(t => t.DiffType == TodoDiffType.Addition)) { Console.WriteLine($"+\t{todoItem}"); }
			Console.WriteLine("Parsed removed TODOs:");
			foreach (var todoItem in todos.Where(t => t.DiffType == TodoDiffType.Deletion)) { Console.WriteLine($"-\t{todoItem}"); }
			if (!nopublish) { HandleTodos(repo, token, newSha, ghIssueLabel, timeout, todos); }
			Console.WriteLine("Finished updating issues.");
		}
	}
}
