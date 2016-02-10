﻿using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NAppUpdate.Framework;
using NAppUpdate.Framework.Common;
using NAppUpdate.Framework.Tasks;
using NAppUpdate.Framework.Utils;

namespace NAppUpdate.Updater
{
	internal static class AppStart
	{
		private static ArgumentsParser _args;
		private static Logger _logger;
		private static ConsoleForm _console;
		private static string _tempFolder = string.Empty;
		private static string _logFilePath = string.Empty;
		private static string _workingDir = string.Empty;

		private static void Main()
		{
			Setup();

			try
			{
				PerformUpdates();
			}
			catch (Exception ex)
			{
				Log(ex);

				if (!_args.Log && !_args.ShowConsole)
				{
					MessageBox.Show(ex.ToString());
				}

				throw ex;
			}
			finally
			{
				Teardown();
			}
		}

		private static void Setup()
		{
			_workingDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
			_logger = UpdateManager.Instance.Logger;
			_args = ArgumentsParser.Get();

			_args.ParseCommandLineArgs();
			if (_args.ShowConsole)
			{
				_console = new ConsoleForm();
				_console.Show();
			}

			Log("Starting to process cold updates...");

			if (_args.Log)
			{
				// Setup a temporary location for the log file, until we can get the DTO
				_logFilePath = Path.Combine(_workingDir, @"NauUpdate.log");
			}
		}

		private static void PerformUpdates()
		{
			string syncProcessName = _args.ProcessName;

			if (string.IsNullOrEmpty(syncProcessName))
			{
				throw new ArgumentException("Required command line argument is missing", "ProcessName");
			}

			Log("Update process name: '{0}'", syncProcessName);


			// QUESTION(robin): WHAT DOES THIS PART DO? IT LOADS ALL ASSEMBLIES IN THE CURRENT WD?

			// Load extra assemblies to the app domain, if present
			var availableAssemblies = FileSystem.GetFiles(_workingDir, "*.exe|*.dll", SearchOption.TopDirectoryOnly);
			foreach (var assemblyPath in availableAssemblies)
			{
				Log("Loading {0}", assemblyPath);

				if (assemblyPath.Equals(Assembly.GetEntryAssembly().Location, StringComparison.InvariantCultureIgnoreCase) || assemblyPath.EndsWith("NAppUpdate.Framework.dll"))
				{
					Log("\tSkipping (part of current execution)");
					continue;
				}

				try
				{
					// ReSharper disable UnusedVariable
					var assembly = Assembly.LoadFile(assemblyPath);
					// ReSharper restore UnusedVariable
				}
				catch (BadImageFormatException ex)
				{
					Log("\tSkipping due to an error: {0}", ex.Message);
				}
			}

			// Connect to the named pipe and retrieve the updates list
			var dto = NauIpc.ReadDto(syncProcessName) as NauIpc.NauDto;

			// Make sure we start updating only once the application has completely terminated
			Thread.Sleep(1000); // Let's even wait a bit
			bool createdNew;
			using (var mutex = new Mutex(false, syncProcessName + "Mutex", out createdNew))
			{
				try
				{
					if (!createdNew)
					{
						mutex.WaitOne();
					}
				}
				catch (AbandonedMutexException)
				{
					// An abandoned mutex is exactly what we are expecting...
				}
				finally
				{
					Log("The application has terminated (as expected)");
				}
			}

			bool updateSuccessful = true;

			if (dto == null || dto.Configs == null)
			{
				throw new Exception("Received an invalid dto from the pipe");
			}

			// shouldn't really happen
			// QUESTION(robin): Why is it being checked then?
			if (dto.LogItems != null)
			{
				_logger.LogItems.InsertRange(0, dto.LogItems);
			}

			dto.LogItems = _logger.LogItems;

			// Get some required environment variables
			string appPath = dto.AppPath;
			string appDir = dto.WorkingDirectory ?? Path.GetDirectoryName(appPath) ?? string.Empty;
			_tempFolder = dto.Configs.TempFolder;
			string backupFolder = dto.Configs.BackupFolder;
			bool relaunchApp = dto.RelaunchApplication;

			if (!string.IsNullOrEmpty(dto.AppPath))
			{
				_logFilePath = Path.Combine(Path.GetDirectoryName(dto.AppPath), @"NauUpdate.log"); // now we can log to a more accessible location
			}

			if (dto.Tasks == null)
			{
				throw new Exception("The Task list received in the dto is null");
			}
			else if (dto.Tasks.Count == 0)
			{
				throw new Exception("The Task list received in the dto is empty");
			}

			Log("Got {0} task objects", dto.Tasks.Count);

			// Perform the actual off-line update process
			foreach (var t in dto.Tasks)
			{
				Log("Task \"{0}\": {1}", t.Description, t.ExecutionStatus);

				if (t.ExecutionStatus != TaskExecutionStatus.RequiresAppRestart && t.ExecutionStatus != TaskExecutionStatus.RequiresPrivilegedAppRestart)
				{
					Log("\tSkipping");
					continue;
				}

				Log("\tExecuting...");

				// TODO: Better handling on failure: logging, rollbacks
				try
				{
					t.ExecutionStatus = t.Execute(true);
				}
				catch (Exception ex)
				{
					Log(ex);
					updateSuccessful = false;
					t.ExecutionStatus = TaskExecutionStatus.Failed;
				}

				if (t.ExecutionStatus == TaskExecutionStatus.Successful)
				{
					continue;
				}

				Log("\tTask execution failed");
				updateSuccessful = false;
				break;
			}

			if (updateSuccessful)
			{
				Log("Finished successfully");
				Log("Removing backup folder");

				// QUESTION(robin): What is the difference between this clean up and the teardown?
				if (Directory.Exists(backupFolder))
				{
					FileSystem.DeleteDirectory(backupFolder);
				}
			}
			else
			{
				MessageBox.Show("Update Failed");
				Log(Logger.SeverityLevel.Error, "Update failed");
			}

			// Start the application only if requested to do so
			if (relaunchApp)
			{
				Log("Re-launching process {0} with working dir {1}", appPath, appDir);
				ProcessStartInfo info;
				if (_args.ShowConsole)
				{
					info = new ProcessStartInfo
					{
						UseShellExecute = false,
						WorkingDirectory = appDir,
						FileName = appPath,
					};
				}
				else
				{
					info = new ProcessStartInfo
					{
						UseShellExecute = true,
						WorkingDirectory = appDir,
						FileName = appPath,
					};
				}

				try
				{
					NauIpc.LaunchProcessAndSendDto(dto, info, syncProcessName);
				}
				catch (Exception ex)
				{
					throw new UpdateProcessFailedException("Unable to relaunch application and/or send DTO", ex);
				}
			}
		}

		private static void Teardown()
		{
			if (_args.Log)
			{
				// at this stage we can't make any assumptions on correctness of the path
				FileSystem.CreateDirectoryStructure(_logFilePath, true);
				_logger.Dump(_logFilePath);
			}

			if (_args.ShowConsole)
			{
				if (_args.Log)
				{
					_console.WriteLine();
					_console.WriteLine("Log file was saved to {0}", _logFilePath);
					_console.WriteLine();
				}
				_console.WriteLine();
				_console.WriteLine("Press any key or close this window to exit.");
				_console.ReadKey();
			}

			if (!string.IsNullOrEmpty(_tempFolder))
			{
				SelfCleanUp(_tempFolder);
			}

			Application.Exit();
		}

		private static void SelfCleanUp(string tempFolder)
		{
			// Delete the updater EXE and the temp folder
			Log("Removing updater and temp folder... {0}", tempFolder);
			try
			{
				var info = new ProcessStartInfo
				{
					Arguments = string.Format(@"/C ping 1.1.1.1 -n 1 -w 3000 > Nul & echo Y|del ""{0}\*.*"" & rmdir ""{0}""", tempFolder),
					WindowStyle = ProcessWindowStyle.Hidden,
					CreateNoWindow = true,
					FileName = "cmd.exe"
				};

				Process.Start(info);
			}
			catch
			{
				/* ignore exceptions thrown while trying to clean up */
			}
		}

		private static void Log(string message, params object[] args)
		{
			Log(Logger.SeverityLevel.Debug, message, args);
		}

		private static void Log(Logger.SeverityLevel severity, string message, params object[] args)
		{
			message = string.Format(message, args);

			_logger.Log(severity, message);
			if (_args.ShowConsole) _console.WriteLine(message);

			Application.DoEvents();
		}

		private static void Log(Exception ex)
		{
			_logger.Log(ex);

			if (_args.ShowConsole)
			{
				_console.WriteLine("*********************************");
				_console.WriteLine("   An error has occurred:");
				_console.WriteLine("   " + ex);
				_console.WriteLine("*********************************");

				_console.WriteLine();
				_console.WriteLine("The updater will close when you close this window.");
			}
		}
	}
}
