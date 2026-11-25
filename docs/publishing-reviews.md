# Publishing pull and merge requests

## Create a branch and commit

In **Repository configuration → AI**, set the branch-name template. The default is `{type}/{slug}`. Generate a subject from the working-copy AI toolbar and review the branch name. **Create branch and commit** shows the branch, subject and staged file count before it writes to Git. If a hook rejects the commit, review the Git log and continue the commit on the new branch.

## Set up PR/MR credentials

In **Preferences → PR/MR Credentials**, add a GitHub or GitLab connection. Enter the HTTPS address of the instance, such as `https://github.com`, `https://gitlab.com`, or your self-hosted server, and paste the token. For GitHub, use a token with access to the repositories and permission to write pull requests. For GitLab, use a token with the `api` scope and access to both projects. SourceGit stores tokens in the operating system's credential store and selects a connection by the target remote's host. An SSH remote can use the same PR/MR connection when its hostname matches the configured instance. Git reads and pushes use the repository's existing Git authentication.

In **Repository configuration → Publish Pull/Merge Request**, you can optionally set the source remote, target remote, default target branch and description template. The target branch falls back to the last successful selection for that remote or the remote's default branch. The publication dialog lets you change both remotes and the target branch. You can enable the draft task in an external AI profile to generate the title and description from local Git data, then edit both fields.

Right-click a local branch and choose **AI → Create PR/MR…**. The dialog shows the source remote, target remote, target branch, and matched connection. Confirm the displayed refs, title and description with **Push if needed and publish**. SourceGit pushes the selected revision when it is missing or behind on the source remote. A rejected push stops publication. If the result of a create request is uncertain, the next attempt checks for a matching request before creating one.

## Legacy publisher protocol

An existing command-based publisher can still run as a fallback when the selected target has no matching connection. The command receives a UTF-8 JSON request on stdin. Both `find` and `create` use the same publication metadata.

```json
{
  "operation": "find",
  "source": {
    "remote": "fork",
    "url": "https://forge.example.com/user/project.git",
    "branch": "fix/example",
    "commit": "the reviewed source commit ID"
  },
  "target": {
    "remote": "upstream",
    "url": "https://forge.example.com/team/project.git",
    "branch": "main",
    "commit": "the reviewed target commit ID"
  },
  "title": "Fix the example",
  "description": "Markdown description"
}
```

- `find` is a read-only lookup. Return `{"status":"missing"}` or `{"status":"existing","url":"https://…"}` for an open request matching both projects and branches.
- `create` returns `{"status":"created","url":"https://…"}` or the same `existing` response if a matching request was created in the meantime.
- Write exactly one JSON result to stdout. Report failures on stderr with a nonzero exit code. Leave Git pushes to SourceGit.

The [GitLab command adapter](examples/gitlab-publisher.py) remains available for existing publisher configurations. It uses Python 3.9+ and an authenticated [GitLab CLI](https://docs.gitlab.com/cli/).
