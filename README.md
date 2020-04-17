# TODO-2-GH-Issue

This action runs through your recent changes and closes all corresponding GitHub issues if relevant `TODO` comments were removed in a pushed commit, and converts all newly added TODO comments to GitHub issues.

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
              REPOSITORY: ${{ github.repository }}
              OLD: ${{ github.event.before }}
              NEW: ${{ github.sha }}
              TOKEN: ${{ secrets.GITHUB_TOKEN }}
              TODO: "TODO"
              COMMENT: "\/\/"
              LABEL: "TODO"
              TRIM: ",: ()\""
              TIMEOUT: 1000
            id: "todo"

> **Copy values for REPOSITORY, OLD, NEW, TOKEN from example, if you need the default use case (running on the same repo when the push even occur, and comparing with the most recent commit)**

### Inputs

| Input    | Description |
|----------|-------------|
| `REPOSITORY` | Repository which action will be used on, e.g. 'jamminroot/my-awesome-repo'. |
| `OLD` | The SHA of the comparand commit. |
| `NEW` | The SHA of the new commit we compare diff with. |
| `TOKEN` | The GitHub access token to allow us to retrieve, create and update issues. |
| `TODO` | The label that will be used to identify TODO comments.|
| `COMMENT` | Regex pattern used to identify start of comment. (`\/\/` for C#'s `\\`). |
| `LABEL` | Label to add to github issue. |
| `TRIM` | Set of characters (as a string) to be trimmed from resulting title. |
| `TIMEOUT` | Delay between requests. |

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


### Thanks

This Action is build while looking at [Alstr's](https://github.com/alstr) great tool [todo-to-issue-action](https://github.com/alstr/todo-to-issue-action) (some changes were required for my specific case), so huge thanks to him!
Go check his tool out, too.
