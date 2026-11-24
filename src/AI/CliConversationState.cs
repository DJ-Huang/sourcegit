using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.AI
{
    internal sealed class CliConversationState
    {
        public bool HasAnalyzed { get; set; }
        public Dictionary<string, string> Analysis { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Generation { get; set; } = new(StringComparer.Ordinal);

        public static string GetStatePath(string repo)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo));
            if (OperatingSystem.IsWindows())
                path = path.ToUpperInvariant();

            return Path.Combine(Native.OS.BasicDirectories.ConfigDir, "cli-sessions", Hash(path) + ".json");
        }

        public static string GetServiceKey(Service service)
        {
            return Hash($"{service.Name}\0{service.ExecutablePath?.Trim()}\0{service.ExtraArguments?.Trim()}");
        }

        public static async Task<FileStream> LockAsync(string path, CancellationToken cancellation)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    return File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException e) when ((e.HResult & 0xffff) is 11 or 32 or 33)
                {
                    await Task.Delay(100, cancellation).ConfigureAwait(false);
                }
            }
        }

        public static CliConversationState Load(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var state = JsonSerializer.Deserialize(stream, JsonCodeGen.Default.CliConversationState);
                if (state?.Analysis != null && state.Generation != null)
                    return state;
            }
            catch (FileNotFoundException)
            {
                return new();
            }
            catch (JsonException)
            {
                // The previous CLI session may have been an analysis session.
            }

            return new() { HasAnalyzed = true };
        }

        public void Save(string path)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonCodeGen.Default.CliConversationState));
            File.Move(temporary, path, true);
        }

        private static string Hash(string text) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
