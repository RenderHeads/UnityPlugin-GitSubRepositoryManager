﻿using System;
using System.IO;
using UnityEngine;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitRepositoryManager
{
	/// <summary>
	/// Logical component of a repository, 1-1 relationship with GUIRepopositoryPanel
	/// Read only once created. Create a new one to chaTnge values.
	/// </summary>
	public class Repository
	{
		public const string DEFAULT_MESSAGE = "Empty commit message";
		
		private static List<Repository> _repos = new List<Repository>();
		public static Repository Get(string url, string branch, string rootFolder, string repositoryFolder, string directoryInRepository)
		{
			foreach(Repository repo in _repos)
			{
				//TODO: questionable comparison. Could this not just be name?
				if(repo._state.Url == url &&
					repo._state.RootFolder ==  rootFolder &&
					repo._state.RepositoryFolder == repositoryFolder)
				{
					if (repo._state.Branch != branch)
					{
						throw new Exception($"[Repository] A repository exists that points to a different branch, but its folder is the same!" +
						                    $"\n{repo._state.Url}/{repo._state.Branch} vs {url}/{branch}");
					}
					return repo;
				}
			}

			Repository newRepo = new Repository(url, branch, rootFolder, repositoryFolder, directoryInRepository);
			_repos.Add(newRepo);
			return newRepo;
		}

		public static void Remove(string url, string rootFolder)
		{
			for(int i = _repos.Count-1; i >=0 ; i--)
			{
				Repository repo = _repos[i];
				//TODO: should we not remove just based on name?
				if (repo._state.Url == url &&
					repo._state.RepositoryFolder == rootFolder)
				{
					_repos[i].TryRemoveCopy();
					_repos.RemoveAt(i);
				}
			}
		}
		public static int TotalInitialized
		{
			get
			{
				return _repos.Count;
			}
		}

		//Update status on main thread. Useful when status result is needed immediately. 
		public void BlockAndUpdateStatus()
		{
			//ThreadPool.QueueUserWorkItem(StatusTask, _state);
			StatusTask(_state);
		}

		private string _lastStatus = string.Empty;
		private string _lastPrintableStatus = string.Empty;
		public string PrintableStatus => _lastPrintableStatus;

		private List<KeyValuePair<string, string>> _statusPrettifyLookup = new List<KeyValuePair<string, string>>()
		{
			new KeyValuePair<string, string>("D ", "Deleted"),
			new KeyValuePair<string, string>("?? ", "Untracked"),
			new KeyValuePair<string, string>("M ", "Modified")
			
		};

		///This is called from multiple threads! May block.
		public string CreatePrintableStatus(bool collapse)
		{
			lock(_lastStatus)
			{
				if (_lastStatus == "Repository not found.")
				{
					return _lastStatus;
				}
				
				//TODO: make the status pretty: https://git-scm.com/docs/git-status
				string pathBlurbRemoved = Regex.Replace(_lastStatus, "^Running: 'git status --porcelain' in '.*'", "");

				if (pathBlurbRemoved.Contains("status"))
				{
					Debug.Log(pathBlurbRemoved);
				}
				
				string[] lines = pathBlurbRemoved.Split('\n');
				
				int lineCount = 0;
				string formatttedStatus = String.Empty;
				int numMetaChanged = 0;
				for(int i = 0; i < lines.Length; i++)
				{
					if (string.IsNullOrEmpty(lines[i]))
					{
						continue;
					}

					if (lines[i].Trim('\"',' ', '\r').EndsWith(".meta") && collapse)
					{
						numMetaChanged++;
						continue;
					}
					
					//Prettify common outputs
					string line = lines[i].Trim();
					foreach (var kvp in _statusPrettifyLookup)
					{
						if (line.StartsWith(kvp.Key))
						{
							line = $"{kvp.Value} {line.Substring(kvp.Key.Length)}";
							break;
						}
					}
					
					formatttedStatus += $"{line.Trim()}\n";
					lineCount++;
				}

				if (numMetaChanged > 0)
				{
					formatttedStatus += $"{numMetaChanged} Meta file changes detected.\n";
					lineCount++;
				}
				
				
				HasUncommittedChanges = lineCount > 1;
					
				return $"{formatttedStatus.Trim('\r', '\n', ' ')}";
			}
		}

		public volatile bool HasUncommittedChanges;

		//TODO: add this functionality!
		public volatile bool AheadOfOrigin;
		//TODO: add this functionality!
		public volatile bool BehindOrigin;

		public class RepoState
		{
			public string Url; //Remote repo base
			public string Branch; // The branch the repo will be on
			public string RootFolder; //The root of the git repo. Absolute path.
			public string RepositoryFolder; //The folder that the repository will be initialized in. Relative from RootFolder.
			public string DirectoryInRepository; //The folder in the repository that will be checked out sparsely. Relative from RootFolder.
		}
		
		public class PushState : RepoState
		{
			public string Message; //Commit message
		}

		private readonly RepoState _state;

		//shared between thread pool process and main thread
		private volatile bool _inProgress;
		private volatile bool _refreshPending = false;

		public string Branch => _state.Branch;

		public bool RefreshPending
		{
			get => _refreshPending;
			set => _refreshPending = value;
		}

		public string AbsolutePath
		{
			get
			{
				Debug.Assert(_state != null, "State is null when trying to get AbsolutePath in repository");
				return $"{_state.RootFolder}/{_state.RepositoryFolder}";
			}
		}

		public struct Progress
		{
			public Progress(float normalizedProgress, string message, bool error)
			{
				NormalizedProgress = normalizedProgress;
				Message = message;
				Error = error;
			}

			public float NormalizedProgress;
			public string Message;
			public bool Error;
		}

		private ConcurrentQueue<Progress> _progressQueue = new ConcurrentQueue<Progress>();

		public Repository(string url, string branch, string rootFolder, string repositoryFolder, string directoryInRepository)
		{
			_state = new RepoState
			{
				Url = url,
				Branch = branch,
				RootFolder = rootFolder,
				RepositoryFolder = repositoryFolder,
				DirectoryInRepository = directoryInRepository
			};
		}

		public bool TryUpdate()
		{
			if(!_inProgress)
			{
				_inProgress = true;
				ThreadPool.QueueUserWorkItem(UpdateTask, _state);
				return true;
			}
			else
			{
				return false;
			}
		}

		public bool PushChanges(string branch = null, string message = null)
		{
			if (!_inProgress)
			{
				_inProgress = true;
				ThreadPool.QueueUserWorkItem(PushTask, new PushState()
				{
					Url = _state.Url,
					Branch = branch ?? _state.Branch,
					RootFolder = _state.RootFolder,
					RepositoryFolder = _state.RepositoryFolder,
					DirectoryInRepository = _state.DirectoryInRepository,
					Message = message ?? DEFAULT_MESSAGE
				});
				return true;
			}
			else
			{
				return false;
			}
		}
		
		public bool ClearLocalChanges()
		{
			if(!_inProgress)
			{
				//_inProgress = true;
				//ThreadPool.QueueUserWorkItem(ClearLocalChangesTask, _state);
				ClearLocalChangesTask(_state);
				return true;
			}
			else
			{
				return false;
			}
		}

		public bool TryRemoveCopy()
		{

			if (!Directory.Exists(AbsolutePath))
			{
				return false;
			}

			// remove read only attribute on all files so we can delete them (this is primarily for the .git folders files, as git sets readonly)
			var files = Directory.GetFiles(AbsolutePath, "*.*", SearchOption.AllDirectories).OrderBy(p => p).ToList();
			foreach (string filePath in files)
			{
				File.SetAttributes(filePath, FileAttributes.Normal);
			}

			Directory.Delete(AbsolutePath, true);
			return true;
		}

		public bool InProgress => _inProgress;

		public bool LastOperationSuccess
		{
			get
			{
				if (_progressQueue.Count <= 0) return true;
				_progressQueue.TryPeek(out Progress progress);
				return !progress.Error;
			}
		}

		public Progress GetLastProgress()
		{
			Progress currentProgress;
			if(_progressQueue.Count > 0)
			{
				if(_progressQueue.Count > 1)
				{
					_progressQueue.TryDequeue(out currentProgress);
				}
				else
				{
					_progressQueue.TryPeek(out currentProgress);
				}
			}
			else
			{
				currentProgress = new Progress(0, "Update Pending", false);
			}

			return currentProgress;
		}
		
		/// <summary>
		/// Made to run in a thread pool, but is running synchronously.
		/// </summary>
		/// <param name="stateInfo"></param>
		private void StatusTask(object stateInfo)
		{
			//Do as much as possible outside of unity so we dont get constant rebuilds. Only when everything is ready
			RepoState state = (RepoState)stateInfo;

			if(state == null)
			{
				_progressQueue.Enqueue(new Progress(0, "Repository state info is null",true));
				return;
			}

			if (GitProcessHelper.RepositoryIsValid(state.RepositoryFolder, OnProgress))
			{
				lock (_lastStatus)
				{
					_lastStatus = GitProcessHelper.Status(state.RootFolder, state.RepositoryFolder, OnProgress);
				}
			}
			else
			{
				lock (_lastStatus)
				{
					_lastStatus = "Repository not found.";
				}
			}
			
			_lastPrintableStatus = CreatePrintableStatus(true);

			void OnProgress(bool success, string message)
			{
				_progressQueue.Enqueue(new Progress(0, message, !success));
			}
		}
		
		/// <summary>
		/// Made to run in a thread pool, but is running synchronously.
		/// </summary>
		/// <param name="stateInfo"></param>
		private void ClearLocalChangesTask(object stateInfo)
		{
			//Do as much as possible outside of unity so we dont get constant rebuilds. Only when everything is ready
			RepoState state = (RepoState)stateInfo;

			if(state == null)
			{
				_progressQueue.Enqueue(new Progress(0, "Repository state info is null",true));
				return;
			}

			if (GitProcessHelper.RepositoryIsValid(state.RepositoryFolder, OnProgress))
			{
				GitProcessHelper.ClearLocalChanges(state.RootFolder,state.RepositoryFolder, 
					state.DirectoryInRepository, state.Url, state.Branch, OnProgress);
				_refreshPending = true;
			}
			else
			{
				lock (_lastStatus)
				{
					_progressQueue.Enqueue(new Progress(0, "Repository not found",true));
				}
			}
			
			_lastPrintableStatus = CreatePrintableStatus(true);

			void OnProgress(bool success, string message)
			{
				_progressQueue.Enqueue(new Progress(0, message, !success));
			}
		}

		/// <summary>
		/// Runs in a thread pool. Should clone then checkout the appropriate branch/commit. copy subdirectory into specified repo.
		/// </summary>
		/// <param name="stateInfo"></param>
		private void UpdateTask(object stateInfo)
		{
			try
			{
				//Do as much as possible outside of unity so we dont get constant rebuilds. Only when everything is ready
				RepoState state = (RepoState)stateInfo;

				if(state == null)
				{
					_progressQueue.Enqueue(new Progress(0, "Repository state info is null",true));
					return;
				}

				if (GitProcessHelper.RepositoryIsValid(state.RepositoryFolder, OnProgress))
				{
					GitProcessHelper.UpdateRepository(state.RootFolder,state.RepositoryFolder, state.DirectoryInRepository, state.Url, state.Branch, OnProgress);
				}
				else
				{
					GitProcessHelper.AddRepository(state.RootFolder, state.RepositoryFolder, state.DirectoryInRepository, state.Url, state.Branch, OnProgress);
				}

				//Once completed
				if (_progressQueue.Count > 0)
				{
					//Get the latest progress
					if (!_progressQueue.ToArray()[_progressQueue.Count-1].Error)
					{
						_refreshPending = true;
					}
				}

				_inProgress = false;

				void OnProgress(bool success, string message)
				{
					_progressQueue.Enqueue(new Progress(0, message, !success));
				}
			}
			catch (Exception e)
			{
				Debug.LogError($"[Repository.UpdateTask] {e.Message}");
			}
		}

		/// <summary>
		/// Runs in a thread pool. Should push based on settings of push window. copy subdirectory into specified repo.
		/// </summary>
		/// <param name="stateInfo"></param>
		private void PushTask(object stateInfo)
		{
			try
			{
				PushState state = (PushState) stateInfo;

				if (state == null)
				{
					_progressQueue.Enqueue(new Progress(0, "Repository state info is null", true));
					return;
				}

				if (GitProcessHelper.RepositoryIsValid(state.RepositoryFolder, OnProgress))
				{
					GitProcessHelper.CheckoutBranch(state.RootFolder, state.RepositoryFolder, state.DirectoryInRepository,
						state.Url, state.Branch, OnProgress);
					GitProcessHelper.PullMerge(state.RootFolder, state.RepositoryFolder, state.DirectoryInRepository,
						state.Url, state.Branch, OnProgress);
					GitProcessHelper.Commit(state.RootFolder, state.RepositoryFolder, state.DirectoryInRepository,
						state.Url, state.Message, OnProgress);
					GitProcessHelper.PushRepository(state.RootFolder, state.RepositoryFolder,
						state.DirectoryInRepository, state.Url, state.Branch, OnProgress);
				}

				//Once completed
				if (_progressQueue.Count > 0)
				{
					//Get the latest progress
					if (!_progressQueue.ToArray()[_progressQueue.Count - 1].Error)
					{
						_refreshPending = true;
					}
				}

				_inProgress = false;

				void OnProgress(bool success, string message)
				{
					_progressQueue.Enqueue(new Progress(0, message, !success));
				}
			}
			catch (Exception e)
			{
				Debug.LogError($"[Repository.PushTask] {e.Message}");
			}
		}


		//TODO: redo for whole folder
		private void SetIgnore(string parentRoot, string relativeFolderToIgnore, bool ignore)
		{
			//This is a pretty ok attempt at automating adding to gitignore. If people go in and add to the ignore manually this may not pick that up but that's fine.
			string ignoreFile = $"{parentRoot}/.gitignore";
			string ignoreString = $"{relativeFolderToIgnore}/*";
			if (!File.Exists(ignoreFile))
			{
				Debug.LogWarning($"[RepositoryManager] Can not find {ignoreFile}. Not ignoring repository");
				return;
			}

			List<string> lines = new List<string>(File.ReadAllLines(ignoreFile));
			if (!ignore)
			{
				//If we are not ignoring we look through all the lines to remove the folder if it was previously ignored
				for (int i = lines.Count - 1; i >= 0; i++)
				{
					if (lines[i].Contains(ignoreString))
					{
						lines.RemoveAt(i);
					}
				}
			}
			else
			{
				//Else we add the ignore string to the end of the file.
				lines.Add(ignoreString);
			}
		}
	}
}
