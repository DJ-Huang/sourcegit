using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.AI
{
    /// <summary>
    /// Runs an external CLI (codely) in headless mode. The prompt is written to a
    /// temporary file and referenced with the <c>@path</c> syntax, which keeps the
    /// command line short, ASCII-only and free of shell quoting or codepage hazards.
    ///
    /// The CLI writes startup banners (Unity TCP status, MCP tool registration) and
    /// ANSI colours to stdout in text mode, so the structured <c>stream-json</c>
    /// format is used instead: only assistant message events carry the answer.
    /// </summary>
    public class CliAgent
    {
        public CliAgent(Service service)
        {
            _service = service;
        }

        public async Task GenerateCommitMessageAsync(
            string repo,
            string currentBranch,
            string changeList,
            string stagedDiff,
            string rule,
            Action<string> onUpdate,
            CancellationToken cancellation)
        {
            var prompt = BuildCommitPrompt(currentBranch, changeList, stagedDiff, rule);
            var text = await RunPromptAsync(repo, prompt, onUpdate, cancellation).ConfigureAwait(false);
            var line = FirstLine(text);

            onUpdate?.Invoke(string.Empty);
            onUpdate?.Invoke("# Assistant");
            onUpdate?.Invoke(line);
            onUpdate?.Invoke(string.Empty);
        }

        /// <summary>
        /// Runs the configured CLI with a fully caller-built prompt and returns the
        /// cleaned answer. The result may span multiple lines.
        /// </summary>
        public async Task<string> RunPromptAsync(
            string repo,
            string prompt,
            Action<string> onUpdate,
            CancellationToken cancellation)
        {
            var promptFile = Path.Combine(Path.GetTempPath(), "sourcegit-codely-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                await File.WriteAllTextAsync(promptFile, prompt, new UTF8Encoding(false), cancellation).ConfigureAwait(false);
                return await RunAsync(repo, promptFile, onUpdate, cancellation).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(promptFile);
            }
        }

        private async Task<string> RunAsync(string repo, string promptFile, Action<string> onUpdate, CancellationToken cancellation)
        {
            using var process = new Process() { StartInfo = CreateStartInfo(repo, promptFile) };

            try
            {
                process.Start();
            }
            catch (Exception e)
            {
                throw new Exception(
                    $"Failed to start '{_service.ExecutablePath}'. Please check the executable path in preferences. {e.Message}", e);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var parser = new StreamEventParser();
            var errors = new StringBuilder();

            var stdout = ReadLinesAsync(process.StandardOutput, parser.Consume, timeout.Token);
            var stderr = ReadLinesAsync(process.StandardError, line => errors.AppendLine(line), CancellationToken.None);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            if (process.ExitCode != 0)
                throw new Exception($"codely exited with code {process.ExitCode}.{FormatErrors(errors.ToString())}");

            if (!string.IsNullOrEmpty(parser.Failure))
                throw new Exception(parser.Failure + FormatErrors(errors.ToString()));

            var text = Normalize(parser.Answer);
            if (string.IsNullOrEmpty(text))
                throw new Exception("codely returned no content." + FormatErrors(errors.ToString()));

            return text;
        }

        private ProcessStartInfo CreateStartInfo(string repo, string promptFile)
        {
            var executable = string.IsNullOrWhiteSpace(_service.ExecutablePath) ? "codely" : _service.ExecutablePath.Trim();
            var extra = string.IsNullOrWhiteSpace(_service.ExtraArguments) ? string.Empty : " " + _service.ExtraArguments.Trim();
            var continueArgument = _service.PersistConversation ? " --continue" : string.Empty;
            var arguments = "-p \"@" + promptFile + "\" --output-format stream-json" + continueArgument + extra;
            var extension = Path.GetExtension(executable).ToLowerInvariant();

            var startInfo = new ProcessStartInfo()
            {
                WorkingDirectory = repo,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            if (extension == ".js")
            {
                startInfo.FileName = "node";
                startInfo.Arguments = "\"" + executable + "\" " + arguments;
            }
            else if (extension is ".exe" or ".com")
            {
                startInfo.FileName = executable;
                startInfo.Arguments = arguments;
            }
            else
            {
                // npm installs `codely` as a .cmd shim, which CreateProcess cannot run directly.
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = "/c \"\"" + executable + "\" " + arguments + "\"";
            }

            return startInfo;
        }

        /// <summary>
        /// Consumes the newline delimited <c>stream-json</c> events and keeps only the
        /// text the assistant produced. Anything that is not a JSON event is discarded
        /// as banner noise, which is exactly what used to leak into commit messages.
        /// </summary>
        private sealed class StreamEventParser
        {
            public string Answer
            {
                get
                {
                    if (_delta.Length > 0)
                        return _delta.ToString();

                    if (_plain.Length > 0)
                        return _plain.ToString();

                    // Only reachable when the CLI emitted no JSON event at all.
                    return _sawEvents ? string.Empty : _raw.ToString();
                }
            }

            public string Failure { get; private set; } = string.Empty;

            public void Consume(string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    return;

                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith('{'))
                {
                    // Banner lines (Unity TCP status, MCP registration) and the resume
                    // notice are never part of the answer.
                    _raw.AppendLine(line);
                    return;
                }

                try
                {
                    using var document = JsonDocument.Parse(trimmed);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("type", out var typeElement))
                    {
                        _raw.AppendLine(line);
                        return;
                    }

                    _sawEvents = true;

                    switch (typeElement.GetString())
                    {
                        case "message":
                            ConsumeMessage(root);
                            break;
                        case "tool_use":
                        case "tool_result":
                            // The model often narrates before a tool call (for example
                            // "let me read the prompt"). Only the text emitted after the
                            // last tool event belongs to the answer.
                            _delta.Clear();
                            _plain.Clear();
                            break;
                        case "result":
                            ConsumeResult(root);
                            break;
                        case "error":
                            AppendFailure(root);
                            break;
                    }
                }
                catch (JsonException)
                {
                    _raw.AppendLine(line);
                }
            }

            private void ConsumeMessage(JsonElement root)
            {
                if (!root.TryGetProperty("role", out var role) || role.GetString() != "assistant")
                    return;

                if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                    return;

                var text = content.GetString();
                if (string.IsNullOrEmpty(text))
                    return;

                var isDelta = root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.True;
                if (isDelta)
                    _delta.Append(text);
                else
                    _plain.Append(text);
            }

            private void ConsumeResult(JsonElement root)
            {
                if (root.TryGetProperty("status", out var status) &&
                    status.ValueKind == JsonValueKind.String &&
                    !string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                {
                    AppendFailure(root);
                }
            }

            private void AppendFailure(JsonElement root)
            {
                if (Failure.Length > 0)
                    return;

                foreach (var name in new[] { "error", "message", "detail" })
                {
                    if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
                    {
                        var text = element.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            Failure = "codely reported an error: " + text.Trim();
                            return;
                        }
                    }
                }

                Failure = "codely reported an error: " + root.GetRawText();
            }

            private readonly StringBuilder _delta = new();
            private readonly StringBuilder _plain = new();
            private readonly StringBuilder _raw = new();
            private bool _sawEvents;
        }

        private string BuildCommitPrompt(string currentBranch, string changeList, string stagedDiff, string rule)
        {
            var builder = new StringBuilder();
            builder.AppendLine("You are a git commit message generator.");
            builder.AppendLine("Read the rules and the staged diff below, then output ONE single-line commit subject.");
            builder.AppendLine("Write exactly one line. Never add a body, a footer, bullet points or extra paragraphs.");
            builder.AppendLine("Output only that line. Do not explain, do not wrap it in code fences.");
            builder.AppendLine();

            if (!string.IsNullOrWhiteSpace(rule))
            {
                builder.AppendLine("=== COMMIT MESSAGE RULES ===");
                builder.AppendLine(rule.Trim());
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(_service.AdditionalPrompt))
            {
                builder.AppendLine("=== ADDITIONAL INSTRUCTIONS ===");
                builder.AppendLine(_service.AdditionalPrompt.Trim());
                builder.AppendLine();
            }

            builder.AppendLine("=== REPOSITORY ===");
            builder.AppendLine("Current branch: " + currentBranch);
            builder.AppendLine();

            builder.AppendLine("=== CHANGED FILES ===");
            builder.AppendLine("(A added, M modified, D deleted, T type changed, R renamed, C copied)");
            builder.AppendLine(changeList.TrimEnd());
            builder.AppendLine();
            builder.AppendLine("=== STAGED DIFF ===");

            if (string.IsNullOrWhiteSpace(stagedDiff))
            {
                builder.AppendLine("[no staged diff available]");
            }
            else if (stagedDiff.Length > MaxDiffChars)
            {
                builder.AppendLine(stagedDiff.Substring(0, MaxDiffChars));
                builder.AppendLine("...[diff truncated]");
            }
            else
            {
                builder.AppendLine(stagedDiff);
            }

            return builder.ToString();
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var index = text.IndexOf('\n');
            return (index >= 0 ? text.Substring(0, index) : text).Trim();
        }

        private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellation)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellation).ConfigureAwait(false);
                if (line == null)
                    break;

                onLine(line);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
                // Best effort: a leftover temp file is harmless.
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch (Exception)
            {
                // Best effort: the process may have exited between the check and the kill.
            }
        }

        private static string FormatErrors(string errors)
        {
            var text = errors.Trim();
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            if (text.Length > MaxErrorChars)
                text = text.Substring(text.Length - MaxErrorChars);

            return "\n" + text;
        }

        private static string Normalize(string raw)
        {
            var text = StripAnsi(raw).ReplaceLineEndings("\n").Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var index = text.IndexOf('\n');
                if (index >= 0)
                    text = text.Substring(index + 1);
            }

            if (text.EndsWith("```", StringComparison.Ordinal))
                text = text.Substring(0, text.Length - 3).TrimEnd();

            return text.Trim();
        }

        private static string StripAnsi(string input)
        {
            var builder = new StringBuilder(input.Length);
            for (var i = 0; i < input.Length; i++)
            {
                if (input[i] == '\u001b' && i + 1 < input.Length && input[i + 1] == '[')
                {
                    i += 2;
                    while (i < input.Length && !char.IsLetter(input[i]))
                        i++;
                }
                else
                {
                    builder.Append(input[i]);
                }
            }

            return builder.ToString();
        }

        private const int TimeoutSeconds = 300;
        private const int MaxDiffChars = 200 * 1024;
        private const int MaxErrorChars = 2000;

        private readonly Service _service;
    }
}
