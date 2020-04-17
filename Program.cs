using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Todo2GhIssue
{
	internal class TodoItem
	{
		public int Line;
		public string File;
		public string Title;

		public TodoItem(string title, int line, string file)
		{
			Title = title;
			Line = line;
			File = file;
		}

		public override bool Equals(object comparand)
		{
			return comparand is TodoItem other && Line.Equals(other.Line) && File.Equals(other.File) && Title.Equals(other.Title);
		}

		public override string ToString()
		{
			return $"{Title} @ {File}:{Line}";
		}
	}

	internal class Program
	{
		private const string GhApiBase = @"https://api.github.com/repos/";
		private const string CommentPatternToken = "{COMMENT_PATTERN}"; 
		private const string TodoSignatureToken = "{TODO_SIGNATURE}";
		private const string DiffHeaderPattern = @"(?<=diff\s--git\sa.*b.*).+";
		private const string BlockStartPattern = @"((?<=^@@\s).+(?=\s@@))";
		private const string LineNumPattern = @"(?<=\+).+";
		private const string AdditionPattern = @"(?<=^\+).*";
		private const string DeletionPattern = @"(?<=^-).*";
		private const string TodoPatternStart = @"(?<="+CommentPatternToken+" ?"+TodoSignatureToken+"[ :]).+";
		
		private static bool MatchTodo(string line, string commentPattern, string todoSignature, char[] trim, out string matchedValue)
		{
			var match = Regex.Match(line, TodoPatternStart.Replace(CommentPatternToken, commentPattern).Replace(TodoSignatureToken, todoSignature));
			matchedValue = match.Value.Trim(trim);
			return match.Success;
		}
		private static Tuple<IList<TodoItem>, IList<TodoItem>> ListTodoItems(string[] diff, string[] lineCommentPatterns = null, string[] todoSignatures = null, char[] trimSeparators = null)
		{
			lineCommentPatterns ??= new[] {@"\/\/", @"\/\*"};
			todoSignatures ??= new[] {"TODO"};
			trimSeparators ??= new[] {' ', ':', ' ', '(', '"'};
			var additions = new List<TodoItem>();
			var deletions = new List<TodoItem>();
			var lineNumber = 0;
			var currFile = "";
			var rec = false;
			foreach (var line in diff)
			{
				var headerMatch = Regex.Match(line, DiffHeaderPattern, RegexOptions.IgnoreCase);
				if (headerMatch.Success)
				{
					currFile = headerMatch.Value;
				}
				else
				{
					var blockStartMatch = Regex.Match(line, BlockStartPattern, RegexOptions.IgnoreCase);
					if (blockStartMatch.Success)
					{
						var lineNumsMatch = Regex.Match(blockStartMatch.Value, LineNumPattern);
						if (lineNumsMatch.Success)
						{
							lineNumber = int.Parse(lineNumsMatch.Groups[0].Value.Split(',')[0]);
						}
					}
					else
					{
						lineNumber++;
						var additionMatch = Regex.Match(line, AdditionPattern, RegexOptions.IgnoreCase);
						if (additionMatch.Success)
						{
							var addition = additionMatch.Value;
							var found = false;
							foreach (var commentPattern in lineCommentPatterns)
							{
								foreach (var todoSignature in todoSignatures)
								{
									if (!MatchTodo(addition, commentPattern, todoSignature, trimSeparators,out var todo)) continue;
									found = true;
									additions.Add(new TodoItem(todo, lineNumber, currFile));
									break;
								}
								if (found) break;
							}
						}
						var deletionMatch = Regex.Match(line, DeletionPattern, RegexOptions.IgnoreCase);
						if (deletionMatch.Success)
						{
							var deletion = deletionMatch.Value;
							var found = false;
							foreach (var commentPattern in lineCommentPatterns)
							{
								foreach (var todoSignature in todoSignatures)
								{
									if (!MatchTodo(deletion, commentPattern, todoSignature, trimSeparators,out var todo)) continue;
									found = true;
									deletions.Add(new TodoItem(todo, lineNumber, currFile));
									break;
								}
								if (found) break;
							}
						}
					}
				}
			}
			return new Tuple<IList<TodoItem>, IList<TodoItem>>(additions, deletions);
		}

		
		
		private static void Main(string[] args)
		{

			var repo = Environment.GetEnvironmentVariable("INPUT_REPO");
			var before = Environment.GetEnvironmentVariable("INPUT_BEFORE");
			var sha = Environment.GetEnvironmentVariable("INPUT_SHA");
			var label = Environment.GetEnvironmentVariable("INPUT_LABEL");
			var tokenParam = $"access_token={Environment.GetEnvironmentVariable("INPUT_TOKEN")}";
			var todoLabel = Environment.GetEnvironmentVariable("TODO_LABEL");



			var issuesApiUrl = $"{GhApiBase}{repo}/issues";
			var issueHeaders = "{ 'Content-Type': 'application/json' }";
			
			var diff = File.ReadAllLines("c:\\temp\\sample_diff.txt");

			var (additions, deletions) = ListTodoItems(diff);
			Console.WriteLine("Additions:");
			foreach (var todoItem in additions) { Console.WriteLine(todoItem); }
			Console.WriteLine("Deletions:");
			foreach (var todoItem in deletions) { Console.WriteLine(todoItem); }
			/*title = issue['todo']
        # Truncate the title if it's longer than 50 chars.
        if len(title) > 50:
            title = title[:50] + '...'
        file = issue['file']
        line = issue['line_num']
        body = issue['body'] + '\n' + f'https://github.com/{repo}/blob/{sha}/{file}#L{line}'
        new_issue_body = {'title': title, 'body': body, 'labels': ['todo']}
        requests.post(url=issues_url, headers=issue_headers, params=params, data=json.dumps(new_issue_body))
        # Don't add too many issues too quickly.
        sleep(1)#1#
		}
		UploadWorkItems(todos);*/
		}

		private static void UploadWorkItems(IEnumerable<TodoItem> todos)
		{
		}
	}
}