using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net.Http;
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
using ResourceManager;

namespace GitExtensions.GitLabCommitHintPlugin
{
    [Export(typeof(IGitPlugin))]
    public class GitLabCommitHintPlugin : GitPluginBase, IGitPluginForRepository
    {
        private const string DefaultFormat = "#{Iid}: {Title}";

        private static readonly TranslationString PreviewButtonText = new("Preview");

        private readonly BoolSetting _enabledSettings = new("GitLab hint plugin enabled", false);
        private readonly StringSetting _stringTemplateSetting = new("GitLab Message Template", "Message Template", DefaultFormat, true);
        private readonly StringSetting _urlSettings = new("GitLab URL", @"https://gitlab.com/");
        private readonly StringSetting _projectSettings = new("Project Id", string.Empty);
        private readonly StringSetting _personalToken = new("Personal token", string.Empty);
        private static readonly TranslationString EmptyQueryResultMessage = new("[Empty GitLab Query Result]");
        private static readonly TranslationString EmptyQueryResultCaption = new("First Task Preview");
        private static readonly TranslationString FieldsLabel = new("GitLab fields");
        private static readonly TranslationString WrongSettings = new("Wrong GitLab settings");
        private readonly string _issueFields = $"{{{string.Join("} {", typeof(Issue).GetProperties().Where(i => i.CanRead).Select(i => i.Name).OrderBy(i => i).ToArray())}}}";

        private GitLabClient _client;

        private Button _btnPreview;
        private Button _btnTestConnection;
        private Button _btnSelectProject;

        private TaskDTO[] _currentMessages;
        private string _stringTemplate = DefaultFormat;
        private Session _currentSession;
        private bool _connectionError;
        private string _gitLabUrl;
        private string _gitLabToken;

        private bool IsEnabled => _enabledSettings.ValueOrDefault(Settings);
        private string Url => _urlSettings.ValueOrDefault(Settings);
        private string Token => _personalToken.ValueOrDefault(Settings);

        public GitLabCommitHintPlugin() : base(true)
        {
            SetNameAndDescription("GitLab Commit Hint");
            Translate();

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
            gitUiCommands.PostSettings += gitUiCommands_PostSettings;
            gitUiCommands.PreCommit += gitUiCommands_PreCommit;
            gitUiCommands.PostCommit += gitUiCommands_PostRepositoryChanged;
            gitUiCommands.PostRepositoryChanged += gitUiCommands_PostRepositoryChanged;
            gitUiCommands.PostRegisterPlugin += gitUiCommands_PostRegisterPlugin;
        }

        public override void Unregister(IGitUICommands gitUiCommands)
        {
            base.Unregister(gitUiCommands);
            gitUiCommands.PreCommit -= gitUiCommands_PreCommit;
            gitUiCommands.PostCommit -= gitUiCommands_PostRepositoryChanged;
            gitUiCommands.PostSettings -= gitUiCommands_PostSettings;
            gitUiCommands.PostRepositoryChanged -= gitUiCommands_PostRepositoryChanged;
            gitUiCommands.PostRegisterPlugin -= gitUiCommands_PostRegisterPlugin;
        }

        private void gitUiCommands_PostRegisterPlugin(object sender, GitUIEventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                if (!IsEnabled || string.IsNullOrWhiteSpace(Url) || string.IsNullOrWhiteSpace(Token))
                {
                    return;
                }

                _client = await GetClientAsync();
                if (_client == null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                }
            });
        }

        public override IEnumerable<ISetting> GetSettings()
        {
            yield return _enabledSettings;

            _urlSettings.CustomControl = new TextBox();
            _urlSettings.CustomControl.TextChanged += (sender, args) =>
            {
                _gitLabUrl = (sender as TextBox).Text;
            };
            yield return _urlSettings;

            _personalToken.CustomControl = new TextBox();
            _personalToken.CustomControl.TextChanged += (sender, args) =>
            {
                _gitLabToken = (sender as TextBox).Text;
            };
            yield return _personalToken;

            var projectTemplate = new TextBox();
            _btnSelectProject = new Button
            {
                Top = -4,
                Text = "Select",
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Enabled = _client != null
            };

            _btnSelectProject.Left = projectTemplate.Width - _btnSelectProject.Width - DpiUtil.Scale(8);
            _btnSelectProject.Size = DpiUtil.Scale(_btnSelectProject.Size);
            _btnSelectProject.Click += BtnProjectSelect_Click;
            projectTemplate.Controls.Add(_btnSelectProject);

            _projectSettings.CustomControl = projectTemplate;
            yield return _projectSettings;
            yield return new PseudoSetting(_issueFields, FieldsLabel.Text, DpiUtil.Scale(55));

            var txtTemplate = new TextBox
            {
                Height = DpiUtil.Scale(75), Multiline = true, ScrollBars = ScrollBars.Horizontal
            };

            _btnPreview = new Button
            {
                Text = PreviewButtonText.Text,
                Top = DpiUtil.Scale(45),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Enabled = _client != null
            };
            _btnPreview.Size = DpiUtil.Scale(_btnPreview.Size);
            _btnPreview.Click += btnPreviewClick;
            _btnPreview.Left = txtTemplate.Width - _btnPreview.Width - DpiUtil.Scale(8);
            txtTemplate.Controls.Add(_btnPreview);
            _stringTemplateSetting.CustomControl = txtTemplate;
            yield return _stringTemplateSetting;

            _btnTestConnection = new Button
            {
                Top = -1,
                Width = 200,
                Text = "Test connection",
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };

            _btnTestConnection.Click += _btnTestConnection_Click;

            _btnTestConnection.Left = _urlSettings.CustomControl.Width - _btnTestConnection.Width - DpiUtil.Scale(8);
            _urlSettings.CustomControl.Controls.Add(_btnTestConnection);
        }

        private void _btnTestConnection_Click(object sender, EventArgs e)
        {
            _btnPreview.Enabled = false;
            _btnSelectProject.Enabled = false;
            _btnTestConnection.Enabled = false;

            if (string.IsNullOrEmpty(_gitLabUrl))
            {
                MessageBox.Show("Empty GitLab url!");
                return;
            }

            if (string.IsNullOrEmpty(_gitLabToken))
            {
                MessageBox.Show("Empty GitLab Token!");
                return;
            }

            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                var client = new GitLabClient(_gitLabUrl, _gitLabToken, new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                });

                bool connected = false;

                try
                {
                    var session = await client.Users.GetCurrentSessionAsync();
                    connected = session != null;

                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await UpdateGitLabSettingsAsync();
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    });
                }
                catch (Exception)
                {
                    connected = false;
                }

                if (!connected)
                    MessageBox.Show("Unable connect to GitLab server.", "GitLab hint plugin", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else
                    MessageBox.Show("Success!", "GitLab hint plugin", MessageBoxButtons.OK, MessageBoxIcon.Information);

                _btnPreview.Enabled = _btnSelectProject.Enabled = connected;
                _btnTestConnection.Enabled = true;
            });
        }

        private GitLabClient Client
        {
            get
            {
                if (_client == null)
                {
                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        _client = await GetClientAsync();

                        if (_client != null)
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        }
                    });
                }

                return _client;
            }
        }

        private void BtnProjectSelect_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(Url) || string.IsNullOrEmpty(Token))
            {
                MessageBox.Show(null, WrongSettings.Text, "GitLab hint plugin", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var form = new ProjectChooser(Client);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _projectSettings.CustomControl.Text = form.ProjectName;
            }
        }

        private async Task UpdateGitLabSettingsAsync()
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(Url) || string.IsNullOrWhiteSpace(Token))
            {
                _projectSettings.CustomControl.Enabled = false;

                return;
            }

            _projectSettings.CustomControl.Enabled = true;

            _stringTemplate = _stringTemplateSetting.ValueOrDefault(Settings);
            _client = await GetClientAsync();

            if (_client == null)
            {
                _connectionError = true;
            }

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

            if (_connectionError)
            {
                e.GitUICommands.AddCommitTemplate("Error", () => "Unable connect to GitLab", Icon);
                return;
            }

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                if (_client == null)
                {
                    UpdateGitLabSettingsAsync();
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
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await UpdateGitLabSettingsAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            });
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
                var template = _stringTemplateSetting.CustomControl.Text;

                ThreadHelper.JoinableTaskFactory.RunAsync(
                    async () =>
                    {
                        GitLabClient client = await GetClientAsync();

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

        private async Task<GitLabClient> GetClientAsync()
        {
            string url = _urlSettings.ValueOrDefault(Settings);
            var token = _personalToken.ValueOrDefault(Settings);

            var client = new GitLabClient(url, token, new HttpClientHandler
            {
                AllowAutoRedirect = false,
            });

            try
            {
                _currentSession = await client.Users.GetCurrentSessionAsync();
            }
            catch (Exception)
            {
                _connectionError = true;
                return null;
            }

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
