using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.AI
{
    internal readonly record struct CliTraceEvent(
        string Type, string Label, string Payload, string Thinking = null, bool ContinuesThinking = false);

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
            _executable = string.IsNullOrWhiteSpace(service.ExecutablePath) ? "codely" : service.ExecutablePath.Trim();
            _extraArguments = service.ExtraArguments?.Trim() ?? string.Empty;
            _persistConversation = service.PersistConversation;
            _additionalPrompt = service.AdditionalPrompt;
            _serviceKey = CliConversationState.GetServiceKey(service);
        }

        public async Task<string> GenerateCommitMessageAsync(
            string repo,
            string currentBranch,
            string changeList,
            string stagedDiff,
            string rule,
            CancellationToken cancellation)
        {
            var prompt = BuildCommitPrompt(currentBranch, changeList, stagedDiff, rule);
            var text = await RunPromptAsync(repo, prompt, cancellation).ConfigureAwait(false);
            return FirstLine(text);
        }

        /// <summary>
        /// Runs the configured CLI with a fully caller-built prompt and returns the
        /// cleaned answer. The result may span multiple lines.
        /// </summary>
        public async Task<string> RunPromptAsync(
            string repo,
            string prompt,
            CancellationToken cancellation)
        {
            var path = CliConversationState.GetStatePath(repo);
            FileStream sessionLock;
            try
            {
                sessionLock = await CliConversationState.LockAsync(path, cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Generation remains available if the local session store is inaccessible.
                CheckExtraArguments(SessionArguments);
                var fresh = await RunPromptFileAsync(repo, prompt, null, false, false, cancellation).ConfigureAwait(false);
                return fresh.Text;
            }

            using (sessionLock)
            {
                CliConversationState state;
                try
                {
                    state = CliConversationState.Load(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    state = new() { HasAnalyzed = true };
                }

                var resumeId = _persistConversation ? GetSessionId(state.Generation, _serviceKey) : null;
                var resumeLatest = _persistConversation && resumeId == null && !state.HasAnalyzed;
                if (state.HasAnalyzed || resumeId != null)
                    CheckExtraArguments(SessionArguments);

                var result = await RunConversationAsync(repo, prompt, resumeId, resumeLatest, false, cancellation).ConfigureAwait(false);
                if (result.SessionId != null)
                {
                    state.Generation[_serviceKey] = result.SessionId;
                    try
                    {
                        state.Save(path);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // The result can still be used.
                    }
                }

                return result.Text;
            }
        }

        internal async Task<(string Text, bool StartedOver)> AnalyzeAsync(
            string repo,
            string prompt,
            bool isFollowUp,
            CancellationToken cancellation,
            Action<CliTraceEvent> onTrace = null)
        {
            CheckExtraArguments(AnalysisArguments);
            var path = CliConversationState.GetStatePath(repo);
            using var sessionLock = await CliConversationState.LockAsync(path, cancellation).ConfigureAwait(false);
            var state = CliConversationState.Load(path);
            var resumeId = GetSessionId(state.Analysis, _serviceKey);
            if (!state.HasAnalyzed)
            {
                state.HasAnalyzed = true;
                state.Save(path);
            }

            var result = await RunConversationAsync(repo, prompt, resumeId, false, true, cancellation, onTrace).ConfigureAwait(false);
            var restarted = resumeId != null ? resumeId != result.SessionId : isFollowUp;

            if (result.SessionId != null)
                state.Analysis[_serviceKey] = result.SessionId;
            else
                state.Analysis.Remove(_serviceKey);
            state.Save(path);
            return (result.Text, restarted);
        }

        private static string GetSessionId(Dictionary<string, string> sessions, string key)
        {
            return sessions.TryGetValue(key, out var id) && IsSafeSessionId(id) ? id : null;
        }

        private static bool IsSafeSessionId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128 || !char.IsAsciiLetterOrDigit(id[0]))
                return false;

            foreach (var ch in id)
            {
                if (!char.IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-')
                    return false;
            }

            return true;
        }

        private void CheckExtraArguments(Regex reserved)
        {
            var match = reserved.Match(_extraArguments);
            if (match.Success)
                throw new InvalidOperationException($"SourceGit manages the '{match.Groups[1].Value}' CLI option. Remove it from Extra Arguments.");
        }

        private async Task<CliResult> RunConversationAsync(
            string repo, string prompt, string resumeId, bool resumeLatest, bool analyze,
            CancellationToken cancellation, Action<CliTraceEvent> onTrace = null)
        {
            try
            {
                return await RunPromptFileAsync(repo, prompt, resumeId, resumeLatest, analyze, cancellation, onTrace).ConfigureAwait(false);
            }
            catch (SessionRestoreException) when (resumeId != null)
            {
                return await RunPromptFileAsync(repo, prompt, null, false, analyze, cancellation, onTrace).ConfigureAwait(false);
            }
        }

        private async Task<CliResult> RunPromptFileAsync(
            string repo, string prompt, string resumeId, bool resumeLatest, bool analyze,
            CancellationToken cancellation, Action<CliTraceEvent> onTrace = null)
        {
            var promptFile = Path.Combine(Path.GetTempPath(), "sourcegit-codely-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                await File.WriteAllTextAsync(promptFile, prompt, new UTF8Encoding(false), cancellation).ConfigureAwait(false);
                return await RunAsync(repo, promptFile, resumeId, resumeLatest, analyze, cancellation, onTrace).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(promptFile);
            }
        }

        private async Task<CliResult> RunAsync(
            string repo, string promptFile, string resumeId, bool resumeLatest, bool analyze,
            CancellationToken cancellation, Action<CliTraceEvent> onTrace = null)
        {
            using var process = new Process() { StartInfo = CreateStartInfo(repo, promptFile, resumeId, resumeLatest, analyze) };

            try
            {
                cancellation.ThrowIfCancellationRequested();
                process.Start();
                process.StandardInput.Close();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new Exception(
                    $"Failed to start '{_executable}'. Please check the executable path in preferences. {e.Message}", e);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var parser = new StreamEventParser(onTrace);
            var errors = new StringBuilder();

            var stdout = ReadLinesAsync(process.StandardOutput, parser.Consume, timeout.Token);
            var stderr = ReadLinesAsync(process.StandardError, line => errors.AppendLine(line), timeout.Token);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Both readers may still be waiting for the process when it is canceled.
                }

                cancellation.ThrowIfCancellationRequested();
                throw new TimeoutException($"codely did not finish within {TimeoutSeconds} seconds.");
            }

            var errorText = parser.Failure + FormatErrors(errors.ToString());
            if (resumeId != null && (process.ExitCode != 0 || !string.IsNullOrEmpty(parser.Failure)) &&
                (errorText.Contains("Failed to restore startup resume history", StringComparison.OrdinalIgnoreCase) ||
                 errorText.Contains("No chat recording session found with id:", StringComparison.OrdinalIgnoreCase)))
                throw new SessionRestoreException(errorText);

            if (!string.IsNullOrEmpty(parser.Failure))
                throw new InvalidOperationException(errorText);

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"codely exited with code {process.ExitCode}.{errorText}");

            var text = Normalize(analyze && !parser.SawEvents ? string.Empty : parser.Answer, !analyze);
            if (string.IsNullOrEmpty(text))
                throw new Exception("codely returned no content." + FormatErrors(errors.ToString()));

            return new(text, parser.SessionId);
        }

        private ProcessStartInfo CreateStartInfo(string repo, string promptFile, string resumeId, bool resumeLatest, bool analyze)
        {
            var executable = _executable;
            var extra = _extraArguments.Length == 0 ? string.Empty : " " + _extraArguments;
            var continuation = resumeId != null ? " --resume-session " + resumeId : resumeLatest ? " --continue" : string.Empty;
            var mode = analyze ? " --collaboration-mode ask --no-upm --no-auto-edit-memory" : string.Empty;
            var arguments = "-p \"@" + promptFile + "\" --output-format stream-json" + continuation + mode + extra;
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
            else if (!OperatingSystem.IsWindows() || extension is ".exe" or ".com")
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
        /// Separates the final assistant answer from the optional analysis trace.
        /// </summary>
        private sealed class StreamEventParser(Action<CliTraceEvent> onTrace)
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
            public string SessionId { get; private set; }
            public bool SawEvents => _sawEvents;

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
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("type", out var typeElement) ||
                        typeElement.ValueKind != JsonValueKind.String)
                    {
                        _raw.AppendLine(line);
                        return;
                    }

                    _sawEvents = true;

                    var type = typeElement.GetString();
                    if (onTrace != null)
                        ReportTrace(type, root);

                    switch (type)
                    {
                        case "init":
                            if (root.TryGetProperty("session_id", out var session) &&
                                session.ValueKind == JsonValueKind.String &&
                                IsSafeSessionId(session.GetString()))
                                SessionId = session.GetString();
                            break;
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

            private void ReportTrace(string type, JsonElement root)
            {
                if (type == "message" &&
                    root.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String && role.ValueEquals("assistant") &&
                    root.TryGetProperty("reasoning_content", out var reasoning) &&
                    reasoning.ValueKind == JsonValueKind.String &&
                    reasoning.GetString() is { Length: > 0 } fragment)
                {
                    onTrace(new("thinking", null, null, fragment, _thinking));
                    _thinking = true;
                    return;
                }

                _thinking = false;
                if (type is not ("error" or "result" or "thinking" or "reasoning") &&
                    !type.StartsWith("tool_", StringComparison.Ordinal) &&
                    !type.StartsWith("agent_", StringComparison.Ordinal))
                    return;

                var label = GetTraceLabel(root);
                var thought = type == "agent_think" ? GetString(root, "thought") : null;
                var payload = new StringBuilder();
                foreach (var property in root.EnumerateObject())
                {
                    if (property.NameEquals("type") || (thought != null && property.NameEquals("thought")))
                        continue;

                    if (payload.Length > 0)
                        payload.Append('\n');
                    var value = property.Value;
                    payload.Append(property.Name).Append(": ");
                    payload.Append(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
                }

                onTrace(new(type, label, payload.ToString(), thought));
            }

            private string GetTraceLabel(JsonElement root)
            {
                var name = GetString(root, "tool_name");
                var id = GetString(root, "tool_id");
                if (id != null && name != null)
                {
                    _traceToolNames ??= new(StringComparer.Ordinal);
                    _traceToolNames[id] = name;
                }
                else if (id != null && _traceToolNames != null)
                {
                    _traceToolNames.TryGetValue(id, out name);
                }

                var label = name ?? id ?? GetString(root, "agent_name");
                var status = GetString(root, "status");
                if (string.IsNullOrEmpty(label))
                    return status;

                return status == null ? label : $"{label} · {status}";
            }

            private static string GetString(JsonElement root, string property) =>
                root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            private void ConsumeMessage(JsonElement root)
            {
                if (!root.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String)
                    return;

                if (role.GetString() == "user")
                    _sawUser = true;
                if (role.GetString() != "assistant")
                    return;

                if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                    return;

                var text = content.GetString();
                if (string.IsNullOrEmpty(text))
                    return;

                var isDelta = root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.True;
                if (!isDelta && !_sawUser && SessionId != null &&
                    text.StartsWith($"resumed session {SessionId}, turns=", StringComparison.Ordinal))
                    return;

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

                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    AppendFailure(error);
                    return;
                }

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
            private bool _sawUser;
            private bool _thinking;
            private Dictionary<string, string> _traceToolNames;
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

            if (!string.IsNullOrWhiteSpace(_additionalPrompt))
            {
                builder.AppendLine("=== ADDITIONAL INSTRUCTIONS ===");
                builder.AppendLine(_additionalPrompt.Trim());
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

        private static string Normalize(string raw, bool stripFences)
        {
            var text = StripAnsi(raw).ReplaceLineEndings("\n").Trim();
            if (!stripFences)
                return text;

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
        internal const int MaxDiffChars = 200 * 1024;
        private const int MaxErrorChars = 2000;

        private sealed class SessionRestoreException(string message) : Exception(message);

        private record CliResult(string Text, string SessionId);

        private static readonly Regex SessionArguments = new(
            """(?:^|\s)["']?(--(?:continue|no-continue|resume(?:-session|-path|Session|Path)?))(?=[\s="']|$)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex AnalysisArguments = new(
            """(?:^|\s)["']?(-p|-i|--(?:continue|no-continue|resume(?:-session|-path|Session|Path)?|prompt(?:-interactive)?|example-prompt|output-format|collaboration-mode|upm|auto-edit-memory|experimental-acp|cmd))(?=[\s="']|$)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly string _executable;
        private readonly string _extraArguments;
        private readonly bool _persistConversation;
        private readonly string _additionalPrompt;
        private readonly string _serviceKey;
    }
}
