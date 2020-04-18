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

		public TodoItem(string title, int line, string file, string[] bodyArray, TodoDiffType type, string repo, string sha, string syntax = "csharp")
		{
			Title = title;
			Line = line;
			File = file;
			DiffType = type;
			if (DiffType == TodoDiffType.Deletion) return;
			var body = new List<string>();
			for (var i = 0; i < bodyArray.Length; i++)
			{
				if (string.IsNullOrWhiteSpace(bodyArray[i].TrimStart('+', ' '))) continue;
				body.Add(bodyArray[i].TrimStart('+', ' '));
			}
			Body = $"https://github.com/{repo}/blob/{sha}{file}#L{line}";
			if (body.Count == 0) return;
			var min = -1;
			var sep = '\t';
			body.ForEach(s =>
			{
				var count = 1;
				while (count + 1 < s.Length && s[count + 1].Equals(sep)) count++;
				min = min < 0 || count < min ? count : min;
			});
			var addBody = "";
			body.ForEach(s => { addBody += s.Substring(min) + '\n'; });
			Body += $"\n\n```{syntax}\n{addBody.TrimEnd()}```";
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
		Addition,
		Deletion
	}

	internal class Program
	{
		private const string ApiBase = @"https://api.github.com/repos/";
		private const string CommentPatternToken = "{COMMENT_PATTERN}";
		private const string TodoSignatureToken = "{TODO_SIGNATURE}";
		private const string DiffHeaderPattern = @"(?<=diff\s--git\sa.*b.*).+";
		private const string BlockStartPattern = @"((?<=^@@\s).+(?=\s@@))";
		private const string LineNumPattern = @"(?<=\+).+";
		private const string AdditionPattern = @"(?<=^\+).*";
		private const string DeletionPattern = @"(?<=^-).*";
		private const string TodoPatternStart = @"(?<=" + CommentPatternToken + " ?" + TodoSignatureToken + "[ :]).+";

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

		private static IList<TodoItem> GetTodoItems(string[] diff, string repo, string sha, string syntax, string commentPattern = @"\/\/", string todoSignature = "TODO",
			char[] trimSeparators = null)
		{
			trimSeparators ??= new[] {' ', ':', ' ', '(', '"'};
			var todos = new List<TodoItem>();
			var lineNumber = 0;
			var currFile = "";
			var currentDiffLineNum = 0;
			var blockStartLineNum = 0;
			foreach (var line in diff)
			{
				currentDiffLineNum++;
				var headerMatch = Regex.Match(line, DiffHeaderPattern, RegexOptions.IgnoreCase);
				if (headerMatch.Success) { currFile = headerMatch.Value; }
				else
				{
					var blockStartMatch = Regex.Match(line, BlockStartPattern, RegexOptions.IgnoreCase);
					if (blockStartMatch.Success)
					{
						blockStartLineNum = currentDiffLineNum;
						var lineNumsMatch = Regex.Match(blockStartMatch.Value, LineNumPattern);
						if (lineNumsMatch.Success) { lineNumber = int.Parse(lineNumsMatch.Groups[0].Value.Split(',')[0]); }
					}
					else
					{
						lineNumber++;
						var todoMatch = Regex.Match(line, TodoPatternStart.Replace(CommentPatternToken, commentPattern).Replace(TodoSignatureToken, todoSignature));
						if (todoMatch.Success)
						{
							var copyStartIndex = Math.Max(currentDiffLineNum - 2, blockStartLineNum);
							var copyEndIndex = Math.Min(currentDiffLineNum + 2, diff.Length - 1);
							var copyLength = copyEndIndex - copyStartIndex;
							var bodyArray = new string[copyLength];
							Array.Copy(diff, copyStartIndex, bodyArray, 0, copyLength);
							var additionMatch = Regex.Match(line, AdditionPattern, RegexOptions.IgnoreCase);
							var deletionMatch = Regex.Match(line, DeletionPattern, RegexOptions.IgnoreCase);
							if (additionMatch.Success)
							{
								todos.Add(new TodoItem(todoMatch.Value.Trim(trimSeparators), lineNumber, currFile, bodyArray, TodoDiffType.Addition, repo, sha, syntax));
							}
							else if (deletionMatch.Success)
							{
								todos.Add(new TodoItem(todoMatch.Value.Trim(trimSeparators), lineNumber, currFile, bodyArray, TodoDiffType.Deletion, repo, sha, syntax));
							}
						}
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
			var repo = Environment.GetEnvironmentVariable("INPUT_REPOSITORY");
			Console.WriteLine("Repo:\t{0}", repo);
			var oldSha = Environment.GetEnvironmentVariable("INPUT_OLD");
			Console.WriteLine("Old SHA:\t{0}", oldSha);
			var newSha = Environment.GetEnvironmentVariable("INPUT_NEW");
			Console.WriteLine("New SHA:\t{0}", newSha);
			var token = Environment.GetEnvironmentVariable("INPUT_TOKEN");
			Console.WriteLine("Token:\t{0}", "***" + token?.Substring(token.Length - 3));
			var todoLabel = Environment.GetEnvironmentVariable("INPUT_TODO");
			Console.WriteLine("TODO:\t{0}", todoLabel);
			var commentPattern = Environment.GetEnvironmentVariable("INPUT_COMMENT");
			Console.WriteLine("Comment re:\t{0}", commentPattern);
			var ghIssueLabel = Environment.GetEnvironmentVariable("INPUT_LABEL");
			Console.WriteLine("GH Label:\t{0}", ghIssueLabel);
			var symbolsToTrim = Environment.GetEnvironmentVariable("INPUT_TRIM");
			Console.WriteLine("Trim:\t{0}", symbolsToTrim);
			var syntax = Environment.GetEnvironmentVariable("INPUT_SYNTAX");
			Console.WriteLine("Syntax:\t{0}", syntax);
			if (!bool.TryParse(Environment.GetEnvironmentVariable("INPUT_NOPUBLISH"), out var nopublish)) { nopublish = false; }
			else { Console.WriteLine("No publishing result mode."); }
			if (!int.TryParse(Environment.GetEnvironmentVariable("INPUT_TIMEOUT"), out var timeout)) { timeout = 1000; }
			Console.WriteLine("Timeout:\t{0}", timeout);
			Console.WriteLine("Getting diff.");
			var diff = GetDiff(repo, token, oldSha, newSha);
			var todos = GetTodoItems(diff, repo, newSha, syntax, commentPattern, todoLabel, symbolsToTrim?.ToCharArray());
			Console.WriteLine("Parsed new TODOs:");
			foreach (var todoItem in todos.Where(t => t.DiffType == TodoDiffType.Addition)) { Console.WriteLine($"+\t{todoItem}"); }
			Console.WriteLine("Parsed removed TODOs:");
			foreach (var todoItem in todos.Where(t => t.DiffType == TodoDiffType.Deletion)) { Console.WriteLine($"-\t{todoItem}"); }
			if (!nopublish) { HandleTodos(repo, token, newSha, ghIssueLabel, timeout, todos); }
			Console.WriteLine("Finished updating issues.");
		}
	}
}