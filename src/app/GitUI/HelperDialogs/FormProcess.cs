using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUI.UserControls;

namespace GitUI.HelperDialogs
{
    /// <param name="isError">if command finished with error.</param>
    /// <param name="form">this form.</param>
    /// <returns>if handled.</returns>
    public delegate bool HandleOnExit(ref bool isError, FormProcess form);

    public partial class FormProcess : FormStatus
    {
        public string Remote { get; set; }
        public string? ProcessInput { get; }
        public readonly string WorkingDirectory;
        public HandleOnExit? HandleOnExitCallback { get; set; }
        public readonly Dictionary<string, string> ProcessEnvVariables = [];

        private FormProcess(IGitUICommands commands, ConsoleOutputControl? outputControl, ArgumentString arguments, string workingDirectory, string? input, bool useDialogSettings, string? process)
            : base(commands, outputControl, useDialogSettings)
        {
            ProcessCallback = ProcessStart;
            AbortCallback = ProcessAbort;
            Remote = "";
            ProcessInput = input;
            WorkingDirectory = workingDirectory;

            if (process is null)
            {
                string wslDistro = AppSettings.WslGitEnabled ? PathUtil.GetWslDistro(workingDirectory) : "";
                if (!string.IsNullOrEmpty(wslDistro))
                {
                    process = AppSettings.WslCommand;

                    // In some WSL environments the current working directory is not passed along to the git command without using the `--cd` argument. Adding it to
                    // the command line is required for these environments. For those that do not need it using the argument is just redundant.
                    arguments = $"-d {wslDistro} --cd {WorkingDirectory.RemoveTrailingPathSeparator().Quote()} {AppSettings.WslGitCommand} {arguments}";
                }
            }

            ProcessString = process ?? AppSettings.GitCommand;
            ProcessArguments = arguments;

            string displayPath = PathUtil.GetDisplayPath(WorkingDirectory);
            if (!string.IsNullOrWhiteSpace(displayPath))
            {
                Text += $" ({displayPath})";
            }

            ConsoleOutput.ProcessExited += delegate { OnExit(ConsoleOutput.ExitCode); };
            ConsoleOutput.DataReceived += DataReceivedCore;
        }

        public FormProcess(IGitUICommands commands, ArgumentString arguments, string workingDirectory, string? input, bool useDialogSettings, string? process = null)
            : this(commands, outputControl: null, arguments, workingDirectory, input, useDialogSettings, process)
        {
        }

        // Note that "DialogResult FormProcess.ShowDialog(owner)" may exit when the process (command) finishes,
        // so that result is other than OK or Cancel.

        public static bool ShowDialog(IWin32Window? owner, IGitUICommands commands, ArgumentString arguments, string workingDirectory, string? input, bool useDialogSettings, string? process = null)
        {
            DebugHelpers.Assert(owner is not null, "Progress window must be owned by another window! This is a bug, please correct and send a pull request with a fix.");

            using FormProcess formProcess = new(commands, arguments, workingDirectory, input, useDialogSettings, process);
            formProcess.ShowDialog(owner);
            return !formProcess.ErrorOccurred();
        }

        public static string ReadDialog(IWin32Window? owner, IGitUICommands commands, ArgumentString arguments, string workingDirectory, string? input, bool useDialogSettings)
        {
            DebugHelpers.Assert(owner is not null, "Progress window must be owned by another window! This is a bug, please correct and send a pull request with a fix.");

            using FormProcess formProcess = new(commands, arguments, workingDirectory, input, useDialogSettings);
            formProcess.ShowDialog(owner);
            return formProcess.GetOutputString();
        }

        protected virtual void BeforeProcessStart()
        {
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape:
                    {
                        Close();
                        return true;
                    }

                default:
                    {
                        return base.ProcessCmdKey(ref msg, keyData);
                    }
            }
        }

        private void ProcessStart(FormStatus form)
        {
            BeforeProcessStart();
            string quotedProcessString = ProcessString;
            if (quotedProcessString.IndexOf(' ') != -1)
            {
                quotedProcessString = quotedProcessString.Quote();
            }

            AppendMessage($"{quotedProcessString} {ProcessArguments}{Environment.NewLine}");

            try
            {
                ConsoleOutput.StartProcess(ProcessString, ProcessArguments, WorkingDirectory, ProcessEnvVariables);

                if (!string.IsNullOrEmpty(ProcessInput))
                {
                    throw new NotSupportedException("No non-NULL usages of ProcessInput are currently expected.");  // Not implemented with all terminal variations, so let's postpone until there's at least one non-null case
/*
                    Thread.Sleep(500);
                    Process.StandardInput.Write(ProcessInput);
                    AddMessageLine(string.Format(":: Wrote [{0}] to process!\r\n", ProcessInput));
*/
                }
            }
            catch (Exception e)
            {
                AppendMessage($"{Environment.NewLine}{e.ToStringWithData()}{Environment.NewLine}");
                OnExit(1);
            }
        }

        private void ProcessAbort(FormStatus form)
        {
            KillProcess();
        }

        protected void KillProcess()
        {
            try
            {
                ConsoleOutput.KillProcess();

                GitModule module = new(WorkingDirectory);
                module.UnlockIndex(includeSubmodules: true);
            }
            catch
            {
                // no-op
            }
        }

        /// <param name="isError">if command finished with error.</param>
        /// <returns>if handled.</returns>
        protected virtual bool HandleOnExit(ref bool isError)
        {
            return HandleOnExitCallback is not null && HandleOnExitCallback(ref isError, this);
        }

        private void OnExit(int exitcode)
        {
            this.InvokeAndForget(() =>
            {
                bool isError;
                try
                {
                    isError = exitcode != 0;

                    if (HandleOnExit(ref isError))
                    {
                        return;
                    }
                }
                catch
                {
                    isError = true;
                }

                Done(!isError);
            });
        }

        protected virtual void DataReceived(object sender, TextEventArgs e)
        {
        }

        private void DataReceivedCore(object sender, TextEventArgs e)
        {
            // CarriageReturn has its literal meaning here, i.e. it is not a line end, but terminates transient progress information
            if (e.Text.EndsWith(Delimiters.CarriageReturn))
            {
                this.InvokeAndForget(() => SetProgressAsync(e.Text.TrimEnd()));
            }
            else
            {
                const string ansiSuffix = "\u001B[K";
                string line = e.Text.Replace(ansiSuffix, "");

                if (ConsoleOutput.IsDisplayingFullProcessOutput)
                {
                    OutputLog.Append(line); // To the log only, display control displays it by itself
                }
                else
                {
                    AppendOutput(line); // Both to log and display control
                }
            }

            DataReceived(sender, e);
        }

        /// <summary>
        /// Appends a line of text (CRLF added automatically) both to the logged output (<see cref="FormStatus.GetOutputString"/>) and to the display console control.
        /// </summary>
        public void AppendOutput(string line)
        {
            // To the internal log (which can be then retrieved as full text from this form)
            OutputLog.Append(line);

            // To the display control
            AppendMessage(line);
        }

        public static bool IsOperationAborted(string dialogResult)
            => dialogResult.Trim(Delimiters.LineFeedAndCarriageReturn) == "Aborted";
    }
}
