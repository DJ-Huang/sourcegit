using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.AI
{
    /// <summary>
    /// Runs an external CLI (codely) in headless mode. The prompt is written to a
    /// temporary file and referenced with the <c>@path</c> syntax, which keeps the
    /// command line short, ASCII-only and free of shell quoting or codepage hazards.
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
        /// cleaned output. The result may span multiple lines.
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

            var response = new StringBuilder();
            var errors = new StringBuilder();

            var stdout = ReadLinesAsync(process.StandardOutput, line =>
            {
                // With `--continue` codely prints a resume banner on stdout; it must
                // never reach the result.
                if (line.StartsWith("resumed session ", StringComparison.Ordinal))
                    return;

                response.AppendLine(line);
                onUpdate?.Invoke(line);
            }, timeout.Token);

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

            var text = Normalize(response.ToString());
            if (string.IsNullOrEmpty(text))
                throw new Exception("codely returned no content." + FormatErrors(errors.ToString()));

            return text;
        }

        private ProcessStartInfo CreateStartInfo(string repo, string promptFile)
        {
            var executable = string.IsNullOrWhiteSpace(_service.ExecutablePath) ? "codely" : _service.ExecutablePath.Trim();
            var extra = string.IsNullOrWhiteSpace(_service.ExtraArguments) ? string.Empty : " " + _service.ExtraArguments.Trim();
            var continueArgument = _service.PersistConversation ? " --continue" : string.Empty;
            var arguments = "-p \"@" + promptFile + "\" --output-format text" + continueArgument + extra;
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
