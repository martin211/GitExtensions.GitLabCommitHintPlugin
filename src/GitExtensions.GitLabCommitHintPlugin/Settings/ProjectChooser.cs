using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using GitUI;
using NGitLab;
using NGitLab.Models;

namespace GitExtensions.GitLabCommitHintPlugin.Settings
{
    public partial class ProjectChooser : Form
    {
        //private TreeNode _previouslySelectedProject;
        public string ProjectName { get; private set; }
        public string BuildIdFilter { get; private set; }

        public ProjectChooser(GitLabClient client)
        {
            InitializeComponent();

            ThreadHelper.FileAndForget(async () =>
            {
                // var projects = await client.Projects.GetAsync(opt =>
                // {
                //     opt.IsMemberOf = true;
                //     opt.WithIssuesEnabled = true;
                //     opt.Owned = true;
                // });
                var projects = client.Projects.GetAsync(new ProjectQuery());
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var gitLabProjects = new List<Project>();
                foreach (var p in projects)
                {
                    gitLabProjects.Add(p);
                }
                
                var rootTreeNode = LoadTreeView(treeViewTeamCityProjects, gitLabProjects);
                rootTreeNode.Expand();
            });
        }

        private void TeamCityBuildChooser_Load(object sender, EventArgs e)
        {
            //ReselectPreviouslySelectedBuild();
        }

        // private void ReselectPreviouslySelectedBuild()
        // {
        //     if (_previouslySelectedProject == null)
        //     {
        //         return;
        //     }
        //
        //     _previouslySelectedProject.Expand();
        //     treeViewTeamCityProjects.SelectedNode = _previouslySelectedProject.Nodes.Find(BuildIdFilter, false).FirstOrDefault()
        //         ?? _previouslySelectedProject;
        // }

        private TreeNode LoadTreeView(TreeView treeView, IEnumerable<Project> rootProject)
        {
            treeView.Nodes.Clear();
            var rootNode = ConvertProjectInTreeNode(rootProject);
            treeView.Nodes.Add(rootNode);
            return rootNode;
        }

        private TreeNode ConvertProjectInTreeNode(IEnumerable<Project> projects)
        {
            var p = projects.Select(c => new
            {
                Key = c.PathWithNamespace.Split('/')[0],
                Name = c.Name,
                CodedName = c.PathWithNamespace.Replace("/", "%2F"),
                Project = c
            }).GroupBy(c => c.Key)
                .OrderBy(c => c.Key)
                .ToDictionary(c => c.Key, c => c.ToList());


            var projectNode = new TreeNode("GitLab server")
            {
                Name = "GitLab server",
            };

            projectNode.Nodes.AddRange(p.Keys.Select(c =>
            {
                var node = new TreeNode(c);

                node.Nodes.AddRange(p[c].Select(n => new TreeNode(n.Name)
                {
                    Name = n.Name,
                    Tag = n.Project
                })
                    .OrderBy(x => x.Name)
                    .ToArray());

                return node; 
            }).ToArray());

            if (projectNode.Nodes.Count == 0)
            {
                projectNode.Nodes.Add(new TreeNode("Loading..."));
            }

            return projectNode;
        }

        private void treeViewTeamCityProjects_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            SelectBuild();
        }

        private void buttonOK_Click(object sender, EventArgs e)
        {
            SelectBuild();
        }

        private void SelectBuild()
        {
            if (treeViewTeamCityProjects.SelectedNode?.Tag is Project build)
            {
                ProjectName = build.PathWithNamespace;
                BuildIdFilter = build.Id.ToString();

                DialogResult = DialogResult.OK;
                Close();
            }
        }

        private void buttonCancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private static bool IsBuildSelected(TreeNode selectedNode)
        {
            return selectedNode?.Tag is Project;
        }

        private void treeViewTeamCityProjects_AfterSelect(object sender, TreeViewEventArgs e)
        {
            buttonOK.Enabled = IsBuildSelected(e.Node);
        }
    }
}
