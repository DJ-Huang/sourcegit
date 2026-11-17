using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Pushes a local branch to the same name on a remote. Used to make the source
    /// branch available before creating a merge request, because the API only accepts
    /// branches that already exist on the server.
    /// </summary>
    public class PushBranch : Command
    {
        public PushBranch(string repo, string remote, string branch, string sshKey)
        {
            WorkingDirectory = repo;
            Context = repo;
            SSHKey = sshKey ?? string.Empty;

            var refspec = $"refs/heads/{branch}:refs/heads/{branch}";
            var builder = new StringBuilder(256);
            builder.Append("push --progress --verbose ")
                .Append(remote.Quoted())
                .Append(' ')
                .Append(refspec.Quoted());
            Args = builder.ToString();
        }

        public async Task<bool> RunAsync()
        {
            return await ExecAsync().ConfigureAwait(false);
        }
    }
}
