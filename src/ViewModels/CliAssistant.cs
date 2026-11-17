using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class CliAssistant : ObservableObject
    {
        public bool IsGenerating
        {
            get => _isGenerating;
            private set
            {
                if (SetProperty(ref _isGenerating, value))
                    OnPropertyChanged(nameof(CanCreateBranch));
            }
        }

        public bool IsCommitting
        {
            get => _isCommitting;
            private set
            {
                if (SetProperty(ref _isCommitting, value))
                    OnPropertyChanged(nameof(CanCreateBranch));
            }
        }

        public string Text
        {
            get => _text;
            private set => SetProperty(ref _text, value);
        }

        public string Response
        {
            get => _response;
            private set
            {
                if (SetProperty(ref _response, value))
                {
                    OnPropertyChanged(nameof(GenerateLabel));
                    OnPropertyChanged(nameof(CanCreateBranch));
                }
            }
        }

        public string BranchName
        {
            get => _branchName;
            set
            {
                if (SetProperty(ref _branchName, value))
                    OnPropertyChanged(nameof(CanCreateBranch));
            }
        }

        public string GenerateLabel
        {
            get => string.IsNullOrEmpty(_response) ? "GENERATE" : "RE-GENERATE";
        }

        public bool CanCreateBranch
        {
            get => !_isGenerating && !_isCommitting &&
                !string.IsNullOrWhiteSpace(_response) &&
                !string.IsNullOrWhiteSpace(_branchName);
        }

        public string CommandText { get; }

        public CliAssistant(Repository repo, AI.Service service, List<Models.Change> changes)
        {
            _repo = repo;
            _service = service;
            CommandText = string.IsNullOrWhiteSpace(service?.ExecutablePath) ? "codely" : service.ExecutablePath;
            _cancel = new CancellationTokenSource();
            _text = "Click GENERATE to create a commit message.";

            var builder = new StringBuilder();
            foreach (var c in changes)
                SerializeChange(c, builder);
            _changeList = builder.ToString();

            if (changes.Count > 0 && changes[0].DataForAmend is { } amend)
                _amendParent = amend.ParentSHA;
        }

        public async Task GenAsync()
        {
            if (_cancel is { IsCancellationRequested: false })
                _cancel.Cancel();
            _cancel = new CancellationTokenSource();

            var builder = new StringBuilder();
            var responseBuilder = new StringBuilder();
            var foundResponse = false;
            var currentBranchName = _repo.CurrentBranch?.Name ?? "main";

            Text = string.Empty;
            Response = string.Empty;
            IsGenerating = true;

            try
            {
                builder.AppendLine("Reading staged changes...").AppendLine();
                Text = builder.ToString();

                var diff = await new Commands.GetStagedDiffForAI(_repo.FullPath, _amendParent).ReadAsync();
                if (!diff.IsSuccess)
                    throw new Exception(string.IsNullOrEmpty(diff.StdErr) ? "Failed to read the staged diff." : diff.StdErr);

                builder.AppendLine("Asking codely to generate a commit message...").AppendLine();
                Text = builder.ToString();

                await new AI.CliAgent(_service).GenerateCommitMessageAsync(
                    _repo.FullPath,
                    currentBranchName,
                    _changeList,
                    diff.StdOut,
                    Preferences.Instance.CommitMessageRule,
                    message =>
                    {
                        builder.AppendLine(message);

                        if (foundResponse)
                        {
                            if (message.Equals("# Token Usage", StringComparison.Ordinal))
                                foundResponse = false;
                            else
                                responseBuilder.AppendLine(message);
                        }
                        else if (message.Equals("# Assistant", StringComparison.Ordinal))
                        {
                            foundResponse = true;
                        }

                        Dispatcher.UIThread.Post(() => Text = builder.ToString());
                    },
                    _cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                builder
                    .AppendLine()
                    .AppendLine("[ERROR]")
                    .Append(e.Message);

                Text = builder.ToString();
                Response = string.Empty;
            }

            Response = responseBuilder.ToString().Trim();
            BranchName = SuggestBranchName(Response);
            IsGenerating = false;
        }

        public async Task<bool> CreateBranchAndCommitAsync()
        {
            var message = Response?.Trim() ?? string.Empty;
            var name = BranchName?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(name))
                return false;

            IsCommitting = true;
            var succ = false;
            var log = _repo.CreateLog("Create Branch And Commit");
            try
            {
                var checkout = new Commands.Checkout(_repo.FullPath).Use(log);
                if (await checkout.BranchAsync(name, "HEAD", false, false).ConfigureAwait(false))
                {
                    var commit = new Commands.Commit(_repo.FullPath, message, false, false, false, false).Use(log);
                    succ = await commit.RunAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                log.Complete();
                IsCommitting = false;
            }

            if (succ)
            {
                _repo.MarkBranchesDirtyManually();
                _repo.RefreshSubmodules();
            }

            return succ;
        }

        public void Use(string text)
        {
            _repo.SetCommitMessage(text);
        }

        public void AppendError(string message)
        {
            Text = Text + Environment.NewLine + Environment.NewLine + message;
        }

        public void Cancel()
        {
            _cancel?.Cancel();
        }

        private static string SuggestBranchName(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            var firstLine = message.Split('\n')[0].Trim();
            var type = "chore";
            var subject = firstLine;

            var colon = firstLine.IndexOf(':');
            if (colon > 0)
            {
                var head = firstLine.Substring(0, colon).Trim();
                var open = head.IndexOf('(');
                type = (open > 0 ? head.Substring(0, open) : head).Trim().ToLowerInvariant();
                subject = firstLine.Substring(colon + 1).Trim();
            }

            if (type.Length == 0)
                type = "chore";

            var slug = new StringBuilder();
            foreach (var ch in subject.ToLowerInvariant())
            {
                var isAsciiLetter = ch >= 'a' && ch <= 'z';
                var isDigit = ch >= '0' && ch <= '9';
                if (isAsciiLetter || isDigit)
                {
                    slug.Append(ch);
                }
                else if (slug.Length > 0 && slug[slug.Length - 1] != '-')
                {
                    slug.Append('-');
                }
            }

            var text = slug.ToString().Trim('-');
            if (text.Length > 40)
                text = text.Substring(0, 40).Trim('-');

            // Non-ASCII subjects (e.g. Chinese) collapse to an empty slug; fall back to a timestamp.
            if (text.Length == 0)
                return type + "/" + DateTime.Now.ToString("yyyyMMdd-HHmm");

            return type + "/" + text;
        }

        private void SerializeChange(Models.Change c, StringBuilder builder)
        {
            var status = c.Index switch
            {
                Models.ChangeState.Added => "A",
                Models.ChangeState.Modified => "M",
                Models.ChangeState.Deleted => "D",
                Models.ChangeState.TypeChanged => "T",
                Models.ChangeState.Renamed => "R",
                Models.ChangeState.Copied => "C",
                _ => " ",
            };

            builder.Append(status).Append('\t');

            if (c.Index == Models.ChangeState.Renamed || c.Index == Models.ChangeState.Copied)
                builder.Append(c.OriginalPath).Append(" -> ").Append(c.Path).AppendLine();
            else
                builder.Append(c.Path).AppendLine();
        }

        private readonly Repository _repo = null;
        private readonly AI.Service _service = null;
        private readonly string _changeList = null;
        private readonly string _amendParent = null;
        private CancellationTokenSource _cancel = null;
        private bool _isGenerating = false;
        private bool _isCommitting = false;
        private string _text = string.Empty;
        private string _response = string.Empty;
        private string _branchName = string.Empty;
    }
}
