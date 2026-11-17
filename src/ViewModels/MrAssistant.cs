using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class MrAssistant : ObservableObject
    {
        public string SourceBranch { get; }

        public List<string> TargetBranches { get; }

        public string SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                if (SetProperty(ref _selectedTarget, value))
                {
                    Preferences.Instance.MrLastTarget = value ?? string.Empty;
                    OnPropertyChanged(nameof(CanCreate));
                }
            }
        }

        public List<Models.MrTemplate> Templates { get; } = Models.MrTemplate.All;

        public Models.MrTemplate SelectedTemplate
        {
            get => _selectedTemplate;
            set
            {
                if (SetProperty(ref _selectedTemplate, value))
                    Preferences.Instance.MrLastTemplate = value?.Name ?? string.Empty;
            }
        }

        public string Title
        {
            get => _title;
            set
            {
                if (SetProperty(ref _title, value))
                    OnPropertyChanged(nameof(CanCreate));
            }
        }

        public string Description
        {
            get => _description;
            set => SetProperty(ref _description, value);
        }

        public string Text
        {
            get => _text;
            private set => SetProperty(ref _text, value);
        }

        public bool IsGenerating
        {
            get => _isGenerating;
            private set
            {
                if (SetProperty(ref _isGenerating, value))
                    OnPropertyChanged(nameof(CanCreate));
            }
        }

        public bool IsCreating
        {
            get => _isCreating;
            private set
            {
                if (SetProperty(ref _isCreating, value))
                    OnPropertyChanged(nameof(CanCreate));
            }
        }

        public string GenerateLabel
        {
            get => string.IsNullOrEmpty(_description) ? "GENERATE" : "RE-GENERATE";
        }

        public bool CanCreate
        {
            get => !_isGenerating && !_isCreating &&
                !string.IsNullOrWhiteSpace(_title) &&
                !string.IsNullOrWhiteSpace(_selectedTarget);
        }

        public MrAssistant(Repository repo, AI.Service service, string sourceBranch, List<string> targetBranches)
        {
            _repo = repo;
            _service = service;
            _cancel = new CancellationTokenSource();

            SourceBranch = sourceBranch;
            SortTargets(targetBranches);
            TargetBranches = targetBranches;
            _selectedTarget = FindTarget(targetBranches, Preferences.Instance.MrLastTarget) ?? PickDefaultTarget(targetBranches);
            _selectedTemplate = FindTemplate(Preferences.Instance.MrLastTemplate) ?? Models.MrTemplate.All[0];

            // Persist the effective defaults so the very first open is remembered even
            // when the user never touches the selectors.
            Preferences.Instance.MrLastTarget = _selectedTarget ?? string.Empty;
            Preferences.Instance.MrLastTemplate = _selectedTemplate?.Name ?? string.Empty;

            _text = "Select a target branch and a template, then click GENERATE.";
        }

        public async Task GenerateAsync()
        {
            if (_cancel is { IsCancellationRequested: false })
                _cancel.Cancel();
            _cancel = new CancellationTokenSource();

            var builder = new StringBuilder();
            Text = string.Empty;
            Title = string.Empty;
            Description = string.Empty;
            IsGenerating = true;

            try
            {
                builder.AppendLine($"Collecting commits between {_selectedTarget} and {SourceBranch}...").AppendLine();
                Text = builder.ToString();

                var context = await new Commands.GetMergeRequestContext(_repo.FullPath, SourceBranch, _selectedTarget).ReadAsync();
                if (!context.IsSuccess)
                    throw new Exception(string.IsNullOrEmpty(context.StdErr) ? "Failed to read commits." : context.StdErr);

                if (string.IsNullOrWhiteSpace(context.StdOut))
                    throw new Exception($"No commits found between '{_selectedTarget}' and '{SourceBranch}'.");

                builder.AppendLine("Asking codely to write the merge request...").AppendLine();
                Text = builder.ToString();

                var prompt = BuildPrompt(context.StdOut);
                var output = await new AI.CliAgent(_service).RunPromptAsync(
                    _repo.FullPath,
                    prompt,
                    line => Dispatcher.UIThread.Post(() => Text = builder.AppendLine(line).ToString()),
                    _cancel.Token);

                var (title, description) = ParseOutput(output);
                Title = title;
                Description = description;

                builder.AppendLine().AppendLine("=== Generated ===").AppendLine(title).AppendLine().AppendLine(description);
                Text = builder.ToString();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                builder.AppendLine().AppendLine("[ERROR]").Append(e.Message);
                Text = builder.ToString();
            }

            IsGenerating = false;
            OnPropertyChanged(nameof(GenerateLabel));
        }

        public async Task<Commands.CreateMergeRequest.Result> CreateAsync()
        {
            var title = Title?.Trim() ?? string.Empty;
            var description = Description?.Trim() ?? string.Empty;
            var target = SelectedTarget?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(target))
                return new Commands.CreateMergeRequest.Result() { Error = "Title and target branch are required." };

            IsCreating = true;
            try
            {
                var (apiBase, token, projectPath, remote, error) = ResolveCredential();
                if (!string.IsNullOrEmpty(error))
                    return new Commands.CreateMergeRequest.Result() { Error = error };

                var pushError = await EnsureSourceBranchPushedAsync(remote).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(pushError))
                    return new Commands.CreateMergeRequest.Result() { Error = pushError };

                var client = new Commands.CreateMergeRequest(apiBase, token, projectPath);
                return await client.CreateAsync(SourceBranch, target, title, description, false, _cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new Commands.CreateMergeRequest.Result() { Error = "Cancelled." };
            }
            finally
            {
                IsCreating = false;
            }
        }

        /// <summary>
        /// The merge request API only accepts a source branch that already exists on the
        /// server, so the branch is pushed first when it is missing or behind. Only a
        /// regular push is attempted: a rejected non-fast-forward surfaces the git error
        /// instead of silently overwriting the remote branch.
        /// </summary>
        private async Task<string> EnsureSourceBranchPushedAsync(Models.Remote remote)
        {
            AppendLine($"Checking '{SourceBranch}' on '{remote.Name}'...");

            var lookup = await new Commands.GetRemoteBranchTip(_repo.FullPath, remote.Name, SourceBranch).ReadAsync().ConfigureAwait(false);
            if (!lookup.IsSuccess)
                return $"Failed to read '{SourceBranch}' from '{remote.Name}': {lookup.Error}";

            var local = await new Commands.QueryRevisionByRefName(_repo.FullPath, $"refs/heads/{SourceBranch}".Quoted())
                .GetResultAsync()
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(local))
                return $"Local branch '{SourceBranch}' does not exist.";

            if (lookup.Tip == local)
            {
                AppendLine($"'{SourceBranch}' is up to date on '{remote.Name}'.");
                return string.Empty;
            }

            AppendLine(string.IsNullOrEmpty(lookup.Tip)
                ? $"Pushing '{SourceBranch}' to '{remote.Name}'..."
                : $"'{remote.Name}' is at a different commit for '{SourceBranch}', pushing the local branch...");

            var push = new Commands.PushBranch(_repo.FullPath, remote.Name, SourceBranch, remote.PrivateSSHKey);
            if (!await push.RunAsync().ConfigureAwait(false))
                return $"Failed to push '{SourceBranch}' to '{remote.Name}'. Resolve the git error above and try again.";

            AppendLine($"'{SourceBranch}' is now available on '{remote.Name}'.");
            return string.Empty;
        }

        private void AppendLine(string line)
        {
            Text = string.IsNullOrEmpty(Text) ? line : Text + Environment.NewLine + line;
        }

        private static string FindTarget(List<string> branches, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            foreach (var branch in branches)
            {
                if (branch.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return branch;
            }

            return null;
        }

        private static Models.MrTemplate FindTemplate(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            foreach (var template in Models.MrTemplate.All)
            {
                if (template.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return template;
            }

            return null;
        }

        private static void SortTargets(List<string> branches)
        {
            branches.Sort((a, b) =>
            {
                var rankA = RankTarget(a);
                var rankB = RankTarget(b);
                if (rankA != rankB)
                    return rankA.CompareTo(rankB);

                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static string PickDefaultTarget(List<string> branches)
        {
            if (branches.Count == 0)
                return string.Empty;

            foreach (var name in DefaultTargetNames)
            {
                foreach (var branch in branches)
                {
                    if (branch.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return branch;
                }
            }

            return branches[0];
        }

        private static int RankTarget(string branch)
        {
            for (var i = 0; i < DefaultTargetNames.Length; i++)
            {
                if (branch.Equals(DefaultTargetNames[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return DefaultTargetNames.Length;
        }

        private static readonly string[] DefaultTargetNames = ["main", "master", "develop", "trunk"];

        public void Cancel()
        {
            _cancel?.Cancel();
        }

        public void AppendError(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            Text = Text + Environment.NewLine + Environment.NewLine + "[ERROR] " + message;
        }

        private string BuildPrompt(string context)
        {
            var builder = new StringBuilder();
            builder.AppendLine("You are writing a GitLab merge request.");
            builder.AppendLine("Write the title and the description in English.");
            builder.AppendLine();
            builder.AppendLine("=== TEMPLATE ===");
            builder.AppendLine(_selectedTemplate?.Instruction ?? string.Empty);
            builder.AppendLine();
            builder.AppendLine("=== BRANCHES ===");
            builder.AppendLine("Source branch: " + SourceBranch);
            builder.AppendLine("Target branch: " + _selectedTarget);
            builder.AppendLine();
            builder.AppendLine("=== OUTPUT FORMAT ===");
            builder.AppendLine("Line 1: the merge request title. Do not prefix it with 'TITLE:'.");
            builder.AppendLine("Then one empty line.");
            builder.AppendLine("Then the description in Markdown.");
            builder.AppendLine("Do not wrap the whole answer in code fences.");
            builder.AppendLine();
            builder.AppendLine("=== COMMITS AND FILE STATS ===");

            if (context.Length > MaxContextChars)
                builder.AppendLine(context.Substring(0, MaxContextChars)).AppendLine("...[truncated]");
            else
                builder.AppendLine(context);

            return builder.ToString();
        }

        private static (string Title, string Description) ParseOutput(string output)
        {
            var text = (output ?? string.Empty).Trim();
            if (text.Length == 0)
                return (string.Empty, string.Empty);

            var index = text.IndexOf('\n');
            if (index < 0)
                return (text, string.Empty);

            var title = text.Substring(0, index).Trim();
            var description = text.Substring(index + 1).TrimStart('\r', '\n').TrimEnd();
            return (title, description);
        }

        private (string ApiBase, string Token, string ProjectPath, Models.Remote Remote, string Error) ResolveCredential()
        {
            if (_repo.Remotes.Count == 0)
                return (string.Empty, string.Empty, string.Empty, null, "No remote is configured for this repository.");

            Models.Remote remote = null;
            foreach (var r in _repo.Remotes)
            {
                if (r.Name.Equals("origin", StringComparison.Ordinal))
                {
                    remote = r;
                    break;
                }

                remote ??= r;
            }

            if (remote == null || !remote.TryGetVisitURL(out var webUrl))
                return (string.Empty, string.Empty, string.Empty, null, "Failed to resolve the project URL from the remote.");

            var uri = new Uri(webUrl);
            var projectPath = uri.AbsolutePath.Trim('/');

            var apiBase = Preferences.Instance.GitLabApiUrl;
            var token = Preferences.Instance.GitLabToken;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(apiBase))
            {
                var (envApi, envToken) = ReadGitLabSkillEnv();
                if (string.IsNullOrWhiteSpace(apiBase))
                    apiBase = !string.IsNullOrWhiteSpace(envApi) ? envApi : $"{uri.Scheme}://{uri.Authority}/api/v4";

                if (string.IsNullOrWhiteSpace(token))
                    token = envToken;
            }

            if (string.IsNullOrWhiteSpace(apiBase))
                apiBase = $"{uri.Scheme}://{uri.Authority}/api/v4";

            if (string.IsNullOrWhiteSpace(token))
                return (string.Empty, string.Empty, string.Empty, null,
                    "No GitLab token found. Configure it in Preferences, or set GITLAB_TOKEN in ~/.config/gitlab-skill/.env.");

            return (apiBase, token, projectPath, remote, string.Empty);
        }

        private static (string Api, string Token) ReadGitLabSkillEnv()
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "gitlab-skill", ".env");

                if (!File.Exists(path))
                    return (string.Empty, string.Empty);

                var api = string.Empty;
                var token = string.Empty;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    var sep = line.IndexOf('=');
                    if (sep <= 0)
                        continue;

                    var key = line.Substring(0, sep).Trim();
                    var value = line.Substring(sep + 1).Trim().Trim('"', '\'');

                    if (key == "GITLAB_TOKEN" || key == "GITLAB_PERSONAL_ACCESS_TOKEN")
                        token = value;
                    else if (key == "GITLAB_API_URL")
                        api = value;
                }

                return (api, token);
            }
            catch (Exception)
            {
                return (string.Empty, string.Empty);
            }
        }

        private const int MaxContextChars = 120 * 1024;

        private readonly Repository _repo = null;
        private readonly AI.Service _service = null;
        private CancellationTokenSource _cancel = null;
        private string _selectedTarget = string.Empty;
        private Models.MrTemplate _selectedTemplate = null;
        private string _title = string.Empty;
        private string _description = string.Empty;
        private string _text = string.Empty;
        private bool _isGenerating = false;
        private bool _isCreating = false;
    }
}
