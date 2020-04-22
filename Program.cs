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
		[DataMember(Name = "forced")]
		public bool Forced;

		[DataMember(Name = "pusher")]
		public Pusher Pusher;

		[DataMember(Name = "repository")]
		public Repository Repository;

		[DataMember(Name = "after")]
		public string After;

		[DataMember(Name = "before")]
		public string Before;
	}

	[DataContract]
	public class Pusher
	{
		[DataMember(Name = "email")]
		public string Email;

		[DataMember(Name = "name")]
		public string Name;
	}

	[DataContract]
	public class Repository
	{
		[DataMember(Name = "full_name")]
		public string FullName;
	}

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
		public readonly string Title;
		public readonly TodoDiffType DiffType;
		private readonly IList<string> _labels;
		private readonly int _line;
		private readonly string _body;
		private readonly string _file;

		public TodoItem(string title, int line, string file, int startLines, int endLine, TodoDiffType type, string repo, string sha, IList<string> labels)
		{
			Title = title;
			_line = line;
			_file = file;
			DiffType = type;
			_labels = labels;
			_body = $"**{Title}**\n\nLine: {_line}\nhttps://github.com/{repo}/blob/{sha}{file}#L{startLines}-L{endLine}";
		}

		public object RequestBody(string pusher = "")
		{
			return new {title = Title, body = _body + "\n\n" + pusher, labels = _labels.ToArray()};
		}

		public override string ToString()
		{
			return $"{Title} @ {_file}:{_line}";
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
		private const string DiffHeaderPattern = @"(?<=diff\s--git\sa.*b.*).+";
		private const string BlockStartPattern = @"((?<=^@@\s).+(?=\s@@))";
		private const string LineNumPattern = @"(?<=\+).+";

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
			foreach (Capture cap in labelsMatch.Captures) { labels.Add(cap.Value); }
			return labels;
		}

		private static IList<TodoItem> GetTodoItems(IEnumerable<string> diff, string repo, string sha, int skipLinesLongerThan, string inlineLabelPattern, string inlineLabelReplacePattern,
			string issueLabel, int linesBefore, int linesAfter, string todoPattern = @"\/\/ TODO", char[] trimSeparators = null)
		{
			var parseLabels = !string.IsNullOrWhiteSpace(inlineLabelPattern);
			trimSeparators ??= new[] {' ', ':', ' ', '"'};
			var todos = new List<TodoItem>();
			var lineNumber = 0;
			var currFile = "";
			foreach (var line in diff)
			{
				if (skipLinesLongerThan > 0 && line.Length > skipLinesLongerThan)
				{
					if (!line.StartsWith('-')) lineNumber++;
					continue;
				}
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
						var todoMatch = Regex.Match(line, todoPattern);
						if (todoMatch.Success)
						{
							var todoType = LineDiffType(line);
							if (todoType == TodoDiffType.None) continue;
							var labels = new List<string> {issueLabel};
							var title = todoMatch.Value.Trim(trimSeparators);
							if (parseLabels)
							{
								var inlineLabels = GetLabels(line, inlineLabelPattern);
								title = Regex.Replace(title, inlineLabelReplacePattern, "");
								labels.AddRange(inlineLabels);
							}
							todos.Add(new TodoItem(title.Trim(), lineNumber, currFile, Math.Max(lineNumber - linesBefore, 0), lineNumber + linesAfter, todoType, repo,
								sha, labels));
						}
						if (!line.StartsWith('-')) lineNumber++;
					}
				}
			}
			return todos;
		}

		private static void HandleTodos(string repo, string pusher, string token, string newSha, int timeout, IList<TodoItem> todos)
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
				request.AddJsonBody(todoItem.RequestBody(pusher));
				var response = client.Execute(request);
				if (response.StatusCode != HttpStatusCode.Created)
				{
					Console.WriteLine(
						$"Failed to create GH issue for {todoItem}.\n{response.Content}\n{response.StatusCode}\n{response.StatusDescription}\nRequest:{request.Body.Value}");
					Environment.Exit(1);
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

		private static void Main()
		{
			Console.WriteLine("Parsing parameters.");
			var ghEvEnvironmentVariable = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
			var repo = "";
			var newSha = "";
			var oldSha = "";
			var pusher = "";
			if (!string.IsNullOrWhiteSpace(ghEvEnvironmentVariable))
			{
				var eventData = File.ReadAllText(ghEvEnvironmentVariable);
				var ser = new DataContractJsonSerializer(typeof(GhEvent));
				using var sr = new MemoryStream(Encoding.UTF8.GetBytes(eventData));
				var githubEvent = (GhEvent) ser.ReadObject(sr);
				oldSha = githubEvent.Before;
				newSha = githubEvent.After;
				repo = githubEvent.Repository.FullName;
				pusher = $"{(githubEvent.Forced ? "Force-pushed" : "Pushed")} by @{githubEvent.Pusher.Name} <{githubEvent.Pusher.Email}>";
			}
			var repoOverride = Environment.GetEnvironmentVariable("INPUT_REPOSITORY");
			var newShaOverride = Environment.GetEnvironmentVariable("INPUT_SHA");
			var oldShaOverride = Environment.GetEnvironmentVariable("INPUT_BASE_SHA");
			if (!string.IsNullOrWhiteSpace(repoOverride)) repo = repoOverride;
			if (!string.IsNullOrWhiteSpace(newShaOverride)) newSha = newShaOverride;
			if (!string.IsNullOrWhiteSpace(oldShaOverride)) oldSha = oldShaOverride;
			var token = Environment.GetEnvironmentVariable("INPUT_TOKEN");
			var todoPattern = Environment.GetEnvironmentVariable("INPUT_TODO_PATTERN");
			var inlineLabelPattern = Environment.GetEnvironmentVariable("INPUT_LABELS_PATTERN");
			var inlineLabelReplacePattern = Environment.GetEnvironmentVariable("INPUT_LABELS_REPLACE_PATTERN");
			var ghIssueLabel = Environment.GetEnvironmentVariable("INPUT_GH_LABEL");
			var symbolsToTrim = Environment.GetEnvironmentVariable("INPUT_TRIM");
			if (!bool.TryParse(Environment.GetEnvironmentVariable("INPUT_NOPUBLISH"), out var nopublish)) { nopublish = false; }
			else { Console.WriteLine("No publishing result mode."); }
			if (!int.TryParse(Environment.GetEnvironmentVariable("INPUT_TIMEOUT"), out var timeout)) { timeout = 1000; }
			timeout = Math.Max(0, timeout);
			if (!int.TryParse(Environment.GetEnvironmentVariable("INPUT_IGNORED_LINES_LENGTH"), out var skipLinesLongerThan)) { skipLinesLongerThan = 0; }
			skipLinesLongerThan = Math.Max(0, skipLinesLongerThan);
			if (!int.TryParse(Environment.GetEnvironmentVariable("INPUT_LINES_BEFORE"), out var linesBefore)) { linesBefore = 3; }
			linesBefore = Math.Clamp(linesBefore, 0, 15);
			if (!int.TryParse(Environment.GetEnvironmentVariable("INPUT_LINES_AFTER"), out var linesAfter)) { linesAfter = 7; }
			linesAfter = Math.Clamp(linesAfter, 0, 15);
			Console.WriteLine("Repository:\t{0}", repo);
			Console.WriteLine("Old SHA:\t{0}", oldSha);
			Console.WriteLine("New SHA:\t{0}", newSha);
			Console.WriteLine("Token:\t{0}", token?[0] + string.Join("", Enumerable.Repeat('*', token.Length - 2)) + token?[^1]);
			Console.WriteLine("TODO regular expression:\t{0}", todoPattern);
			Console.WriteLine("Inline label regular expression:\t{0}", inlineLabelPattern);
			Console.WriteLine("Inline label replace regular expression:\t{0}", inlineLabelReplacePattern);
			Console.WriteLine("GH Label:\t{0}", ghIssueLabel);
			Console.WriteLine("Trim:\t{0}", symbolsToTrim);
			Console.WriteLine("Timeout:\t{0}", timeout);
			Console.WriteLine("Lines of code before todo to include to snippet:\t{0}", linesBefore);
			Console.WriteLine("Lines of code after todo to include to snippet:\t{0}", linesAfter);
			Console.WriteLine("Maximum length of line to be processed:\t{0}", skipLinesLongerThan);
			if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(todoPattern) || string.IsNullOrWhiteSpace(oldSha) || string.IsNullOrWhiteSpace(newSha) ||
			    string.IsNullOrWhiteSpace(token))
			{
				Console.WriteLine("Failed to parse required parameters (repository, SHAs of commits, token).");
				Console.WriteLine("Aborting.");
				Environment.Exit(1);
			}
			Console.WriteLine("Getting diff.");
			var diff = GetDiff(repo, token, oldSha, newSha);
			var todos = GetTodoItems(diff, repo, newSha, skipLinesLongerThan, inlineLabelPattern, inlineLabelReplacePattern, ghIssueLabel, linesBefore, linesAfter, todoPattern,
				symbolsToTrim?.ToCharArray());
			Console.WriteLine("Parsed new TODOs:");
			foreach (var todoItem in todos.Where(t => t.DiffType == TodoDiffType.Addition)) { Console.WriteLine($"+\t{todoItem}"); }
			Console.WriteLine("Parsed removed TODOs:");
			foreach (var todoItem in todos.Where(t => t.DiffType == TodoDiffType.Deletion)) { Console.WriteLine($"-\t{todoItem}"); }
			if (!nopublish) { HandleTodos(repo, pusher, token, newSha, timeout, todos); }
			Console.WriteLine("Finished updating issues.");
		}
	}
}