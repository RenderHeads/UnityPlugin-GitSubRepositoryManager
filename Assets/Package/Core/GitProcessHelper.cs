﻿using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using JetBrains.Annotations;
using UnityEditor;
using UnityEngine.WSA;

namespace GitRepositoryManager
{
    //This blocks on the caller thread. Should be run through a thread pool.
    public static class GitProcessHelper
    {
        private static readonly object enableRepoLock = new object();
        
        public static bool CheckRemoteExists(string url, string branch, Action<bool, string> onProgress)
        {
            bool success = false;
            string message = string.Empty;
            RunCommand(null, $"git ls-remote {url} {branch}", (s, msg) => { success = s; message = msg; }, out var output);
            if (success)
            {
                if(output.Contains("refs/heads"))
                {
                    onProgress(true, $"Repository {url}:{branch} is valid");
                    return true;
                }

                onProgress(false, $"No repository or branch found for {url}:{branch}");
                return false;
            }

            onProgress(false, message);
            return false;
        }

        public static bool RepositoryIsValid(string directory, Action<bool, string> onProgress)
        {
            bool success = Directory.Exists(directory);
            if (!success)
            {
                onProgress(false, $"Directory does not exist: {directory}");
                return false;
            }

            success = Directory.Exists(Path.Combine(directory,".git")) || Directory.Exists(Path.Combine(directory,".gitsubrepository"));
            if (success)
            {
                onProgress(true, $"Repository is valid: {directory}");
                return true;
            }

            onProgress(false, $".git folder does not exist in: {directory}");
            return false;
        }

        /// <summary>
        /// Requires git 2.28 or later
        /// </summary>
        /// <param name="rootDirectory"></param>
        /// <param name="repositoryDirectory"></param>
        /// <param name="directoryInRepository"></param>
        /// <param name="url"></param>
        /// <param name="branch"></param>
        /// <param name="onProgress"></param>
        /// <returns></returns>
        public static void AddRepository(string rootDirectory, string repositoryDirectory, string directoryInRepository, string url, string branch, Action<bool, string> onProgress)
        {
            string subDirectoryPathRelativeToRepository = directoryInRepository.Substring(repositoryDirectory.Length).Trim('/','\\');
            bool isSparse = !string.IsNullOrEmpty(subDirectoryPathRelativeToRepository);
            RunCommand(rootDirectory, $"git clone {url} --filter=blob:none" + (isSparse?" --sparse ":" ") + $"--single-branch --branch {branch} --depth 1 {repositoryDirectory}", onProgress, out var output);
            //   if (!AssertCommandOutput(", done.", output, onProgress)) { return; } //Causing issues sometimes where the clone succeeds but there is no done output! just "..."
            if (!isSparse)  { return; }
            RunCommand($"{rootDirectory}/{repositoryDirectory}", $"git sparse-checkout set \"{subDirectoryPathRelativeToRepository}\"", onProgress, out output);
            AssertCommandOutput("Running: 'git sparse-checkout set", output, onProgress);
            SetEnableRepository(rootDirectory, repositoryDirectory, false, onProgress);
        }

        public static void RemoveRepository(string rootDirectory, string repositoryDirectory, Action<bool, string> onProgress)
        {
            try
            {
                string path = $"{rootDirectory}/{repositoryDirectory}";
                Directory.Delete(path, true);
                onProgress(true, "Removed repository at " + path);
            }
            catch (Exception e)
            {
                onProgress(false, e.Message);
            }
        }
        
        private static void SetEnableRepository(string rootDirectory, string repositoryDirectory, bool enable, Action<bool, string> onProgress)
        {
            lock (enableRepoLock)
            {
                string gitPath = $"{rootDirectory}/{repositoryDirectory}/{(enable?".gitsubrepository":".git")}";
                string destinationGitPath = $"{rootDirectory}/{repositoryDirectory}/{(enable?".git":".gitsubrepository")}";
                if (Directory.Exists(gitPath))
                {
                    onProgress?.Invoke(true, enable?$"Enabling git for {repositoryDirectory}":$"Disabling git for {repositoryDirectory}");
                    Directory.Move(gitPath, destinationGitPath);
                }
                else
                {
                    onProgress?.Invoke(true, enable?$"{repositoryDirectory} already enabled.":$"{repositoryDirectory} already disabled.");
                }
            }
            
        }

        public static void CheckoutBranch(string rootDirectory, string repositoryDirectory,
            string directoryInRepository, string url, string branch, Action<bool, string> onProgress)
        {
            SetEnableRepository(rootDirectory, repositoryDirectory, true, onProgress);
            string path = $"{rootDirectory}/{repositoryDirectory}";
            RunCommand(path, $"git checkout -B {branch}", onProgress, out var output);
            if(!AssertCommandOutput("Running: 'git checkout -B ", output, onProgress)) { return; }
            SetEnableRepository(rootDirectory, repositoryDirectory, false, onProgress);
        }

        public static void UpdateRepository(string rootDirectory, string repositoryDirectory, string directoryInRepository, string url, string branch, Action<bool, string> onProgress)
        {
            SetEnableRepository(rootDirectory, repositoryDirectory, true, onProgress);

            string path = $"{rootDirectory}/{repositoryDirectory}";
            RunCommand(path, $"git checkout -B {branch}", onProgress, out var output);
            if(!AssertCommandOutput("Running: 'git checkout -B ", output, onProgress)) { return; }

            RunCommand(path, $"git fetch origin refs/heads/{branch}:refs/remotes/origin/{branch} --depth 1", onProgress, out output);
            if(!AssertCommandOutput("Running: 'git fetch origin refs/heads/", output, onProgress)) { return; }

            RunCommand(path, $"git reset --hard origin/{branch}", onProgress, out output);
            AssertCommandOutput("HEAD is now at", output, onProgress);

            SetEnableRepository(rootDirectory, repositoryDirectory, false, onProgress);
        }
        
        public static void PullMerge(string rootDirectory, string repositoryDirectory, string directoryInRepository, string url, string branch, Action<bool, string> onProgress)
        {
            SetEnableRepository(rootDirectory, repositoryDirectory, true, onProgress);

            string path = $"{rootDirectory}/{repositoryDirectory}";
            RunCommand(path, $"git pull", onProgress, out var output);
           // AssertCommandOutput("TODO", output, onProgress);
           SetEnableRepository(rootDirectory, repositoryDirectory, false, onProgress);
        }
        
        public static void Commit(string rootDirectory, string repositoryDirectory,
            string directoryInRepository, string url, string message, Action<bool, string> onProgress)
        {
            SetEnableRepository(rootDirectory, repositoryDirectory, true, onProgress);

            string path = $"{rootDirectory}/{repositoryDirectory}";
            RunCommand(path, $"git add --all", onProgress, out var addOutput);
            //if(!AssertCommandOutput("Running: '", addOutput, onProgress)) { return; }
            
            RunCommand(path, $"git commit -m \"{message}\"", onProgress, out var commitOutput);
            //if(!AssertCommandOutput("Running: '", commitOutput, onProgress)) { return; }

            SetEnableRepository(rootDirectory, repositoryDirectory, false, onProgress);
        }

        public static string Status(string rootDirectory, string repositoryDirectory, Action<bool, string> onProgress)
        {
            SetEnableRepository(rootDirectory, repositoryDirectory, true, onProgress);
            string path = $"{rootDirectory}/{repositoryDirectory}";
            
            RunCommand(path, $"git status --porcelain", onProgress, out string output);
            
            SetEnableRepository(rootDirectory, repositoryDirectory, false, onProgress);
            
            return output;
        }

        public static void PushRepository(string rootDirectory, string repositoryDirectory,
            string directoryInRepository, string url, string branch, Action<bool, string> onProgress)
        {
            SetEnableRepository(rootDirectory, repositoryDirectory, true, onProgress);

            string path = $"{rootDirectory}/{repositoryDirectory}";
            RunCommand(path, $"git push origin {branch}", onProgress, out var output);
            //if(!AssertCommandOutput("Running: ' ", output, onProgress)) { return; }

            SetEnableRepository(rootDirectory, repositoryDirectory, false, onProgress);
        }
        
        public static void ClearLocalChanges(string rootDirectory, string repositoryDirectory,
            string directoryInRepository, string url, string branch, Action<bool, string> onProgress)
        {
            SetEnableRepository(rootDirectory, repositoryDirectory, true, onProgress);

            string path = $"{rootDirectory}/{repositoryDirectory}";
            
            RunCommand(path, $"git clean -fd", onProgress, out var cleanOutput);
            RunCommand(path, $"git reset --hard origin/{branch}", onProgress, out var resetOutput);
            
            //if(!AssertCommandOutput("Running: ' ", output, onProgress)) { return; }

            SetEnableRepository(rootDirectory, repositoryDirectory, false, onProgress);
        }

        public static void OpenRepositoryInExplorer(string rootDirectory, string repositoryDirectory)
        {
            string path = $"{rootDirectory}/{repositoryDirectory}";
            Process.Start(new ProcessStartInfo()
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "open"
            });
            //Leave the process running. User should close it manually.
        }

        /// <summary>
        /// Secondary validation of command output as some commands return no errors and report success even though they did not execute as intended.
        /// </summary>
        /// <param name="expectedPiece"></param>
        /// <param name="actual"></param>
        /// <param name="onProgress"></param>
        /// <param name="errorMessage"></param>
        /// <returns></returns>
        private static bool AssertCommandOutput(string expectedPiece, string actual, Action<bool, string> onProgress, string errorMessage = "")
        {
            if (actual.ToLower().Contains(expectedPiece.ToLower())) return true;
            SetErrorMessage();
            return false;

            void SetErrorMessage()
            {
                if (string.IsNullOrEmpty(errorMessage))
                {
                    onProgress(false, actual);
                }
                else
                {
                    onProgress(false, errorMessage);
                }
            }
        }

        private static void RunCommand(string directory, string command, Action<bool, string> onProgress, out string output)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append($"Running: '{command}' in '{directory}'");
                onProgress(true, sb.ToString());

                ProcessStartInfo procStartInfo = new ProcessStartInfo("cmd", $"/c {command}");

                procStartInfo.RedirectStandardError = true;
                procStartInfo.RedirectStandardInput = true;
                procStartInfo.RedirectStandardOutput = true;
                procStartInfo.UseShellExecute = false;
                procStartInfo.CreateNoWindow = true;

                if (!string.IsNullOrEmpty(directory))
                {
                    procStartInfo.WorkingDirectory = directory;
                }

                Process proc = new Process
                {
                    StartInfo = procStartInfo
                };
                proc.Start();

                proc.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    sb.AppendLine(e.Data);
                    onProgress(true, e.Data);
                };
                proc.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    sb.AppendLine(e.Data);
                    //http://git.661346.n2.nabble.com/git-push-output-goes-into-stderr-td6758028.html
                    //must consider stderr success as git puts non essential output in stderr
                    onProgress(true, e.Data);
                };

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                output = sb.ToString();

                https://stackoverflow.com/questions/4917871/does-git-return-specific-return-error-codes
                if (proc.ExitCode != 0)
                {
                    onProgress(false, output);
                }
            }
            catch (Exception objException)
            {
                output = $"Error in command: '{command}' running in '{directory}', {objException.Message}";
                onProgress(false, output);
            }
        }
    }
}