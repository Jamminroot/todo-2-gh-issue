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
		[DataMember(Name = "title")]
		public string Title;

		[DataMember(Name = "number")]
		public long Number;
	}

	internal class TodoItem
	{
		public int Line;
		public string File;
		public string Title;
		public TodoDiffType DiffType;
		
		public TodoItem(string title, int line, string file, TodoDiffType type)
		{
			Title = title;
			Line = line;
			File = file;
			DiffType = type;
		}

		public override string ToString()
		{
			return $"{Title} @ {File}:{Line}";
		}

		public object RequestBody(string ghIssueLabel=null)
		{
			return new {title = Title, body = Title + "\n" + File + ":" + Line, labels = $"[{ghIssueLabel}]"};
		}
	}

	enum TodoDiffType
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
		
		private static IList<TodoItem> GetTodoItems(string[] diff, string commentPattern = @"\/\/", string todoSignature = "TODO",
			char[] trimSeparators = null)
		{
			trimSeparators ??= new[] {' ', ':', ' ', '(', '"'};
			var todos = new List<TodoItem>();
			var lineNumber = 0;
			var currFile = "";
			var rec = false;
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
						lineNumber++;
						var additionMatch = Regex.Match(line, AdditionPattern, RegexOptions.IgnoreCase);
						if (additionMatch.Success)
						{
							var match = Regex.Match(line, TodoPatternStart.Replace(CommentPatternToken, commentPattern).Replace(TodoSignatureToken, todoSignature));
							if (match.Success) { todos.Add(new TodoItem(match.Value.Trim(trimSeparators), lineNumber, currFile, TodoDiffType.Addition)); }
						}
						var deletionMatch = Regex.Match(line, DeletionPattern, RegexOptions.IgnoreCase);
						if (deletionMatch.Success)
						{
							var match = Regex.Match(line, TodoPatternStart.Replace(CommentPatternToken, commentPattern).Replace(TodoSignatureToken, todoSignature));
							if (match.Success) { todos.Add(new TodoItem(match.Value.Trim(trimSeparators), lineNumber, currFile, TodoDiffType.Deletion)); }
						}
					}
				}
			}
			return todos;
		}

		private static string[] GetDiff(string repo, string token, string oldSha, string newSha)
		{
			var client = new RestClient($"{ApiBase}{repo}/compare/{oldSha}...{newSha}?access_token={token}") {Timeout = -1};
			var request = new RestRequest(Method.GET);
			request.AddHeader("Accept", "application/vnd.github.v3.diff");
			var response = client.Execute(request);
			if (response.StatusCode != HttpStatusCode.OK)
			{
				Console.WriteLine($"Failed to get diff.\n{response.StatusCode}\n{response.StatusDescription}\n{response.ResponseStatus}");
				Environment.Exit(1);
			}
			return response.Content.Split('\n');
		}

		private static void HandleTodos(string repo, string token, string newSha, string ghIssueLabel, int timeout, IEnumerable<TodoItem> todos)
		{
			var activeIssues = GetActiveItems(repo, token);
			var deletions = todos.Where(t => t.DiffType == TodoDiffType.Deletion).ToList();
			var additions = todos.Where(t => t.DiffType == TodoDiffType.Addition).ToList();
			foreach (var number in activeIssues.Where(i => deletions.Select(d => d.Title).Contains(i.Title)).Select(i=>i.Number))
			{
				var client = new RestClient($"{ApiBase}{repo}/issues/{number}?access_token={token}") {Timeout = -1};
				var request = new RestRequest(Method.PATCH);
				request.AddHeader("Accept", "application/json");
				request.AddJsonBody(new {state="closed"});
				var response = client.Execute(request);
				if (response.StatusCode != HttpStatusCode.OK)
				{
					Console.WriteLine($"Failed to close GH issue.\n{response.StatusCode}\n{response.StatusDescription}\n{response.ResponseStatus}");
					Environment.Exit(1);
				}
				request = new RestRequest(Method.POST);
				request.AddHeader("Accept", "application/json");
				request.AddJsonBody(new {state=$"Closed automatically with {newSha}"});
				client = new RestClient($"{ApiBase}{repo}/issues/{number}/comments?access_token={token}") {Timeout = -1};
				response = client.Execute(request);
				if (response.StatusCode != HttpStatusCode.OK)
				{
					Console.WriteLine($"Failed to post GH comment.\n{response.StatusCode}\n{response.StatusDescription}\n{response.ResponseStatus}");
					Environment.Exit(1);
				}
				Thread.Sleep(timeout);
			}
			foreach (var todoItem in additions)
			{
				var client = new RestClient($"{ApiBase}{repo}/issues?access_token={token}") {Timeout = -1};
				var request = new RestRequest(Method.POST);
				request.AddHeader("Accept", "application/json");
				request.AddJsonBody(todoItem.RequestBody(ghIssueLabel));
				var response = client.Execute(request);
				if (response.StatusCode != HttpStatusCode.OK)
				{
					Console.WriteLine($"Failed to post GH comment.\n{response.StatusCode}\n{response.StatusDescription}\n{response.ResponseStatus}");
					Environment.Exit(1);
				}
				Thread.Sleep(timeout);
			}
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
				Console.WriteLine($"Failed to get active items.\n{response.StatusCode}\n{response.StatusDescription}\n{response.ResponseStatus}");
				Environment.Exit(1);
			}
			using var sr = new MemoryStream(Encoding.UTF8.GetBytes(response.Content));
			var result = (List<Issue>)ser.ReadObject(sr);
			return result;
		}
		
		private static void Main(string[] args)
		{
			var repo = Environment.GetEnvironmentVariable("INPUT_REPOSITORY");
			var oldSha = Environment.GetEnvironmentVariable("INPUT_OLD");
			var newSha = Environment.GetEnvironmentVariable("INPUT_NEW");
			var token = Environment.GetEnvironmentVariable("INPUT_TOKEN");
			var todoLabel = Environment.GetEnvironmentVariable("INPUT_TODO");
			var commentPattern = Environment.GetEnvironmentVariable("INPUT_COMMENT");
			var ghIssueLabel = Environment.GetEnvironmentVariable("INPUT_LABEL");
			var symbolsToTrim = Environment.GetEnvironmentVariable("INPUT_TRIM");
			if (!int.TryParse(Environment.GetEnvironmentVariable("INPUT_TIMEOUT"), out var timeout))
			{
				timeout = 1000;
			}
			var diff = GetDiff(repo, token, oldSha, newSha);
			var todos = GetTodoItems(diff, commentPattern, todoLabel, symbolsToTrim?.ToCharArray());
			Console.WriteLine("Added TODOs:");
			foreach (var todoItem in todos.Where(t=>t.DiffType==TodoDiffType.Addition)) { Console.WriteLine($"+\t{todoItem}"); }
			Console.WriteLine("Removed TODOs:");
			foreach (var todoItem in todos.Where(t=>t.DiffType==TodoDiffType.Deletion)) { Console.WriteLine($"-\t{todoItem}"); }
			HandleTodos(repo, token, newSha, ghIssueLabel, timeout, todos);
			Console.WriteLine("Finished updating issues.");
		}
	}
}
