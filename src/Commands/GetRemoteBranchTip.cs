using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Reads the commit a branch currently points to on a remote. A branch that was
    /// never pushed returns an empty tip, which the merge request API reports as
    /// "source_branch does not exist".
    /// </summary>
    public class GetRemoteBranchTip : Command
    {
        public GetRemoteBranchTip(string repo, string remote, string branch)
        {
            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder(256);
            builder.Append("ls-remote --heads ")
                .Append(remote.Quoted())
                .Append(' ')
                .Append($"refs/heads/{branch}".Quoted());
            Args = builder.ToString();
        }

        public async Task<(bool IsSuccess, string Tip, string Error)> ReadAsync()
        {
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (!rs.IsSuccess)
                return (false, string.Empty, string.IsNullOrWhiteSpace(rs.StdErr) ? rs.StdOut.Trim() : rs.StdErr.Trim());

            var line = (rs.StdOut ?? string.Empty).Trim();
            if (line.Length == 0)
                return (true, string.Empty, string.Empty);

            var end = line.IndexOfAny(['\t', ' ', '\r', '\n']);
            return (true, end < 0 ? line : line.Substring(0, end), string.Empty);
        }
    }
}
