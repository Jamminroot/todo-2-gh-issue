# TODO-2-GH-Issue

This action runs through your recent changes and closes all corresponding GitHub issues if relevant `TODO` comments were removed in a pushed commit, and converts all newly added TODO comments to GitHub issues.

## Screenshot

![Todo-2-gh-issue result](images/issue.png "Example issue")

## Usage

Create a workflow file in your .github/workflows/ directory as follows:
 
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
              TOKEN: ${{ secrets.GITHUB_TOKEN }}
              TODO_PATTERN: "(?<=\\/\\/ ?TODO[ :]).+"
              GH_LABEL: "TODO"
              TRIM: ",: ()\""
              TIMEOUT: 1000
              LINES_BEFORE: 2
              LINES_AFTER: 5
              LABELS_PATTERN: "(?<=\\[).+?(?=\\])"
              LABELS_REPLACE_PATTERN: "\\[(.+?)\\]"
            id: "todo"
            
> **Keep in mind that you have to escape slashes in regex when putting them to yml**
> **Put `${{ secrets.GITHUB_TOKEN }}` as a value for TOKEN**

### Inputs

| Input    | Description |
|----------|-------------|
| `TOKEN` | The GitHub access token to allow us to get existing, create, update issues, comment on them. |
| `TODO_PATTERN` | Regex pattern used to identify TODO comment. Default is `(?<=\\/\\/ ?TODO[ :]).+` for `// TODO`. |
| `GH_LABEL` | Label to add to github issue. |
| `TRIM` | Set of characters (as a string) to be trimmed from resulting title. |
| `TIMEOUT` | Delay between requests. |
| `LINES_BEFORE` | How many lines above `// TODO` to include to snippet. |
| `LINES_AFTER` | How many lines after `// TODO` to include to snippet. |
| `LABELS_PATTERN` | Regex to parse inlined labels. If empty, they will be left in todo. Default is text inside square brackets. |
| `LABELS_REPLACE_PATTERN` | Regex to replace inlined labels. Only works when LABELS_PATTERN provided. Default is text with square brackets. |


> Note that todo labels will only be compared if they follow matching comment pattern. 

> Resulting regex with default C# values (e.g. `// TODO This is a comment`, where comment pattern is `\/\/` and TODO label is `TODO`) would be `(?<=\/\/?TODO[ :]).+`.

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


### Thanks

This Action is build while looking at [Alstr's](https://github.com/alstr) great tool [todo-to-issue-action](https://github.com/alstr/todo-to-issue-action) (some changes were required for my specific case), so huge thanks to him!
Go check his tool out, too.
