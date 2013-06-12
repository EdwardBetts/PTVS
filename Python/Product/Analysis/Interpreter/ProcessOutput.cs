﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.PythonTools {
    /// <summary>
    /// Base class that can receive output from <see cref="ProcessOutput"/>.
    /// 
    /// If this class implements <see cref="IDisposable"/>, it will be disposed
    /// when the <see cref="ProcessOutput"/> object is disposed.
    /// </summary>
    abstract class Redirector {
        /// <summary>
        /// Called when a line is written to standard output.
        /// </summary>
        /// <param name="line">The line of text, not including the newline. This
        /// is never null.</param>
        public abstract void WriteLine(string line);
        /// <summary>
        /// Called when a line is written to standard error.
        /// </summary>
        /// <param name="line">The line of text, not including the newline. This
        /// is never null.</param>
        public abstract void WriteErrorLine(string line);

        /// <summary>
        /// Called when output is written that should be brought to the user's
        /// immediate attention. The default implementation does nothing.
        /// </summary>
        public virtual void Show() {
        }
    }

    /// <summary>
    /// Represents a process and its captured output.
    /// </summary>
    sealed class ProcessOutput : IDisposable {
        private readonly Process _process;
        private readonly string _arguments;
        private readonly List<string> _output, _error;
        private ProcessWaitHandle _waitHandle;
        private readonly Redirector _redirector;
        private bool _isDisposed;

        private static readonly char[] EolChars = new[] { '\r', '\n' };
        private static readonly char[] _needToBeQuoted = new[] { ' ', '"' };

        /// <summary>
        /// Runs the provided executable file and allows the program to display
        /// output to the user.
        /// </summary>
        /// <param name="filename">Executable file to run.</param>
        /// <param name="arguments">Arguments to pass.</param>
        /// <returns>A <see cref="ProcessOutput"/> object.</returns>
        public static ProcessOutput RunVisible(string filename, params string[] arguments) {
            return Run(filename, arguments, null, null, true, null);
        }

        /// <summary>
        /// Runs the provided executable file hidden and captures any output
        /// messages.
        /// </summary>
        /// <param name="filename">Executable file to run.</param>
        /// <param name="arguments">Arguments to pass.</param>
        /// <returns>A <see cref="ProcessOutput"/> object.</returns>
        public static ProcessOutput RunHiddenAndCapture(string filename, params string[] arguments) {
            return Run(filename, arguments, null, null, false, null);
        }

        /// <summary>
        /// Runs the file with the provided settings.
        /// </summary>
        /// <param name="filename">Executable file to run.</param>
        /// <param name="arguments">Arguments to pass.</param>
        /// <param name="workingDirectory">Starting directory.</param>
        /// <param name="env">Environment variables to set.</param>
        /// <param name="visible">
        /// False to hide the window and redirect output to
        /// <see cref="StandardOutputLines"/> and
        /// <see cref="StandardErrorLines"/>.
        /// </param>
        /// <param name="redirector">
        /// An object to receive redirected output.
        /// </param>
        /// <returns>A <see cref="ProcessOutput"/> object.</returns>
        public static ProcessOutput Run(string filename,
                                        IEnumerable<string> arguments,
                                        string workingDirectory,
                                        IEnumerable<KeyValuePair<string, string>> env,
                                        bool visible,
                                        Redirector redirector,
                                        bool quoteArgs = true) {
            var psi = new ProcessStartInfo(filename);
            if (quoteArgs) {
                psi.Arguments = string.Join(" ",
                    arguments.Where(a => a != null).Select(QuoteSingleArgument));
            } else {
                psi.Arguments = string.Join(" ", arguments.Where(a => a != null));
            }
            psi.WorkingDirectory = workingDirectory;
            psi.CreateNoWindow = !visible;
            psi.UseShellExecute = false;
            psi.RedirectStandardError = !visible || (redirector != null);
            psi.RedirectStandardOutput = !visible || (redirector != null);
            if (env != null) {
                foreach (var kv in env) {
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
                }
            }

            var process = new Process();
            process.StartInfo = psi;
            return new ProcessOutput(process, redirector);
        }


        internal static IEnumerable<string> SplitLines(string source) {
            int start = 0;
            int end = source.IndexOfAny(EolChars);
            while (end >= start) {
                yield return source.Substring(start, end - start);
                start = end + 1;
                if (source[start - 1] == '\r' && start < source.Length && source[start] == '\n') {
                    start += 1;
                }

                if (start < source.Length) {
                    end = source.IndexOfAny(EolChars, start);
                } else {
                    end = -1;
                }
            }
            if (start <= 0) {
                yield return source;
            } else if (start < source.Length) {
                yield return source.Substring(start);
            }
        }

        internal static string QuoteSingleArgument(string arg) {
            if (string.IsNullOrEmpty(arg)) {
                return "\"\"";
            }
            if (arg.IndexOfAny(_needToBeQuoted) < 0) {
                return arg;
            }

            if (arg.StartsWith("\"") && arg.EndsWith("\"")) {
                bool inQuote = false;
                int consecutiveBackslashes = 0;
                foreach (var c in arg) {
                    if (c == '"') {
                        if (consecutiveBackslashes % 2 == 0) {
                            inQuote = !inQuote;
                        }
                    }

                    if (c == '\\') {
                        consecutiveBackslashes += 1;
                    } else {
                        consecutiveBackslashes = 0;
                    }
                }
                if (!inQuote) {
                    return arg;
                }
            }

            var newArg = arg.Replace("\"", "\\\"");
            if (newArg.EndsWith("\\")) {
                newArg += "\\";
            }
            return "\"" + newArg + "\"";
        }

        private ProcessOutput(Process process, Redirector redirector) {
            _arguments = QuoteSingleArgument(process.StartInfo.FileName) + " " + process.StartInfo.Arguments;
            _redirector = redirector;
            if (_redirector == null) {
                _output = new List<string>();
                _error = new List<string>();
            }

            _process = process;
            if (_process.StartInfo.RedirectStandardOutput) {
                _process.OutputDataReceived += OnOutputDataReceived;
            }
            if (_process.StartInfo.RedirectStandardError) {
                _process.ErrorDataReceived += OnErrorDataReceived;
            }

            try {
                _process.Start();
            } catch (Exception ex) {
                _error.AddRange(SplitLines(ex.ToString()));
                _process = null;
            }

            if (_process != null) {
                _process.Exited += OnExited;
                _process.EnableRaisingEvents = true;
                if (_process.StartInfo.RedirectStandardOutput) {
                    _process.BeginOutputReadLine();
                } if (_process.StartInfo.RedirectStandardError) {
                    _process.BeginErrorReadLine();
                }
            }
        }

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs e) {
            if (!string.IsNullOrEmpty(e.Data)) {
                foreach (var line in SplitLines(e.Data)) {
                    if (_output != null) {
                        _output.Add(line);
                    }
                    if (_redirector != null) {
                        _redirector.WriteLine(line);
                    }
                }
            }
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs e) {
            if (!string.IsNullOrEmpty(e.Data)) {
                foreach (var line in SplitLines(e.Data)) {
                    if (_error != null) {
                        _error.Add(line);
                    }
                    if (_redirector != null) {
                        _redirector.WriteLine(line);
                    }
                }
            }
        }

        /// <summary>
        /// The arguments that were originally passed, including the filename.
        /// </summary>
        public string Arguments {
            get {
                return _arguments;
            }
        }

        /// <summary>
        /// The exit code or null if the process never started or has not
        /// exited.
        /// </summary>
        public int? ExitCode {
            get {
                if (_process == null || !_process.HasExited) {
                    return null;
                }
                return _process.ExitCode;
            }
        }

        /// <summary>
        /// Gets or sets the priority class of the process.
        /// </summary>
        public ProcessPriorityClass PriorityClass {
            get {
                if (_process == null) {
                    return ProcessPriorityClass.Normal;
                }
                return _process.PriorityClass;
            }
            set {
                if (_process != null) {
                    _process.PriorityClass = value;
                }
            }
        }

        /// <summary>
        /// The redirector that was originally passed.
        /// </summary>
        public Redirector Redirector {
            get { return _redirector; }
        }

        /// <summary>
        /// The lines of text sent to standard output. These do not include
        /// newline characters.
        /// </summary>
        public IEnumerable<string> StandardOutputLines {
            get {
                return _output;
            }
        }

        /// <summary>
        /// The lines of text sent to standard error. These do not include
        /// newline characters.
        /// </summary>
        public IEnumerable<string> StandardErrorLines {
            get {
                return _error;
            }
        }

        /// <summary>
        /// A handle that can be waited on. It triggers when the process exits.
        /// </summary>
        public WaitHandle WaitHandle {
            get {
                if (_waitHandle == null && _process != null) {
                    _waitHandle = new ProcessWaitHandle(_process);
                }
                return _waitHandle;
            }
        }

        /// <summary>
        /// Waits until the process exits.
        /// </summary>
        public void Wait() {
            if (_process != null) {
                _process.WaitForExit();
            }
        }

        /// <summary>
        /// Waits until the process exits or the timeout expires.
        /// </summary>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <returns>
        /// True if the process exited before the timeout expired.
        /// </returns>
        public bool Wait(TimeSpan timeout) {
            if (_process != null) {
                return _process.WaitForExit((int)timeout.TotalMilliseconds);
            }
            return true;
        }

        /// <summary>
        /// Immediately stops the process.
        /// </summary>
        public void Kill() {
            if (_process != null) {
                _process.Kill();
            }
        }

        /// <summary>
        /// Raised when the process exits.
        /// </summary>
        public event EventHandler Exited;

        private void OnExited(object sender, EventArgs e) {
            var evt = Exited;
            if (evt != null) {
                evt(this, e);
            }
        }

        class ProcessWaitHandle : WaitHandle {
            public ProcessWaitHandle(Process process) {
                Debug.Assert(process != null);
                SafeWaitHandle = new SafeWaitHandle(process.Handle, false); // Process owns the handle
            }
        }

        /// <summary>
        /// Called to dispose of unmanaged resources.
        /// </summary>
        public void Dispose() {
            if (!_isDisposed) {
                _isDisposed = true;
                if (_process != null) {
                    _process.Dispose();
                }
                var disp = _redirector as IDisposable;
                if (disp != null) {
                    disp.Dispose();
                }
            }
        }
    }
}