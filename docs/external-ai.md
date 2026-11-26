# External AI commands

Open **Preferences → AI → External CLI profiles** and add a profile. Set its executable and enter one argument per line. Put spaces inside the argument that needs them, without surrounding quotes. Select the tasks for this profile. A command can be a program from `PATH` or an absolute executable path. For a Windows script, select its interpreter as the executable and put the script path in the arguments.

SourceGit launches the command in the selected worktree. The command inherits your user permissions and can send the supplied prompt to its configured provider. Configure a headless, read-only command for analysis and generation. Set model and reasoning options in the command arguments, or create separate profiles for fast generation and deeper analysis.

## Input and output

- By default the command reads the UTF-8 prompt on stdin and writes the answer to stdout with exit code zero. The CLI should send diagnostics to stderr.
- To use a prompt file, select the prompt-file option and insert `{promptFile}` in one argument. The file is UTF-8 and temporary. A CLI that accepts an `@file` argument can use `@{promptFile}`.
- Select **Read SourceGit JSONL output** for a command or adapter that emits one UTF-8 JSON object per stdout line:

  | Type | Fields | Meaning |
  | --- | --- | --- |
  | `answer` | `text` | One final answer |
  | `thinking` | `text` | Optional reasoning output exposed by the provider |
  | `tool_call`, `tool_result` | `text`, optional `label` | Optional tool activity |
  | `session` | `id` | Optional session identifier for the completed answer |
  | `error` | `text`, optional `code` | A failed request |

For a resumable JSONL profile, specify optional resume arguments with `{sessionId}`. SourceGit appends them only when it has an active ID. A resumed command can report `{"type":"error","code":"session_expired","text":"…"}` to request one fresh attempt. SourceGit stores an ID after successful completion, keyed by worktree, profile configuration and task. Analysis resumes by default when resume arguments are set. Generation resumes when its option is enabled. Stopping a request does not replace the saved ID.

## Examples

To check a profile without contacting an AI provider, choose `node` as the executable and enter these arguments in plain-text mode:

```text
-e
process.stdin.resume().on("end", () => console.log("fix: test the external generator"))
```

For a structured Codely profile, choose `node`, enable the prompt-file and JSONL options, and enter the following arguments. Replace the first two lines with absolute paths to the [adapter](examples/codely-jsonl.mjs) and the Codely JavaScript entry point from your installation. On npm installations, the Codely shim identifies its entry point.

```text
<absolute path to codely-jsonl.mjs>
<absolute path to the Codely CLI JavaScript entry point>
{promptFile}
--collaboration-mode
ask
--no-upm
--no-auto-edit-memory
```

Set the following resume arguments for that adapter:

```text
--resume-session
{sessionId}
```

The same app settings work with another vendor or a local backend by configuring its program directly or using a JSONL adapter for that backend. Profiles migrated from the older single-argument-string implementation show their previous arguments in the editor. Review the new input mode and enter any arguments you still need individually before enabling the profile's tasks.

## Use a profile

Open the AI helper from the commit toolbar to generate a subject for all staged changes. From a selection in the staged file list, the helper generates from those files. Apply the returned subject to the commit editor.

Right-click a commit, selected files, or a text diff and choose **AI → Add to AI Chat**. The context appears in the right sidebar and can be previewed or removed before you send it. Choose **AI → Analysis** to start a conversation with the selected changes and a suggested question. An independent window hosts the same chat when the repository view is too narrow.

Open the sidebar with the **AI Chat** button on the right of the repository toolbar. Select a CLI, enter a question, and press **Enter** or click **Send**. Use **Shift+Enter** for a new line. A chat uses the frozen snapshots for follow-up questions. **History** reopens locally saved conversations and drafts for this worktree. SourceGit stores history in its application configuration directory. Tool and reasoning output can be expanded during the session. Drag across the analysis to select multiple paragraphs or code blocks, then use the clipboard shortcut or the Copy menu.

Open **AI Chat model and effort** in an external CLI profile to configure the optional composer selectors. Enter the supported model IDs or effort values one per line. Add the matching argument template, such as `--model={model}` or `--reasoning_effort={effort}` for supported Codely models. Each argument line is passed separately. **CLI default** omits the corresponding override. SourceGit remembers the choices per chat and resumes CLI sessions for the same effective command arguments. These selectors apply to chat requests.

Sending moves the draft and its pending attachments into the conversation and clears the composer. SourceGit waits for the external AI command to finish. If the CLI reports an error or you click **Stop**, the draft and its attachments return to the composer.

## Create a branch and commit

In **Repository configuration → AI**, set the branch-name template. The default is `{type}/{slug}`. Generate a subject from the working-copy AI toolbar and review the branch name. **Create branch and commit** shows the branch, subject and staged file count before it writes to Git. If a hook rejects the commit, review the Git log and continue the commit on the new branch.
