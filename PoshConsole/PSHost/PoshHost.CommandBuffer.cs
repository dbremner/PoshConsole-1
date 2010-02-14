﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Threading;

namespace PoshConsole
{
   internal struct PipelineExecutionResult
   {
      private readonly Collection<PSObject> _output;
      private readonly Collection<Object> _errors;
      private readonly Exception _failure;
      private readonly PipelineState _state;

      public Collection<PSObject> Output { get { return _output; } }
      public Collection<Object> Errors { get { return _errors; } }
      public Exception Failure { get { return _failure; } }
      public PipelineState State { get { return _state; } }

      public PipelineExecutionResult(Collection<PSObject> output, Collection<Object> errors, Exception failure, PipelineState state)
      {
         _failure = failure;
         _errors = errors ?? new Collection<Object>();
         _output = output ?? new Collection<PSObject>();
         _state = state;
      }
   }

   internal delegate void PipelineOutputHandler(PipelineExecutionResult result);

   public delegate void ShouldExitHandler(object source, int exitCode);
   public delegate void RunspaceReadyHandler(object source, RunspaceState stateEventArgs);



   internal struct InputBoundCommand
   {
      public string[] Commands;
      public bool AddToHistory;
      public bool DefaultOutput;
      public bool RunAsScript;
      public bool UseLocalScope;

      //public Pipeline Pipeline;
      public IEnumerable Input;
      public PipelineOutputHandler Callback;

      public InputBoundCommand(/*Pipeline pipeline,*/ string[] commands, IEnumerable input, PipelineOutputHandler callback)
      {
         //Pipeline = pipeline;
         Commands = commands;
         Input = input;
         Callback = callback;

         AddToHistory = true;
         DefaultOutput = true;
         RunAsScript = true;
         UseLocalScope = false;

      }

      public InputBoundCommand(/*Pipeline pipeline,*/ string[] commands, IEnumerable input, bool addToHistory, PipelineOutputHandler callback)
      {
         //Pipeline = pipeline;
         Commands = commands;
         Input = input;
         Callback = callback;
         AddToHistory = addToHistory;

         DefaultOutput = true;
         RunAsScript = true;
         UseLocalScope = false;
      }
   }


   public class SyncEvents
   {
      // the exit thread event needs to be in every signalling array
      private EventWaitHandle myExitThreadEvent;
      // this event signals new items
      private EventWaitHandle myNewItemEvent;
      // these events signal the end of processing
      // the empty queue event: temporary end of processing because there's no items left
      private EventWaitHandle myEmptyQueueEvent;
      // the abort queue event: permanent end of processing because the web server(s) won't respond
      private EventWaitHandle myAbortProcessingEvent;

      private EventWaitHandle myPipelineFinishedEvent;


      private WaitHandle[] myNewItemHandles;
      private WaitHandle[] myEndQueueHandles;



      public SyncEvents()
      {
         myNewItemEvent = new AutoResetEvent(false);
         myExitThreadEvent = new ManualResetEvent(false);
         myEmptyQueueEvent = new ManualResetEvent(false);
         myAbortProcessingEvent = new ManualResetEvent(false);
         myPipelineFinishedEvent = new AutoResetEvent(false);

         myNewItemHandles = new WaitHandle[3];
         myNewItemHandles[0] = myExitThreadEvent;
         myNewItemHandles[1] = myAbortProcessingEvent;
         myNewItemHandles[2] = myNewItemEvent;

         myEndQueueHandles = new WaitHandle[3];
         myEndQueueHandles[0] = myExitThreadEvent;
         myEndQueueHandles[1] = myAbortProcessingEvent;
         myEndQueueHandles[2] = myEmptyQueueEvent;
      }

      public EventWaitHandle PipelineFinishedEvent
      {
         get { return myPipelineFinishedEvent; }
      }

      /// <summary>
      /// Gets the exit thread event.
      /// </summary>
      /// <value>The exit thread event.</value>
      public EventWaitHandle ExitThreadEvent
      {
         get { return myExitThreadEvent; }
      }
      /// <summary>
      /// Gets the new item event.
      /// </summary>
      /// <value>The new item event.</value>
      public EventWaitHandle NewItemEvent
      {
         get { return myNewItemEvent; }
      }
      /// <summary>
      /// Gets the empty queue event.
      /// </summary>
      /// <value>The empty queue event.</value>
      public EventWaitHandle EmptyQueueEvent
      {
         get { return myEmptyQueueEvent; }
      }
      /// <summary>
      /// Gets the abort queue event.
      /// </summary>
      /// <value>The abort queue event.</value>
      public EventWaitHandle AbortQueueEvent
      {
         get { return myAbortProcessingEvent; }
      }


      public WaitHandle[] NewItemEvents
      {
         get { return myNewItemHandles; }
      }

      public WaitHandle[] TerminationEvents
      {
         get { return myEndQueueHandles; }
      }
   }


   internal class CommandRunner : IDisposable
   {
#if DEBUG
      protected static TraceSwitch parseTrace = new TraceSwitch("parsing", "Controls the output level of the parsing tracers", "4");
      protected static TraceSwitch threadTrace = new TraceSwitch("threading", "Controls the output level of the thread interaction tracers", "4");
#else
        protected static TraceSwitch parseTrace = new TraceSwitch("parsing", "Controls the output level of the parsing tracers", "1");
        protected static TraceSwitch threadTrace = new TraceSwitch("threading", "Controls the output level of the thread interaction tracers", "0");
#endif
      protected Queue<InputBoundCommand> _CommandQueue;
      protected Thread _WorkerThread;

      private SyncEvents _SyncEvents;
      public SyncEvents SyncEvents
      {
         get { return _SyncEvents; }
         set { _SyncEvents = value; }
      }

      private Pipeline _Pipeline = null;
      private Runspace _runSpace;

      public Pipeline CurrentPipeline
      {
         get { return _Pipeline; }
      }
      public readonly Command DefaultOutputCommand;
      public event ShouldExitHandler ShouldExit;
      public event RunspaceReadyHandler RunspaceReady;
      /// <summary> Initializes a new instance of the <see cref="CommandRunner"/> class 
      /// with no cookies.
      /// </summary>
      public CommandRunner(System.Management.Automation.Host.PSHost host)
      {
         _CommandQueue = new Queue<InputBoundCommand>();
         _SyncEvents = new SyncEvents();

         // pre-create this
         DefaultOutputCommand = new Command("Out-Default");
         // for now, merge the errors with the rest of the output
         DefaultOutputCommand.MergeMyResults(PipelineResultTypes.Error, PipelineResultTypes.Output);
         DefaultOutputCommand.MergeUnclaimedPreviousCommandResults = PipelineResultTypes.Error | PipelineResultTypes.Output;

         // Create the default initial session state and add the module.
         InitialSessionState iss = InitialSessionState.CreateDefault();

         var poshModule = System.Reflection.Assembly.GetEntryAssembly();
         string currentUserProfilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WindowsPowerShell");
         string allUsersProfilePath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0");

         
         // Import the PoshWPF module automatically

         if(!Environment.ExpandEnvironmentVariables("%PSModulePath%").Contains( poshModule.Location )) {
            Environment.SetEnvironmentVariable( "PSModulePath", Environment.GetEnvironmentVariable("PSModulePath") +
                                                         ";" + Path.Combine(currentUserProfilePath, "Modules") +
                                                         ";" + Path.Combine(Path.GetDirectoryName(poshModule.Location), "Modules"));
         }
         
         iss.ImportPSModule(new string[] { @"PoshWPF" });
         // We need STA so we can do WPF stuff from our console thread.
         iss.ApartmentState = ApartmentState.STA;
         // We need ReuseThread so that we behave, well, the way that PowerShell.exe and ISE do.
         iss.ThreadOptions = PSThreadOptions.ReuseThread;
         // iss.Variables
         // Load all the Cmdlets that are in this assembly automatically.
         foreach (var t in poshModule.GetTypes())
         {
            var cmdlets = t.GetCustomAttributes(typeof(CmdletAttribute), false) as CmdletAttribute[];

            if (cmdlets != null)
            {
               foreach (var cmdlet in cmdlets)
               {
                  iss.Commands.Add(new SessionStateCmdletEntry(
                                         string.Format("{0}-{1}", cmdlet.VerbName, cmdlet.NounName), t,
                                         string.Format("{0}.xml", t.Name)));
               }
            }
         }
        

         PSObject profile = new PSObject(Path.GetFullPath(Path.Combine(currentUserProfilePath, host.Name + "_profile.ps1")));
         //* %windir%\system32\WindowsPowerShell\v1.0\profile.ps1
         //  This profile applies to all users and all shells.
         profile.Properties.Add(new PSNoteProperty("AllUsersAllHosts", Path.GetFullPath(Path.Combine(allUsersProfilePath, "Profile.ps1"))));
         //* %windir%\system32\WindowsPowerShell\v1.0\PoshConsole_profile.ps1
         //  This profile applies to all users, but only to the Current shell.
         profile.Properties.Add(new PSNoteProperty("AllUsersCurrentHost", Path.GetFullPath(Path.Combine(allUsersProfilePath, host.Name + "_profile.ps1"))));
         //* %UserProfile%\My Documents\WindowsPowerShell\profile.ps1
         //  This profile applies only to the current user, but affects all shells.
         profile.Properties.Add(new PSNoteProperty("CurrentUserAllHosts", Path.GetFullPath(Path.Combine(currentUserProfilePath, "Profile.ps1"))));
         //* %UserProfile%\My Documents\WindowsPowerShell\PoshConsole_profile.ps1
         //  This profile applies only to the current user and the Current shell.
         profile.Properties.Add(new PSNoteProperty("CurrentUserCurrentHost", (string)profile.ImmediateBaseObject));

         iss.Variables.Add( new SessionStateVariableEntry("profile", profile, "The enumeration of all the available profiles the user could edit."));

         _runSpace = RunspaceFactory.CreateRunspace(host, iss);

         /*
            RunspaceConfiguration conf = RunspaceConfiguration.Create();
            conf.InitializationScripts.Append(new ScriptConfigurationEntry("ImportPoshWpf", "$Foo = 'This is foo'")); // Import-Module .\\PoshWPF.dll
            _runSpace = RunspaceFactory.CreateRunspace(host, conf);
         */

         // Set the default runspace, so that event handlers can run in the same runspace as commands.
         // We _could_ hypothetically make this a different runspace, but it would probably cause issues.
         Runspace.DefaultRunspace = _runSpace;

         // we could hypothetically make several threads to do this work...
         _WorkerThread = new Thread(ThreadRun) { Name = "CommandRunner" };
         _WorkerThread.SetApartmentState(ApartmentState.STA);
         _WorkerThread.Start();
      }

      private void StartRunspace()
      {
         /*
                    foreach (var t in System.Reflection.Assembly.GetEntryAssembly().GetTypes())
                    {
                       var cmdlets = t.GetCustomAttributes(typeof(CmdletAttribute), false) as CmdletAttribute[];

                       if (cmdlets != null)
                       {
                          foreach (var cmdlet in cmdlets)
                          {
                             _runSpace.RunspaceConfiguration.Cmdlets.Append(new CmdletConfigurationEntry(
                                                                               string.Format("{0}-{1}", cmdlet.VerbName, cmdlet.NounName), t,
                                                                               string.Format("{0}.xml", t.Name)));
                          }
                       }
                    }

         //// Must compile against v2 for this
         //#if POWERSHELL2
         //           if (_runSpace.Version.Major >= 2)
         //           {
                       _runSpace.ApartmentState = ApartmentState.STA;
                       _runSpace.ThreadOptions = PSThreadOptions.ReuseThread;
         //           }
         //#endif
                    //_runSpace.StateChanged += (sender, e) =>
                    //                             {
                    //                                if (e.RunspaceStateInfo.State == RunspaceState.Opened && RunspaceReady != null)
                    //                                   {
                    //                                      RunspaceReady(sender, e);
                    //                                   }
                    //                             };
          */
         _runSpace.Open();
         ExecuteStartupProfile();
      }

      public void Enqueue(InputBoundCommand command)
      {
         lock (((ICollection)_CommandQueue).SyncRoot)
         {
            _CommandQueue.Enqueue(command);
         }
         _SyncEvents.NewItemEvent.Set();
      }


      /// <summary>
      /// The ThreadStart delegate
      /// </summary>
      public void ThreadRun()
      {
         StartRunspace();
         InputBoundCommand boundCommand;
         //_ExitException = null;

         int sync = WaitHandle.WaitAny(_SyncEvents.NewItemEvents);
         while (sync > 0)
         {
            Trace.WriteLineIf(threadTrace.TraceVerbose, "Signalled. Items in queue: " + _CommandQueue.Count.ToString(), "threading");
            while (_CommandQueue.Count > 0)
            {
               lock (((ICollection)_CommandQueue).SyncRoot)
               {
                  boundCommand = _CommandQueue.Dequeue();
               }

               Pipeline pipeline = _runSpace.CreatePipeline(boundCommand.Commands[0], boundCommand.AddToHistory);
               for (int c = 1; c < boundCommand.Commands.Length; c++)
               {
                  pipeline.Commands.Add(new Command(boundCommand.Commands[c], boundCommand.RunAsScript, boundCommand.UseLocalScope));
               }
               if (boundCommand.DefaultOutput)
               {
                  pipeline.Commands.Add(DefaultOutputCommand);
               }

               // Trace.WriteLineIf(threadTrace.TraceVerbose, "Executing " + pipeline.Commands[0] + "... Items remaining: " + _CommandQueue.Count.ToString(), "threading");

               _Pipeline = pipeline;

               // This is a dynamic anonymous delegate so that it can access the Callback parameter
               _Pipeline.StateChanged += (EventHandler<PipelineStateEventArgs>)delegate(object sender, PipelineStateEventArgs e) // =>
               {
                  Trace.WriteLine("Pipeline is " + e.PipelineStateInfo.State.ToString());

                  if (e.PipelineStateInfo.IsDone())
                  {
                     Trace.WriteLine("Pipeline is Done");

                     Pipeline completed = Interlocked.Exchange(ref _Pipeline, null);
                     if (completed != null)
                     {
                        Exception failure = e.PipelineStateInfo.Reason;

                        if (failure != null)
                        {
                           Debug.WriteLine(failure.GetType(), "PipelineFailure");
                           Debug.WriteLine(failure.Message, "PipelineFailure");
                        }
                        Collection<Object> errors = completed.Error.ReadToEnd();
                        Collection<PSObject> results = completed.Output.ReadToEnd();

                        completed.Dispose();
                        //_SyncEvents.PipelineFinishedEvent.Set();

                        if (boundCommand.Callback != null)
                        {
                           boundCommand.Callback(new PipelineExecutionResult(results, errors, failure, e.PipelineStateInfo.State));
                        }
                     }

                  }
               };

               // I thought that maybe invoke instead of InvokeAsync() would stop the (COM) thread problems
               // it didn't, but it means I don't need the sync, so I might as well leave it...
               try
               {
                  _Pipeline.Invoke(boundCommand.Input);
               }
               catch (Exception ipe)
               {
                  // TODO: Handle IncompleteParseException with some elegance!
                  //    klumsy suggested we could prevent these by using the tokenizer 
                  // Tokenizing in OnEnterPressed (before sending it to the CommandRunner)
                  //    would allow us to let {Enter} be handled nicely ... 
                  // Tokenizing in KeyDown would let us do live syntax highlighting,
                  //    is it fast enough to work?
                  Debug.WriteLine(ipe.Message);
               }
               //catch (ParseException pe)
               //{
               //   // TODO: Handle ParseException with some elegance!
               //}
               //_Pipeline.InvokeAsync();
               //_Pipeline.Input.Write(boundCommand.Input, true);
               //_Pipeline.Input.Close();

               //_SyncEvents.PipelineFinishedEvent.WaitOne();
            }
            Trace.WriteLineIf(threadTrace.TraceVerbose, "Done. No items in Queue.", "threading");
            _SyncEvents.EmptyQueueEvent.Set();
            sync = WaitHandle.WaitAny(_SyncEvents.NewItemEvents);
         }

         if (_runSpace.RunspaceStateInfo.State != RunspaceState.Closing
         && _runSpace.RunspaceStateInfo.State != RunspaceState.Closed)
         {
            _runSpace.Close();
         }

      }

      /// <summary>
      /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
      /// </summary>
      public void Dispose()
      {
         _SyncEvents.ExitThreadEvent.Set();
         _WorkerThread.Join();

         _runSpace.Dispose();
      }

      public void StopPipeline()
      {
         if (_Pipeline != null && _Pipeline.PipelineStateInfo.State == PipelineState.Running)
         {
            _Pipeline.StopAsync();
         }
      }
      public InitialSessionState InitialSessionState
      {
         get { return _runSpace.InitialSessionState; }
      }

      public RunspaceConfiguration RunspaceConfiguration
      {
         get { return _runSpace.RunspaceConfiguration; }
      }

      public RunspaceStateInfo RunspaceStateInfo
      {
         get { return _runSpace.RunspaceStateInfo; }
      }


      /// <summary>
      /// Executes the shutdown profile(s).
      /// </summary>
      internal void ExecuteShutdownProfile(int exitCode)
      {
         //* %windir%\system32\WindowsPowerShell\v1.0\profile_exit.ps1
         //  This profile applies to all users and all shells.
         //* %windir%\system32\WindowsPowerShell\v1.0\PoshConsole_profile_exit.ps1
         //  This profile applies to all users, but only to the PoshConsole shell.
         //* %UserProfile%\My Documents\WindowsPowerShell\profile_exit.ps1
         //  This profile applies only to the current user, but affects all shells.
         //* %UserProfile%\\My Documents\WindowsPowerShell\PoshConsole_profile_exit.ps1
         //  This profile applies only to the current user and the PoshConsole shell.

         // just for the sake of the profiles...
         List<string> existing = new List<string>(4);

         //StringBuilder cmd = new StringBuilder();
         foreach (string path in new[]{
                Path.GetFullPath(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\profile_exit.ps1"))
                ,
                // Put this back if we can get our custom runspace working again.
                // Path.GetFullPath(Path.Combine(Environment.SystemDirectory , @"WindowsPowerShell\v1.0\" + _runSpace.RunspaceConfiguration.ShellId + "_profile_exit.ps1")),
                Path.GetFullPath(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\PoshConsole_profile_exit.ps1"))
                ,
                Path.GetFullPath( Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.MyDocuments), @"WindowsPowerShell\profile_exit.ps1")),
                // Put this back if we can get our custom runspace working again.
                // Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"WindowsPowerShell\" + _runSpace.RunspaceConfiguration.ShellId + "_profile_exit.ps1")),
                Path.GetFullPath( Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.MyDocuments), @"WindowsPowerShell\PoshConsole_profile_exit.ps1")),
             })
         {
            if (File.Exists(path))
            {
               existing.Add(path);
               //cmd.AppendFormat(". \"{0}\";\n", path);
            }
         }

         existing.TrimExcess();
         if (existing.Count > 0)
         {
            Enqueue(new InputBoundCommand(existing.ToArray(), new object[0], result =>
            {
               if (result.Failure != null)
               {
                  // WriteErrorRecord(((RuntimeException)(result.Failure)).ErrorRecord);
               }

               if (ShouldExit != null)
               {
                  ShouldExit(this, exitCode);
               }
            }) { AddToHistory = false, RunAsScript = false });


            //try
            //{
            //    ExecuteHelper(cmd.ToString(), null, false);
            //}
            //catch (RuntimeException rte)
            //{
            //    // An exception occurred that we want to display ...
            //    // We have to run another pipeline, and pass in the error record.
            //    // The runtime will bind the Input to the $Input variable
            //    ExecuteHelper("write-host ($Input | out-string) -fore darkyellow", rte.ErrorRecord, false);
            //}
         }
         else
         {
            if (ShouldExit != null)
            {
               ShouldExit(this, exitCode);
            }
         }


         //else
         //{
         //   ExecutePromptFunction();
         //}
      }

      /// <summary>
      /// Executes the startup profile(s).
      /// </summary>
      public void ExecuteStartupProfile()
      {
         _CommandQueue.Clear();
         
         Enqueue(new InputBoundCommand(new[] { Properties.Resources.Prompt }, new object[0], false, null));

         List<string> existing = new List<string>(4);
         foreach(var profileVariable in InitialSessionState.Variables["profile"]) {
            foreach(var pathProperty in ((PSObject)profileVariable.Value).Properties.Match("*Host*", PSMemberTypes.NoteProperty)) {
               if (File.Exists(pathProperty.Value.ToString()))
               {
                  existing.Add(pathProperty.Value.ToString());
               }
            }
         }
         // This might be nice to have too (in case anyone was using it):
         _runSpace.SessionStateProxy.SetVariable("profiles", existing.ToArray());

         if (existing.Count > 0)
         { 
            existing.TrimExcess();
            Enqueue(new InputBoundCommand(
                        new[] { ". \"" + string.Join("\";. \"", existing.ToArray()) + "\";" },
                        new object[0],
                        false,
                        ignored => RunspaceReady(this, _runSpace.RunspaceStateInfo.State))); // this is super important
         }
         else
         {
            Enqueue(new InputBoundCommand(
                        new string[] { "New-Paragraph" },
                        new object[0],
                        false,
                        ignored => RunspaceReady(this, _runSpace.RunspaceStateInfo.State))); // this is super important
         }
      }

      //existing.Add();

      ////ExecutePipelineOutDefault(, false, result => { });

      //result =>
      //   {
      //      StringBuilder str = new StringBuilder();

      //      foreach (PSObject obj in result.Output)
      //      {
      //         str.Append(obj);
      //      }
      //      // ToDo: write errors from PROMPT the same as we would for a regular command...
      //      //if(result.State == PipelineState.Failed ) {
      //      //   str.Append(result.Failure.Message);
      //      //   str.Append(result.Failure.Message);

      //      _buffer.Prompt(str.ToString());
      //   }



      //if (cmd.Length > 0)
      //{
      //   ExecutePipelineOutDefault(cmd.ToString(), false, result => ExecutePromptFunction(result.State));
      //}
      //else
      //{
      //   ExecutePromptFunction(PipelineState.NotStarted);
      //}
      //}

   }
}

