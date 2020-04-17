# TODO-2-GH-Issue

This action run through your recent changes and close an issue if relevant `TODO` comment was removed in a pushed commit, and convert all newly added TODO comments to GitHub issues.

## Usage

Create a workflow file in your .github/workflows directory as follows:
Copy values for REPOSITORY, SHA_OLD, SHA_NEW, TOKEN from example, if your are going for default use case (running on the same repo the push even occured, and comparing with the most recent commit)
 
### todos.yaml

    name: "Todos"
    on: ["push"]
    jobs:
      build:
        runs-on: "ubuntu-latest"
        steps:
          - uses: "actions/checkout@master"
          - name: "TODO-2-GH-Issue"
            uses: "jamminroot/todo-2-gh-issue@master"
            with:
              REPOSITORY: ${{ github.repository }}
              SHA_OLD: ${{ github.event.before }}
              SHA_NEW: ${{ github.sha }}
              TOKEN: ${{ secrets.GITHUB_TOKEN }}
              TODO_LABELS: ["TODO"]
              COMMENT_PATTERNS: ["\/\/"]
            id: "todo"

### Inputs

| Input    | Description |
|----------|-------------|
| `REPOSITORY` | Repository which action will be used on, e.g. 'jamminroot/my-awesome-repo'. |
| `SHA_OLD` | The SHA of the commit to compare with. |
| `SHA_NEW` | The SHA of the commit comparand commit. |
| `TOKEN` | The GitHub access token to allow us to retrieve, create and update issues. |
| `TODO_LABELS` | The label that will be used to identify TODO comments.|
| `COMMENT_PATTERNS` | Regex patterns used to identify start of comment. (`\/\/` C#'s `\\`) |

Note that todo labels will only be compared if they follow matching comment pattern. 
Resulting regex with default C# values (e.g. `// TODO This is a comment`, where comment pattern is `\/\/` and TODO label is `TODO`) would be `(?<=\/\/?TODO[ :]).+`.

## Examples

### Adding TODOs

```diff
+// TODO Change method signature
void method() {

}
```

This will create an issue called "Change method signature".

### Removing TODOs

```diff
-// TODO Change method signature
void method() {

}
```

Removing the `// TODO` comment will close the issue on push.

### Updating TODOs

```diff
-// TODO Change method signature
+// TODO Change method signature to something more creative
void method() {

}
```

Changing the contents of TODO comment will close existing issue and create new one.

This Action is build while looking at [Alstr's](https://github.com/alstr) great tool [todo-to-issue-action](https://github.com/alstr/todo-to-issue-action) (some changes were required for my specific case).