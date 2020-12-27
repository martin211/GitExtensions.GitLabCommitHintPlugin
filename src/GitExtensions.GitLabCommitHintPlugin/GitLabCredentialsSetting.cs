using System;
using GitUIPluginInterfaces;

namespace GitExtensions.GitLabCommitHintPlugin
{
    public class GitLabCredentialsSetting : CredentialsSetting
    {
        public GitLabCredentialsSetting(string name, string caption, Func<string> getWorkingDir)
            : base(name, caption, getWorkingDir)
        {
        }
    }
}
