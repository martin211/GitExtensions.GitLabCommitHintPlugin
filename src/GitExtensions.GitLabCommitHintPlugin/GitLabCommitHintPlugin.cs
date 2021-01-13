using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using GitExtensions.GitLabCommitHintPlugin.Properties;
using GitExtensions.GitLabCommitHintPlugin.Settings;
using GitExtUtils.GitUI;
using GitLabApiClient;
using GitLabApiClient.Models.Issues.Responses;
using GitLabApiClient.Models.Users.Responses;
using NString;
using GitUIPluginInterfaces;
using GitUI;
using GitUIPluginInterfaces.UserControls;
using ResourceManager;

namespace GitExtensions.GitLabCommitHintPlugin
{
    [Export(typeof(IGitPlugin))]
    public class GitLabCommitHintPlugin : GitPluginBase, IGitPluginForRepository
    {
        private const string DefaultFormat = "#{Iid}: {Title}";

        private static readonly TranslationString PreviewButtonText = new TranslationString("Preview");

        private readonly GitLabCredentialsSetting _credentialsSettings;
        private readonly BoolSetting _enabledSettings = new BoolSetting("GitLab hint plugin enabled", false);
        private readonly StringSetting _stringTemplateSetting = new StringSetting("GitLab Message Template", "Message Template", DefaultFormat, true);
        private readonly StringSetting _urlSettings = new StringSetting("GitLab URL", @"https://www.gitlab.com/");
        private readonly StringSetting _projectSettings = new StringSetting("Project Id", string.Empty);
        private readonly StringSetting _personalToken = new StringSetting("Personal token", string.Empty);
        private static readonly TranslationString EmptyQueryResultMessage = new TranslationString("[Empty Jira Query Result]");
        private static readonly TranslationString EmptyQueryResultCaption = new TranslationString("First Task Preview");

        private NetworkCredential _credential;
        private GitLabClient _client;

        private Button _btnPreview;

        private TaskDTO[] _currentMessages;
        private IGitModule _gitModule;
        private string _stringTemplate = DefaultFormat;
        private Session _currentSession;

        public GitLabCommitHintPlugin() : base(true)
        {
            SetNameAndDescription("GitLab Commit Hint");
            Translate();
            _credentialsSettings = new GitLabCredentialsSetting("GitLabCredentials", "GitLab Credentials",
                () => _gitModule?.WorkingDir);

            Icon = Resources.gitlab;
        }

        public override bool Execute(GitUIEventArgs args)
        {
            if (!_enabledSettings.ValueOrDefault(Settings))
            {
                args.GitUICommands.StartSettingsDialog(this);
                return false;
            }

            return false;
        }

        public override void Register(IGitUICommands gitUiCommands)
        {
            base.Register(gitUiCommands);
            _gitModule = gitUiCommands.GitModule;
            gitUiCommands.PostSettings += gitUiCommands_PostSettings;
            gitUiCommands.PreCommit += gitUiCommands_PreCommit;
            gitUiCommands.PostCommit += gitUiCommands_PostRepositoryChanged;
            gitUiCommands.PostRepositoryChanged += gitUiCommands_PostRepositoryChanged;
            UpdateGitLabSettings();
        }

        public override IEnumerable<ISetting> GetSettings()
        {
            yield return _enabledSettings;

            _urlSettings.CustomControl = new TextBox();
            yield return _urlSettings;

            _credentialsSettings.CustomControl = new CredentialsControl();
            yield return _credentialsSettings;

            _personalToken.CustomControl = new TextBox();
            yield return _personalToken;

            var projectTemplate = new TextBox();
            var btn = new Button
            {
                Top = -4,
                Text = "Select",
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };

            btn.Left = projectTemplate.Width - btn.Width - DpiUtil.Scale(8);
            btn.Size = DpiUtil.Scale(btn.Size);
            btn.Click += BtnOnClick;
            projectTemplate.Controls.Add(btn);
            _projectSettings.CustomControl = projectTemplate;
            yield return _projectSettings;

            var txtTemplate = new TextBox
            {
                Height = DpiUtil.Scale(75), Multiline = true, ScrollBars = ScrollBars.Horizontal
            };

            _btnPreview = new Button
            {
                Text = PreviewButtonText.Text,
                Top = DpiUtil.Scale(45),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            _btnPreview.Size = DpiUtil.Scale(_btnPreview.Size);
            _btnPreview.Click += btnPreviewClick;
            _btnPreview.Left = txtTemplate.Width - _btnPreview.Width - DpiUtil.Scale(8);
            txtTemplate.Controls.Add(_btnPreview);
            _stringTemplateSetting.CustomControl = txtTemplate;
            yield return _stringTemplateSetting;
        }

        private void BtnOnClick(object sender, EventArgs e)
        {
            var form = new ProjectChooser(_client);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _projectSettings.CustomControl.Text = form.ProjectName;
            }
        }

        public override void Unregister(IGitUICommands gitUiCommands)
        {
            base.Unregister(gitUiCommands);
            gitUiCommands.PreCommit -= gitUiCommands_PreCommit;
            gitUiCommands.PostCommit -= gitUiCommands_PostRepositoryChanged;
            gitUiCommands.PostSettings -= gitUiCommands_PostSettings;
            gitUiCommands.PostRepositoryChanged -= gitUiCommands_PostRepositoryChanged;
        }

        private void UpdateGitLabSettings()
        {
            if (!_enabledSettings.ValueOrDefault(Settings))
            {
                return;
            }

            string url = _urlSettings.ValueOrDefault(Settings);
            _credential = _credentialsSettings.GetValueOrDefault(Settings);
            var token = _personalToken.ValueOrDefault(Settings);

            if (!string.IsNullOrWhiteSpace(url))
            {
                if (string.IsNullOrWhiteSpace(token) && (string.IsNullOrWhiteSpace(_credential.Password) || string.IsNullOrWhiteSpace(_credential.UserName)))
                {
                    return;
                }
            }
            else
            {
                return;
            }

            _client = GetClient(_credential);
            _stringTemplate = _stringTemplateSetting.ValueOrDefault(Settings);
            if (_btnPreview == null)
            {
                return;
            }

            _btnPreview.Click -= btnPreviewClick;
            _btnPreview = null;
        }

        private void gitUiCommands_PreCommit(object sender, GitUIEventArgs e)
        {
            if (!_enabledSettings.ValueOrDefault(Settings))
            {
                return;
            }

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                if (_client == null)
                {
                    UpdateGitLabSettings();
                }

                TaskDTO[] currentMessages = await GetMessageToCommitAsync(_client, _stringTemplate);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                _currentMessages = currentMessages;
                foreach (TaskDTO message in _currentMessages)
                {
                    e.GitUICommands.AddCommitTemplate(message.Title, () => message.Text, Icon);
                }
            });
        }

        private void gitUiCommands_PostSettings(object sender, GitUIPostActionEventArgs e)
        {
            UpdateGitLabSettings();
        }

        private void gitUiCommands_PostRepositoryChanged(object sender, GitUIEventArgs e)
        {
            if (!_enabledSettings.ValueOrDefault(Settings))
            {
                return;
            }

            if (_currentMessages == null)
            {
                return;
            }

            foreach (TaskDTO message in _currentMessages)
            {
                e.GitUICommands.RemoveCommitTemplate(message.Title);
            }

            _currentMessages = null;
        }

        private void btnPreviewClick(object sender, EventArgs eventArgs)
        {
            try
            {
                _btnPreview.Enabled = false;
                NetworkCredential credentials = _credentialsSettings.GetValueOrDefault(Settings);
                var client = GetClient(credentials);
                var template = _stringTemplateSetting.CustomControl.Text;

                ThreadHelper.JoinableTaskFactory.RunAsync(
                    async () =>
                    {
                        var message = await GetMessageToCommitAsync(client, template);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        var preview = message.FirstOrDefault();

                        MessageBox.Show(
                            null, 
                             preview == null 
                                 ? EmptyQueryResultMessage.Text 
                                 : preview.Text, 
                            EmptyQueryResultCaption.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

                        _btnPreview.Enabled = true;
                    });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _btnPreview.Enabled = true;
            }
        }

        private async Task<TaskDTO[]> GetMessageToCommitAsync(GitLabClient client, string stringTemplate)
        {
            if (client == null)
            {
                return null;
            }

            try
            {
                IList<Issue> issues = new List<Issue>();

                var projectId = _projectSettings.ValueOrDefault(Settings);

                if (projectId != string.Empty)
                {
                    if (!int.TryParse(projectId, out _))
                    {
                        projectId = projectId.Replace("/", "%2F");
                    }

                    issues = await client.Issues.GetAllAsync(projectId, options: options => options.AssigneeId = _currentSession.Id);
                }
                else
                {
                    issues = await client.Issues.GetAllAsync(options: options => options.AssigneeId = _currentSession.Id);
                }

                return issues
                     .Select(issue => new TaskDTO($"#{issue.Iid}: {issue.Title}", StringTemplate.Format(stringTemplate, issue)))
                     .ToArray();
            }
            catch (Exception ex)
            {
                return new[] { new TaskDTO($"{Description} error", ex.ToString()) };
            }
        }

        private GitLabClient GetClient(NetworkCredential credentials)
        {
            var token = _personalToken.ValueOrDefault(Settings);
            string url = _urlSettings.ValueOrDefault(Settings);

            GitLabClient client = null;

            var t = Task.Run(async () =>
            {
                client = !string.IsNullOrWhiteSpace(token)
                    ? new GitLabClient(url, token)
                    : new GitLabClient(url);

                if (string.IsNullOrWhiteSpace(token))
                {
                    await client.LoginAsync(credentials.UserName, credentials.Password);
                }

                _currentSession = await client.Users.GetCurrentSessionAsync();
            });
            t.Wait();

            return client;
        }

        private class TaskDTO
        {
            public TaskDTO(string title, string text)
            {
                Title = title;
                Text = text;
            }

            public string Title { get; }
            public string Text { get; }
        }
    }
}
