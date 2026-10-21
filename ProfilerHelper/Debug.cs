using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CSharp.RuntimeBinder;

// debug: drives the Visual Studio that is debugging a step-mode replay (debug_step), one action per run:
//   wait (until the first break), state, over / into / out (Count statements), continue.
// Each action blocks until Visual Studio is in break mode again or the replay ended, then reads where it stopped.
// The message filter retries calls Visual Studio rejects while busy, so reads never come back half-done.
[DataContract]
sealed class DebugState
{
    [DataMember] public string Mode = ""; // Break | Run | Ended
    [DataMember(EmitDefaultValue = false)] public string? BreakReason;
    [DataMember(EmitDefaultValue = false)] public string? Exception;
    [DataMember(EmitDefaultValue = false)] public string? Function;
    [DataMember(EmitDefaultValue = false)] public string? File;
    [DataMember(EmitDefaultValue = false)] public int? Line;
    [DataMember(EmitDefaultValue = false)] public string? Source;
    [DataMember(EmitDefaultValue = false)] public List<Variable>? Locals;
    [DataMember(EmitDefaultValue = false)] public List<Variable>? Evaluated;
    [DataMember(EmitDefaultValue = false)] public List<string>? Stack;
    [DataMember(EmitDefaultValue = false)] public List<StepRecord>? Steps;
}

[DataContract]
sealed class Variable
{
    [DataMember] public string Name = "";
    [DataMember(EmitDefaultValue = false)] public string? Type;
    [DataMember] public string Value = "";
}

[DataContract]
sealed class StepRecord
{
    [DataMember(EmitDefaultValue = false)] public string? Function;
    [DataMember(EmitDefaultValue = false)] public int? Line;
    [DataMember(EmitDefaultValue = false)] public string? Source;
    [DataMember] public List<Variable> Changed = new();
    [DataMember(EmitDefaultValue = false)] public List<Variable>? Evaluated;
    [DataMember] public long At; // when the step ended; the server files the replay's time-stamped output under it, then drops it
}

static partial class Program
{
    // dbgEventReason, 1-based.
    static readonly string[] BreakReasons = { "", "None", "Go", "AttachProgram", "DetachProgram", "LaunchProgram", "EndProgram", "StopDebugging",
        "Step", "Breakpoint", "ExceptionThrown", "ExceptionNotHandled", "UserBreak", "ContextSwitch" };

    static DebugState Debug(Request request) => OnSta(() => DebugFromSta(request));

    static DebugState DebugFromSta(Request r)
    {
        var deadline = DateTime.UtcNow.AddSeconds(r.TimeoutSeconds > 0 ? r.TimeoutSeconds : 60);
        dynamic? dte;
        // The replay attaches Visual Studio itself; "wait" covers the time until it has.
        while ((dte = VisualStudioDebugging(r.DebuggeePid)) == null)
        {
            if (Exited(r.DebuggeePid)) return new DebugState { Mode = "Ended" };
            if (r.Action != "wait" || DateTime.UtcNow > deadline) throw new InvalidOperationException($"No Visual Studio is debugging the replay (process {r.DebuggeePid}).");
            Thread.Sleep(200);
        }
        var debugger = dte.Debugger;
        List<StepRecord>? steps = null;
        switch (r.Action)
        {
            case "wait":
                while (Mode(debugger, r.DebuggeePid) == "Run" && DateTime.UtcNow < deadline) Thread.Sleep(200);
                break;
            case "state":
                break;
            case "continue":
                RequireBreak(debugger, r.DebuggeePid);
                debugger.Go(true);
                break;
            case "over" or "into" or "out":
                RequireBreak(debugger, r.DebuggeePid);
                steps = new List<StepRecord>();
                List<Variable> before = Locals(debugger, Frame(debugger));
                for (int i = 0; i < Math.Min(Math.Max(r.Count, 1), 100); i++)
                {
                    if (r.Action == "over") debugger.StepOver(true);
                    else if (r.Action == "into") debugger.StepInto(true);
                    else debugger.StepOut(true);
                    var at = Now();
                    if (Mode(debugger, r.DebuggeePid) != "Break") break;
                    var frame = Frame(debugger);
                    var (file, line) = Position((object)dte, (object)frame); // object: a dynamic argument would make the result dynamic, which can't be deconstructed
                    List<Variable> now = Locals(debugger, frame);
                    steps.Add(new StepRecord
                    {
                        Function = (string)frame.FunctionName, Line = line, Source = SourceLine(file, line),
                        Changed = now.Where(v => !before.Any(b => b.Name == v.Name && b.Value == v.Value)).ToList(),
                        Evaluated = Evaluated(debugger, r), At = at,
                    });
                    before = now;
                }
                if (steps.Count <= 1) steps = null; // a single step is the final state itself
                break;
            default:
                throw new ArgumentException($"Unknown action '{r.Action}': wait | state | over | into | out | continue.");
        }
        return Snapshot(dte, r, steps);
    }

    static DebugState Snapshot(dynamic dte, Request r, List<StepRecord>? steps)
    {
        var debugger = dte.Debugger;
        var state = new DebugState { Mode = Mode(debugger, r.DebuggeePid), Steps = steps };
        if (state.Mode != "Break") return state;
        int reason = debugger.LastBreakReason;
        state.BreakReason = reason > 0 && reason < BreakReasons.Length ? BreakReasons[reason] : reason.ToString();
        if (reason is 10 or 11) state.Exception = Evaluate(debugger, "$exception.GetType().FullName + \": \" + $exception.Message");
        var frame = Frame(debugger);
        state.Function = frame.FunctionName;
        (state.File, state.Line) = Position((object)dte, (object)frame);
        state.Source = SourceLine(state.File, state.Line);
        state.Locals = Locals(debugger, frame);
        state.Evaluated = Evaluated(debugger, r);
        state.Stack = new List<string>();
        var frames = debugger.CurrentThread.StackFrames;
        for (int i = 1; i <= Math.Min((int)frames.Count, 10); i++) state.Stack.Add((string)frames.Item(i).FunctionName);
        return state;
    }

    static string Mode(dynamic debugger, int pid) => Exited(pid) ? "Ended" : (int)debugger.CurrentMode switch { 2 => "Break", 1 => "Ended", _ => "Run" };

    static void RequireBreak(dynamic debugger, int pid)
    {
        var mode = Mode(debugger, pid);
        if (mode != "Break") throw new InvalidOperationException($"The replay isn't stopped at a breakpoint (mode {mode}).");
    }

    // Right after a step Visual Studio can still be building the frame: give it 2 s.
    static dynamic Frame(dynamic debugger)
    {
        for (int i = 0; ; i++)
        {
            var frame = debugger.CurrentStackFrame;
            if (frame != null || i == 20) return frame ?? throw new InvalidOperationException("Visual Studio has no current stack frame.");
            Thread.Sleep(100);
        }
    }

    // StackFrame2 has the source position; if late binding can't reach it, the editor caret, which Visual Studio moves to the current statement.
    static (string? File, int? Line) Position(dynamic dte, dynamic frame)
    {
        try { return ((string)frame.FileName, (int)frame.LineNumber); }
        catch (Exception e) when (e is RuntimeBinderException or COMException) { }
        try { return ((string)dte.ActiveDocument.FullName, (int)dte.ActiveDocument.Selection.CurrentLine); }
        catch (Exception e) when (e is RuntimeBinderException or COMException) { return (null, null); }
    }

    static string? SourceLine(string? file, int? line) =>
        file != null && line > 0 && System.IO.File.Exists(file) ? System.IO.File.ReadLines(file).Skip(line.Value - 1).FirstOrDefault()?.Trim() : null;

    static List<Variable> Locals(dynamic debugger, dynamic frame)
    {
        var list = new List<Variable>();
        var locals = frame.Locals;
        for (int i = 1; i <= (int)locals.Count; i++)
        {
            var local = locals.Item(i);
            string name = local.Name, type = local.Type, value = local.Value;
            // An Entity shows as "{Microsoft.Xrm.Sdk.Entity}": show its record and attribute values instead (ProfilerHelperInspect, Replay.cs, runs in the replay).
            // Only for real variables: Visual Studio also lists return values ("...this[TKey].get returned"), which aren't expressions.
            if (type.Split(' ')[0] == "Microsoft.Xrm.Sdk.Entity" && value != "null" && Regex.IsMatch(name, @"^[A-Za-z_]\w*$"))
                value = Evaluate(debugger, $"global::ProfilerHelperInspect.Entity({name})");
            list.Add(new Variable { Name = name, Type = type, Value = Clip(value) });
        }
        return list;
    }

    static List<Variable>? Evaluated(dynamic debugger, Request r) =>
        r.Expressions is { Length: > 0 } ? r.Expressions.Select(e => new Variable { Name = e, Value = Evaluate(debugger, e) }).ToList() : null;

    static string Evaluate(dynamic debugger, string expression)
    {
        var x = debugger.GetExpression(expression, false, 2000);
        string value = x.Value;
        return Clip((bool)x.IsValidValue ? value : "error: " + value);
    }

    static string Clip(string s) => s.Length > 1000 ? s.Substring(0, 1000) + "..." : s;

    static dynamic? VisualStudioDebugging(int pid)
    {
        foreach (var (_, dte) in RunningVisualStudios())
        {
            try
            {
                var processes = dte.Debugger.DebuggedProcesses;
                for (int i = 1; i <= (int)processes.Count; i++)
                    if ((int)processes.Item(i).ProcessID == pid) return dte;
            }
            catch (COMException) { } // an instance that is shutting down
        }
        return null;
    }

    static bool Exited(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.HasExited; }
        catch (ArgumentException) { return true; }
    }
}
