using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Creates a merge request through the GitLab REST API.
    /// Projects that have been renamed or moved answer write requests with
    /// "Non GET methods are not allowed for moved projects", so the project id is
    /// resolved with a GET first and the numeric id is used for the write.
    /// </summary>
    public class CreateMergeRequest
    {
        public class Result
        {
            public bool IsSuccess { get; set; } = false;
            public string WebUrl { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
        }

        public CreateMergeRequest(string apiBase, string token, string projectPath)
        {
            _apiBase = apiBase.TrimEnd('/');
            _token = token;
            _projectPath = projectPath;
        }

        public async Task<Result> CreateAsync(
            string sourceBranch,
            string targetBranch,
            string title,
            string description,
            bool removeSourceBranch,
            CancellationToken cancellation)
        {
            var (projectId, resolveError) = await ResolveProjectIdAsync(cancellation).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolveError))
                return new Result() { Error = resolveError };

            var url = $"{_apiBase}/projects/{projectId}/merge_requests";

            using var buffer = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("source_branch", sourceBranch);
                writer.WriteString("target_branch", targetBranch);
                writer.WriteString("title", title);
                writer.WriteString("description", description);
                writer.WriteBoolean("remove_source_branch", removeSourceBranch);
                writer.WriteEndObject();
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("PRIVATE-TOKEN", _token);
            request.Content = new StringContent(Encoding.UTF8.GetString(buffer.ToArray()), Encoding.UTF8, "application/json");

            try
            {
                using var response = await _client.SendAsync(request, cancellation).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return new Result() { Error = $"HTTP {(int)response.StatusCode}: {Trim(payload)}" };

                using var document = JsonDocument.Parse(payload);
                var webUrl = document.RootElement.TryGetProperty("web_url", out var element) ? element.GetString() : null;
                return new Result() { IsSuccess = true, WebUrl = webUrl ?? string.Empty };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                return new Result() { Error = e.Message };
            }
        }

        private async Task<(string Id, string Error)> ResolveProjectIdAsync(CancellationToken cancellation)
        {
            var url = $"{_apiBase}/projects/{Uri.EscapeDataString(_projectPath)}";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("PRIVATE-TOKEN", _token);

                using var response = await _client.SendAsync(request, cancellation).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return (null, $"Failed to resolve project '{_projectPath}'. HTTP {(int)response.StatusCode}: {Trim(payload)}");

                using var document = JsonDocument.Parse(payload);
                if (!document.RootElement.TryGetProperty("id", out var id))
                    return (null, $"Project '{_projectPath}' response did not contain an id.");

                return (id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString(), null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                return (null, e.Message);
            }
        }

        private static string Trim(string payload)
        {
            if (string.IsNullOrEmpty(payload))
                return string.Empty;

            return payload.Length > 1000 ? payload.Substring(0, 1000) : payload;
        }

        private static readonly HttpClient _client = new HttpClient() { Timeout = TimeSpan.FromSeconds(60) };

        private readonly string _apiBase;
        private readonly string _token;
        private readonly string _projectPath;
    }
}
